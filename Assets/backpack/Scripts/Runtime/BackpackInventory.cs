using System;
using System.Collections.Generic;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Runtime
{
    /// <summary>单机背包宿主：挂在玩家或箱子物体上，组合一份 <see cref="InventoryHostCore"/> 作为权威数据（容器、意图命令与编排逻辑都在核心内），只承担「创建核心 + 登记注册 + 测试辅助」；物品读取由调用方直连核心容器，变更通知经容器的 <c>InvChannels</c> 广播、写入意图经 <c>CmdChannels</c> 命令处理，宿主不再中转或转发。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/Backpack Inventory")]
    public sealed class BackpackInventory : MonoBehaviour, IBackpackView
    {
        public const int DefaultRows = InventoryHostCore.DefaultRows; // 尺寸非法时的回退行数（与联机组件默认一致）。

        public const int DefaultColumns = InventoryHostCore.DefaultColumns; // 尺寸非法时的回退列数（与联机组件默认一致）。

        [Header("背包尺寸（创建后不可变更）")]
        [SerializeField, Min(1)]
        private int rows = DefaultRows;

        [SerializeField, Min(1)]
        private int columns = DefaultColumns;

        [Header("归属")]
        [Tooltip("勾选后登记为 BackpackRegistry.Primary（本地玩家背包）；箱子等其它容器应取消勾选。")]
        [SerializeField]
        private bool isLocalPlayerInventory = true;

        [Header("物品目录（拆分恢复新实例用）")]
        [Tooltip("拆分按类型取模板必需；留空时拆分会被拒绝并记警告。")]
        [SerializeField]
        private ItemManifestCatalog catalog;

        [Header("测试")]
        [Tooltip("勾选后 Start 时把目录中的模板各填入一份测试堆叠（仅调试用，正式场景请取消勾选）。")]
        [SerializeField]
        private bool fillCatalogOnStart;

        private const int TestStackCount = 5; // 测试填入时每个可堆叠模板的堆叠数（仍受其单堆上限约束）。

        private readonly List<RuntimeItem> testItems = new List<RuntimeItem>(); // 测试遍历用的复用缓冲，避免每次调用分配。

        private InventoryHostCore core; // 共享核心：容器、意图命令与编排逻辑（单机自任权威）。

        public InventoryContainer Container => core != null ? core.Container : null; // 权威容器；Awake 创建后不变。

        public int ConfiguredRows => rows; // 面板配置的行数。

        public int ConfiguredColumns => columns; // 面板配置的列数。

        public bool IsLocalPlayerInventory => isLocalPlayerInventory; // 是否登记为本地玩家背包。

        public object EventSource => Container; // 事件管线键：单机形态恒为权威容器实例（订阅通知与发布意图共用）。

        /// <summary>物品目录：可在运行时注入（例如场景手搭后再赋值），拆分前必须能按类型取到模板。</summary>
        public ItemManifestCatalog Catalog
        {
            get => catalog;
            set
            {
                catalog = value;
                if (core != null) core.Catalog = value;
            }
        }

        // 生命周期

        /// <summary>创建共享核心（内部创建权威容器，行列非法或乘积超限时回退默认尺寸并记警告），并把 Inspector 目录同步给核心。</summary>
        private void Awake()
        {
            core = new InventoryHostCore(rows, columns, LogModule.Inventory, name);
            core.Catalog = catalog;

            BackpackLog.Info(
                LogModule.Inventory,
                $"背包宿主就绪：{name}，容器 {core.Container.Rows} × {core.Container.Columns}，本地玩家包={isLocalPlayerInventory}。");
        }

        /// <summary>登记到注册表，并以容器为管线键订阅三个写入意图命令；核心未创建时只登记不订阅。</summary>
        private void OnEnable()
        {
            BackpackRegistry.Register(this);
            if (core != null) core.SubscribeCommands(core.Container);
        }

        /// <summary>注销登记并释放全部意图订阅（与 OnEnable 成对，重复调用安全）。</summary>
        private void OnDisable()
        {
            BackpackRegistry.Unregister(this);
            if (core != null) core.UnsubscribeCommands();
        }

        /// <summary>测试用自动填充：放在 Start 以保证容器已创建、注册已完成。</summary>
        private void Start()
        {
            if (fillCatalogOnStart) FillFromCatalogForTest();
        }

        // 视图契约（物品读取；其余查询与变更请直连 Container）

        /// <summary>按 ItemId 升序复制当前物品（确定性顺序，先清空目标列表），供界面、存档与日志复用；容器未创建时清空并返回。</summary>
        public void CopyItems(List<RuntimeItem> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            InventoryContainer container = Container;
            if (container == null)
            {
                destination.Clear();
                return;
            }

            container.FillItemsByItemId(destination);
        }

        // 写入编排（转发共享核心；其余写入请直连 Container）

        /// <summary>按模板创建一件新物品并整批收进背包：分配标识、克隆模板并执行首次随机化、先并同类堆叠再落首个空白区域；任一步失败整体失败且不留下半成品。</summary>
        public bool TryAddItem(ItemManifest manifest, int stackCount, out RuntimeItem item, out string error)
        {
            item = null;
            if (core == null)
            {
                error = "背包尚未初始化（组件未执行 Awake）。";
                BackpackLog.Warn(LogModule.Inventory, error);
                return false;
            }

            return core.TryAddItem(manifest, stackCount, out item, out error);
        }

        /// <summary>从指定堆叠拆出独立实例（核心自任权威）：核对归属、可堆叠与数量边界、按类型目录取模板、抓源快照并以新标识恢复一份数值原样继承的新实例、落到行优先扫描出的首个空白区域；任一步失败整体零变更。</summary>
        public bool TrySplitStack(RuntimeItem source, int splitCount, out ItemId newItemId, out string error)
        {
            newItemId = ItemId.None;
            if (core == null)
            {
                error = "背包尚未初始化（组件未执行 Awake）。";
                BackpackLog.Warn(LogModule.Inventory, error);
                return false;
            }

            return core.TrySplitStack(source, splitCount, out newItemId, out error);
        }

        // 内部实现

        /// <summary>容器可用性守卫（仅测试辅助使用；正式写入的守卫在核心内）。</summary>
        private bool EnsureContainer(out string error)
        {
            if (Container != null)
            {
                error = null;
                return true;
            }

            error = "背包尚未初始化（组件未执行 Awake）。";
            BackpackLog.Warn(LogModule.Inventory, error);
            return false;
        }

        // 测试辅助（行为与正式调用完全一致，只是把「目录 → 背包」的取证过程自动化；Inspector 组件右键菜单触发）

        /// <summary>从目录把每个模板各填入一份测试堆叠（可堆叠物品取 TestStackCount 件、受单堆上限约束）；逐件失败只记警告并继续，用于界面与拆分的冒烟。</summary>
        [ContextMenu("测试/从目录填入物品")]
        private void FillFromCatalogForTest()
        {
            if (!EnsureContainer(out string error)) return;

            if (catalog == null)
            {
                BackpackLog.Warn(LogModule.Inventory, $"{name} 未配置物品目录（catalog），无法填入测试物品。");
                return;
            }

            IReadOnlyList<ItemManifest> manifests = catalog.Manifests;
            if (manifests.Count == 0)
            {
                BackpackLog.Warn(LogModule.Inventory, $"{name} 的物品目录为空，没有可填入的模板。");
                return;
            }

            int added = 0;
            for (int i = 0; i < manifests.Count; i++)
            {
                ItemManifest manifest = manifests[i];
                if (manifest == null) continue;

                StackableFragment stackable = manifest.GetFragment<StackableFragment>();
                int stackCount = stackable == null ? 1 : Mathf.Min(TestStackCount, stackable.MaxStackSize);

                if (TryAddItem(manifest, stackCount, out _, out error)) added++;
                else BackpackLog.Warn(LogModule.Inventory, $"{name} 测试填入 {manifest.ItemType} 失败：{error}");
            }

            BackpackLog.Info(
                LogModule.Inventory,
                $"{name} 测试填入完成：成功 {added}/{manifests.Count}，当前物品 {Container.ItemCount} 件。");
        }

        /// <summary>移除背包内全部物品（测试用）；逐件失败只记警告并继续。</summary>
        [ContextMenu("测试/清空背包")]
        private void ClearForTest()
        {
            if (!EnsureContainer(out string error)) return;

            testItems.Clear();
            Container.FillItemsByItemId(testItems);

            int removed = 0;
            for (int i = 0; i < testItems.Count; i++)
            {
                RuntimeItem item = testItems[i];
                if (Container.TryRemoveItem(item, out error)) removed++;
                else BackpackLog.Warn(LogModule.Inventory, $"{name} 测试清空 {item.ItemType} 失败：{error}");
            }

            BackpackLog.Info(
                LogModule.Inventory,
                $"{name} 测试清空完成：移除 {removed} 件，剩余 {Container.ItemCount} 件。");
        }

        /// <summary>把首个堆叠数 ≥ 2 的物品拆出一半（测试用），用于核对「目录取模板 + 快照恢复 + 空白落位」全链路。</summary>
        [ContextMenu("测试/拆分首个可堆叠物品")]
        private void SplitFirstStackableForTest()
        {
            if (!EnsureContainer(out string error)) return;

            testItems.Clear();
            Container.FillItemsByItemId(testItems);

            for (int i = 0; i < testItems.Count; i++)
            {
                RuntimeItem item = testItems[i];
                if (!item.IsStackable || item.StackCount < 2) continue;

                int splitCount = item.StackCount / 2;
                if (TrySplitStack(item, splitCount, out ItemId newItemId, out error))
                {
                    BackpackLog.Info(
                        LogModule.Inventory,
                        $"{name} 测试拆分成功：{item.ItemType} 拆出 {splitCount} 件（新标识 {newItemId}），源剩 {item.StackCount} 件。");
                }
                else
                {
                    BackpackLog.Warn(LogModule.Inventory, $"{name} 测试拆分失败：{error}");
                }

                return;
            }

            BackpackLog.Warn(LogModule.Inventory, $"{name} 没有可拆分的堆叠（需堆叠数 ≥ 2），请先填入测试物品。");
        }

        /// <summary>按 ItemId 升序把当前背包内容打印到控制台（测试用）。</summary>
        [ContextMenu("测试/打印背包内容")]
        private void LogContentsForTest()
        {
            if (!EnsureContainer(out string error)) return;

            testItems.Clear();
            Container.FillItemsByItemId(testItems);

            BackpackLog.Info(
                LogModule.Inventory,
                $"{name} 背包内容：物品 {Container.ItemCount} 件 / 槽位 {Container.SlotCount} 格。");

            for (int i = 0; i < testItems.Count; i++)
            {
                RuntimeItem item = testItems[i];
                Container.TryGetPlacement(item, out InventoryPlacement placement);
                BackpackLog.Info(
                    LogModule.Inventory,
                    $"  [{i}] {item.ItemType} × {item.StackCount}，占格 {placement.Dimensions}，上左索引 {placement.UpperLeftIndex}。");
            }
        }
    }
}
