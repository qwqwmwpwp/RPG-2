using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>权威提交后的确认回调（联机 = 投影同步）：返回 false 表示外部表示未能跟随本次提交，核心按各操作的回滚口径撤销变更，并再执行一次补偿确认（requiredItem 传 None）。</summary>
    public delegate bool CommitConfirmHandler(ItemId requiredItem, out string error);

    /// <summary>背包宿主共享核心：单机与联机宿主各持一份，容器创建、意图命令分发、物品编排（新增 / 设置堆叠 / 移除 / 拆分）只在本类实现；权威端（自己为主端）直接复核执行，非权威端把意图交给宿主注入的 Forward* 委托转发，结果一律由容器广播回传。</summary>
    public sealed class InventoryHostCore
    {
        public const int DefaultRows = 6; // 尺寸非法时的回退行数（与联机组件默认一致）。
        public const int DefaultColumns = 8; // 尺寸非法时的回退列数（与联机组件默认一致）。

        private readonly LogModule logModule; // 日志模块位（单机 = Inventory，联机 = Network）。
        private readonly string ownerName; // 日志里的宿主名（组件名或兜底名）。

        private static readonly ItemIdGenerator SharedGenerator = new ItemIdGenerator(); // 未注入提供者时共用的标识生成器，避免单机玩家与箱子分别从 1 发号而在转移后重号。

        private EventSubscription setStackCountSubscription; // 意图订阅句柄：设置堆叠数。
        private EventSubscription removeItemSubscription; // 意图订阅句柄：移除物品。
        private EventSubscription splitStackSubscription; // 意图订阅句柄：拆分堆叠。

        /// <summary>非权威端（纯客户端）构造：不创建容器，只承载意图订阅与转发。</summary>
        public InventoryHostCore(LogModule logModule, string ownerName)
        {
            this.logModule = logModule;
            this.ownerName = string.IsNullOrEmpty(ownerName) ? "背包" : ownerName;
        }

        /// <summary>权威端构造：创建指定行列的容器；行列非正或乘积超过上限时回退默认尺寸并记警告。</summary>
        public InventoryHostCore(int rows, int columns, LogModule logModule, string ownerName)
            : this(logModule, ownerName)
        {
            int safeRows = rows;
            int safeColumns = columns;
            if (safeRows < 1 || safeColumns < 1 || (long)safeRows * safeColumns > InventoryContainer.MaxSlotCount)
            {
                BackpackLog.Warn(
                    logModule,
                    $"{this.ownerName} 的背包尺寸配置非法（{rows} × {columns}），已回退为 {DefaultRows} × {DefaultColumns}。");
                safeRows = DefaultRows;
                safeColumns = DefaultColumns;
            }

            Container = new InventoryContainer(safeRows, safeColumns);
        }

        public InventoryContainer Container { get; } // 权威容器；纯客户端为 null。

        public ItemManifestCatalog Catalog { get; set; } // 新增 / 拆分按类型取模板的目录；留空时拆分被拒。

        public Func<ItemIdGenerator> IdGeneratorProvider { get; set; } // 标识生成器提供者（联机注入服务端生成器）；留空时使用共享默认生成器。

        public Func<bool> IsAuthority { get; set; } // 自己是否为主端；留空恒为 true。

        public Action<ItemId, int> ForwardSetStackCount { get; set; } // 非权威端转发：设置堆叠数。

        public Action<ItemId> ForwardRemoveItem { get; set; } // 非权威端转发：移除物品。

        public Action<ItemId, int> ForwardSplitStack { get; set; } // 非权威端转发：拆分堆叠。

        public CommitConfirmHandler CommitConfirm { get; set; } // 权威提交后的确认钩子；留空表示无需确认。

        // 意图命令（source = 宿主的事件管线键；只记警告不抛异常，结果由 InvChannels 广播回传）

        /// <summary>以指定管线键（权威端 = 容器，纯客户端 = 宿主自身）订阅三个写入意图频道；重复调用先退订再订阅，避免条目累积。</summary>
        public void SubscribeCommands(object sourceKey)
        {
            UnsubscribeCommands();

            if (sourceKey == null)
            {
                BackpackLog.Warn(logModule, $"{ownerName} 的事件管线键为空，意图命令不会生效。");
                return;
            }

            setStackCountSubscription = EventBus.Global.Subscribe<ItemId, int>(
                CmdChannels.SetStackCount, sourceKey, this, OnSetStackCountCommand);
            removeItemSubscription = EventBus.Global.Subscribe<ItemId>(
                CmdChannels.RemoveItem, sourceKey, this, OnRemoveItemCommand);
            splitStackSubscription = EventBus.Global.Subscribe<ItemId, int>(
                CmdChannels.SplitStack, sourceKey, this, OnSplitStackCommand);
        }

        /// <summary>释放全部意图订阅（幂等）。</summary>
        public void UnsubscribeCommands()
        {
            setStackCountSubscription.Dispose();
            removeItemSubscription.Dispose();
            splitStackSubscription.Dispose();
            setStackCountSubscription = default;
            removeItemSubscription = default;
            splitStackSubscription = default;
        }

        // 权威操作（任一步失败零变更；提交后确认失败按各操作口径回滚）

        /// <summary>按标识查找权威容器内物品（线性扫描，仅低频操作调用）；无容器或标识无效返回 false。</summary>
        public bool TryGetItem(ItemId itemId, out RuntimeItem item)
        {
            item = null;
            return Container != null && Container.TryGetItemById(itemId, out item);
        }

        /// <summary>按模板创建一件新物品并整批收进容器：分配标识、克隆模板并执行首次随机化、先并同类堆叠再落首个空白区域；提交后确认失败时回滚本次新增并补偿确认，任一步失败整体失败且不留下半成品。</summary>
        public bool TryAddItem(ItemManifest manifest, int stackCount, out RuntimeItem item, out string error)
        {
            item = null;
            if (!EnsureContainer(out error)) return false;
            if (manifest == null) return Reject("新增物品", "物品模板为空。", out error);

            ItemIdGenerator generator = ResolveGenerator();
            if (generator == null) return Reject("新增物品", "物品标识生成器不可用。", out error);

            ItemId issued = generator.Next();
            if (!manifest.TryCreateRuntimeItem(issued, out item, out error, stackCount))
            {
                return false;
            }

            if (!Container.TryAddItem(item, out _, out error))
            {
                item = null;
                return false;
            }

            // 全部并入已有堆叠时新标识没有成为库存条目，确认整体同步即可。
            ItemId requiredItem = Container.Contains(item) ? issued : ItemId.None;
            if (CommitConfirm != null && !CommitConfirm(requiredItem, out error))
            {
                Container.TryRemoveItem(item, out _);
                CommitConfirm(ItemId.None, out _);
                item = null;
                return false;
            }

            return true;
        }

        /// <summary>修改容器内物品的堆叠数：委托容器做归属与 1..单堆上限校验（数量未变化时不提交）；提交后确认失败时回滚原值并补偿确认。</summary>
        public bool TrySetStackCount(ItemId itemId, int stackCount, out string error)
        {
            if (!TryResolveItem(itemId, out RuntimeItem item, out error)) return false;

            int previousStack = item.StackCount;
            if (!Container.TrySetStackCount(item, stackCount, out error)) return false;

            if (CommitConfirm != null && !CommitConfirm(item.ItemId, out error))
            {
                item.SetStackCount(previousStack);
                CommitConfirm(ItemId.None, out _);
                return false;
            }

            return true;
        }

        /// <summary>从容器移除物品并释放其全部占格；容器拒绝时原因外露，提交后确认失败直接判失败（移除回滚会改变占格，按原口径不回滚）。</summary>
        public bool TryRemoveItem(ItemId itemId, out string error)
        {
            if (!TryResolveItem(itemId, out RuntimeItem item, out error)) return false;
            if (!Container.TryRemoveItem(item, out error)) return false;
            if (CommitConfirm != null && !CommitConfirm(ItemId.None, out error)) return false;
            return true;
        }

        /// <summary>从指定堆叠拆出独立实例：核对归属、可堆叠与数量边界、按类型目录取模板、抓源快照并以新标识恢复数值原样继承的新实例、落到行优先扫描出的首个空白区域；提交后确认失败时回滚新实例与源堆叠并补偿确认，任一步失败整体零变更。</summary>
        public bool TrySplitStack(RuntimeItem source, int splitCount, out ItemId newItemId, out string error)
        {
            newItemId = ItemId.None;
            if (source == null) return Reject("拆分堆叠", "物品为空。", out error);
            return TrySplitStack(source.ItemId, splitCount, out newItemId, out error);
        }

        /// <summary>按标识定位后拆分（语义同上）。</summary>
        public bool TrySplitStack(ItemId itemId, int splitCount, out ItemId newItemId, out string error)
        {
            newItemId = ItemId.None;
            if (!TryResolveItem(itemId, out RuntimeItem source, out error)) return false;

            if (!source.IsStackable)
            {
                return Reject("拆分堆叠", $"{source.ItemType} 不可堆叠，无法拆分。", out error);
            }

            if (splitCount < 1 || splitCount >= source.StackCount)
            {
                return Reject(
                    "拆分堆叠",
                    $"拆分数量 {splitCount} 必须在 1..{source.StackCount - 1} 范围内（上限为堆叠数减一）。",
                    out error);
            }

            if (Catalog == null || !Catalog.TryGet(source.ItemType, out ItemManifest manifest) || manifest == null)
            {
                return Reject("拆分堆叠", $"类型目录中缺少 {source.ItemType} 的模板，无法创建拆分出的新实例。", out error);
            }

            if (!RuntimeItemSnapshotCodec.TryCapture(source, out RuntimeItemSnapshot snapshot, out string captureError))
            {
                return Reject("拆分堆叠", captureError, out error);
            }

            ItemIdGenerator generator = ResolveGenerator();
            if (generator == null) return Reject("拆分堆叠", "物品标识生成器不可用。", out error);

            ItemId issued = generator.Next();
            if (!TryBuildSplitInstance(manifest, snapshot, issued, splitCount, out RuntimeItem created, out string buildError))
            {
                return Reject("拆分堆叠", buildError, out error);
            }

            GridCoord dimensions = InventoryContainer.GetItemDimensions(created);
            int placeIndex = FindFirstFreeRegion(Container, dimensions);
            if (placeIndex == InventoryContainer.NoIndex)
            {
                return Reject("拆分堆叠", $"没有能容纳 {dimensions} 拆分结果的空白区域。", out error);
            }

            int previousStack = source.StackCount;
            source.SetStackCount(previousStack - splitCount);
            if (!Container.TryPlaceItem(created, placeIndex, out error))
            {
                source.SetStackCount(previousStack);
                return false;
            }

            if (CommitConfirm != null && !CommitConfirm(issued, out error))
            {
                Container.TryRemoveItem(created, out _);
                source.SetStackCount(previousStack);
                CommitConfirm(ItemId.None, out _);
                return false;
            }

            newItemId = issued;
            error = null;
            return true;
        }

        // 意图分发（自己为主端直接执行，否则交给宿主转发）

        /// <summary>处理「设置堆叠数」意图。</summary>
        private void OnSetStackCountCommand(ItemId itemId, int stackCount)
        {
            if (ResolveAuthority())
            {
                TrySetStackCount(itemId, stackCount, out _);
                return;
            }

            if (ForwardSetStackCount != null) ForwardSetStackCount(itemId, stackCount);
            else BackpackLog.Warn(logModule, $"{ownerName} 不是权威端且未接线设置堆叠数转发，意图已丢弃。");
        }

        /// <summary>处理「移除物品」意图。</summary>
        private void OnRemoveItemCommand(ItemId itemId)
        {
            if (ResolveAuthority())
            {
                TryRemoveItem(itemId, out _);
                return;
            }

            if (ForwardRemoveItem != null) ForwardRemoveItem(itemId);
            else BackpackLog.Warn(logModule, $"{ownerName} 不是权威端且未接线移除物品转发，意图已丢弃。");
        }

        /// <summary>处理「拆分堆叠」意图。</summary>
        private void OnSplitStackCommand(ItemId itemId, int splitCount)
        {
            if (ResolveAuthority())
            {
                TrySplitStack(itemId, splitCount, out _, out _);
                return;
            }

            if (ForwardSplitStack != null) ForwardSplitStack(itemId, splitCount);
            else BackpackLog.Warn(logModule, $"{ownerName} 不是权威端且未接线拆分堆叠转发，意图已丢弃。");
        }

        /// <summary>将指定数量拆成新实例并直接放入同一或另一容器的空位；源容器无需额外空位，数值继承源快照，确认失败时恢复双方状态。</summary>
        public bool TrySplitStackToEmptySpace(RuntimeItem source, int splitCount, InventoryContainer destination, out ItemId newItemId, out string error, CommitConfirmHandler destinationConfirm = null)
        {
            newItemId = ItemId.None;
            if (!ResolveAuthority()) return Reject("拆分堆叠", "只有权威端可以拆分库存物品。", out error);
            if (!EnsureContainer(out error)) return false;
            if (source == null || !Container.Contains(source)) return Reject("拆分堆叠", "物品不在来源容器中。", out error);
            if (destination == null) return Reject("拆分堆叠", "目标容器为空。", out error);
            if (!source.IsStackable) return Reject("拆分堆叠", $"{source.ItemType} 不可堆叠，无法拆分。", out error);
            if (splitCount < 1 || splitCount >= source.StackCount)
                return Reject("拆分堆叠", $"拆分数量 {splitCount} 必须在 1..{source.StackCount - 1} 范围内。", out error);
            if (Catalog == null || !Catalog.TryGet(source.ItemType, out ItemManifest manifest) || manifest == null)
                return Reject("拆分堆叠", $"类型目录中缺少 {source.ItemType} 的模板，无法创建拆分出的新实例。", out error);
            if (!RuntimeItemSnapshotCodec.TryCapture(source, out RuntimeItemSnapshot snapshot, out string captureError))
                return Reject("拆分堆叠", captureError, out error);

            ItemIdGenerator generator = ResolveGenerator();
            if (generator == null) return Reject("拆分堆叠", "物品标识生成器不可用。", out error);
            ItemId issued;
            do issued = generator.Next();
            while (Container.TryGetItemById(issued, out _) || destination.TryGetItemById(issued, out _));

            if (!TryBuildSplitInstance(manifest, snapshot, issued, splitCount, out RuntimeItem created, out string buildError))
                return Reject("拆分堆叠", buildError, out error);
            if (!Container.TrySplitStackToEmptySpace(source, created, destination, CommitConfirm, destinationConfirm, out error)) return false;

            newItemId = issued;
            return true;
        }

        // 内部实现

        /// <summary>自己是否为主端。</summary>
        private bool ResolveAuthority() => IsAuthority == null || IsAuthority();

        /// <summary>容器可用性守卫：权威容器尚未创建时拒绝并说明原因。</summary>
        private bool EnsureContainer(out string error)
        {
            if (Container != null)
            {
                error = null;
                return true;
            }

            return Reject("背包操作", "背包尚未初始化（容器尚未创建）。", out error);
        }

        /// <summary>按标识解析容器内物品；失败时记录并返回拒绝原因。</summary>
        private bool TryResolveItem(ItemId itemId, out RuntimeItem item, out string error)
        {
            item = null;
            if (!EnsureContainer(out error)) return false;

            if (!Container.TryGetItemById(itemId, out item))
            {
                return Reject("背包操作", $"背包中不存在物品 {itemId}。", out error);
            }

            return true;
        }

        /// <summary>取标识生成器：优先用注入的提供者（联机 = 服务端生成器），留空时共用默认生成器（单机自任权威）。</summary>
        private ItemIdGenerator ResolveGenerator()
        {
            if (IdGeneratorProvider != null) return IdGeneratorProvider();
            return SharedGenerator;
        }

        /// <summary>按「源快照 + 新标识 + 拆分数量」恢复一份新实例（数值原样继承、不重新随机）。</summary>
        private static bool TryBuildSplitInstance(
            ItemManifest manifest,
            RuntimeItemSnapshot snapshot,
            ItemId id,
            int stackCount,
            out RuntimeItem item,
            out string error)
        {
            IReadOnlyList<FragmentNumberState> numbers = snapshot.Numbers;
            var copy = new FragmentNumberState[numbers.Count];
            for (int i = 0; i < numbers.Count; i++) copy[i] = numbers[i];

            var splitSnapshot = new RuntimeItemSnapshot(id, snapshot.ItemType, stackCount, copy);
            return manifest.TryRestoreRuntimeItem(splitSnapshot, out item, out error);
        }

        /// <summary>行优先扫描首个能完整容纳指定尺寸的空白区域，返回其上左索引；没有则返回 NoIndex。</summary>
        private static int FindFirstFreeRegion(InventoryContainer container, GridCoord dimensions)
        {
            for (int index = 0; index < container.SlotCount; index++)
            {
                if (container.QuerySpace(index, dimensions).HasSpace) return index;
            }

            return InventoryContainer.NoIndex;
        }

        /// <summary>统一拒绝路径：拼装文案（"{操作} 被拒绝：{原因}"）、记一条警告、输出原因。</summary>
        private bool Reject(string operation, string reason, out string error)
        {
            error = $"{operation} 被拒绝：{reason}";
            BackpackLog.Warn(logModule, $"{ownerName}：{error}");
            return false;
        }
    }
}
