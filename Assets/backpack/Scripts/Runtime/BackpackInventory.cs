using System;
using System.Collections.Generic;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Runtime
{
    /// <summary>基础背包组件：组合一份 <see cref="InventoryHostCore"/>，统一暴露物品集合、格子映射、空间查询与写入操作；全部状态和提交逻辑委托同一核心与容器，变更通知经容器的 <c>InvChannels</c> 广播、写入意图经 <c>CmdChannels</c> 命令处理；角色归属由组合组件承担。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/Backpack Inventory")]
    public sealed class BackpackInventory : MonoBehaviour, IBackpackView, IInventoryOperations
    {
        #region 常量

        public const int DefaultRows = InventoryHostCore.DefaultRows; // 尺寸非法时的回退行数（与联机组件默认一致）。

        public const int DefaultColumns = InventoryHostCore.DefaultColumns; // 尺寸非法时的回退列数（与联机组件默认一致）。

        public const int NoIndex = InventoryContainer.NoIndex; // 空格与无效下标。

        public const int MaxSlotCount = InventoryContainer.MaxSlotCount; // 槽位总数上限。

        #endregion

        #region Inspector 配置

        [Header("背包尺寸（创建后不可变更）")]
        [SerializeField, Min(1)]
        private int rows = DefaultRows;

        [SerializeField, Min(1)]
        private int columns = DefaultColumns;

        [Header("物品目录（拆分恢复新实例用）")]
        [Tooltip("拆分按类型取模板必需；留空时拆分会被拒绝并记警告。")]
        [SerializeField]
        private ItemManifestCatalog catalog;

        #endregion

        #region 运行时字段

        private bool networkReadOnly;
        public InventoryHostCore AuthorityCore => core;
        public bool IsNetworkReadOnly => networkReadOnly;

        public void SetNetworkAuthority(bool authority)
        {
            networkReadOnly = !authority;
            if (core == null) return;
            core.IsAuthority = () => !networkReadOnly;
            if (networkReadOnly) core.UnsubscribeCommands();
            else if (isActiveAndEnabled) core.SubscribeCommands(core.Container);
        }

        private InventoryHostCore core; // 共享核心：容器、意图命令与编排逻辑（单机自任权威）。

        #endregion

        #region 背包状态与统计

        public InventoryContainer Container => core != null ? core.Container : null; // 权威容器；Awake 创建后不变。

        public bool IsReady => !networkReadOnly && Container != null;

        public bool IsInitialized => !networkReadOnly && Container != null; // 核心与权威容器是否已创建。

        public int Rows => Container?.Rows ?? 0; // 实际行数；尚未初始化时为 0。

        public int Columns => Container?.Columns ?? 0; // 实际列数；尚未初始化时为 0。

        public int SlotCount => Container?.SlotCount ?? 0; // 实际槽位总数。

        public int OccupiedCellCount => Container?.OccupiedCellCount ?? 0; // 已占用格子数。

        public int EmptyCellCount => Container?.EmptyCellCount ?? 0; // 空格数。

        public int ItemCount => Container?.ItemCount ?? 0; // 当前物品数量。

        public IReadOnlyCollection<RuntimeItem> Items => Container?.Items ?? Array.Empty<RuntimeItem>(); // 权威物品集合的只读视图，枚举顺序不保证。

        public int Revision => Container?.Revision ?? 0; // 权威容器的状态版本号。

        #endregion

        #region 背包配置与数据源

        public int ConfiguredRows => rows; // 面板配置的行数。

        public int ConfiguredColumns => columns; // 面板配置的列数。

        public object EventSource => Container; // 事件管线键：单机形态恒为权威容器实例（订阅通知与发布意图共用）。

        public void ConfigureWeaponOperations(Func<ItemId, InventoryContainer> resolver, Func<ItemId, string> blockReason)
        {
            if (core == null) return;
            core.WeaponContainerResolver = resolver;
            core.WeaponOperationBlockReason = blockReason;
        }

        public bool CanExecuteWeaponOperation(WeaponOperation operation, out string error)
        {
            error = "背包尚未初始化。";
            return core != null && core.CanExecuteWeaponOperation(operation, out error);
        }

        public bool CanSubmitTo(IBackpackView destination) => core != null && core.CanSubmitTo(destination);

        public InventoryOperationResult ExecuteOperation(InventoryOperation operation) => core != null
            ? core.ExecuteOperation(operation)
            : new InventoryOperationResult(operation.RequestId, false, ItemId.None, 0, "背包尚未初始化。");

        public GridCoord ResolveItemDimensions(RuntimeItem item) => InventoryContainer.GetItemDimensions(item);

        public bool CanAcceptItem(RuntimeItem item, out string error)
        {
            error = item == null ? "物品为空。" : null;
            return item != null;
        }

        public bool HasEmptySpace(GridCoord dimensions) => core != null && core.HasEmptySpace(dimensions);

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

        #endregion

        #region 生命周期

        /// <summary>创建共享核心（内部创建权威容器，行列非法或乘积超限时回退默认尺寸并记警告），并把 Inspector 目录同步给核心。</summary>
        private void Awake()
        {
            foreach (MonoBehaviour component in GetComponents<MonoBehaviour>())
                if (component is ISharedInventoryAccess access && access.IsNetworkSessionActive && !access.IsServerSession)
                    networkReadOnly = true;
            core = new InventoryHostCore(rows, columns, LogModule.Inventory, name);
            core.Catalog = catalog;
            core.IsAuthority = () => !networkReadOnly;
        }

        /// <summary>以容器为管线键订阅写入意图命令。</summary>
        private void OnEnable()
        {
            if (core != null && !networkReadOnly) core.SubscribeCommands(core.Container);
        }

        /// <summary>释放全部意图订阅（与 OnEnable 成对，重复调用安全）。</summary>
        private void OnDisable()
        {
            if (core != null) core.UnsubscribeCommands();
        }

        #endregion

        #region 物品与格子查询

        // 未初始化时返回空结果，全部查询使用同一权威容器。

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

        public bool IsIndexValid(int index) => Container != null && Container.IsIndexValid(index);

        public bool IsIndexOccupied(int index) => Container != null && Container.IsIndexOccupied(index);

        public int GetUpperLeftIndexAt(int cellIndex) => Container?.GetUpperLeftIndexAt(cellIndex) ?? NoIndex;

        /// <summary>该格物品在当前 ItemId 升序集合中的下标；空格、越界或未初始化返回 NoIndex。</summary>
        public int GetItemIndexAt(int cellIndex) => Container?.GetItemIndexAt(cellIndex) ?? NoIndex;

        public RuntimeItem GetItemAt(int cellIndex) => Container?.GetItemAt(cellIndex);

        public RuntimeItem GetItemAt(GridCoord coord) => Container?.GetItemAt(coord);

        public bool Contains(RuntimeItem item) => Container != null && Container.Contains(item);

        /// <summary>按引用查询当前物品下标；增删后下标可能变化，持久标识请使用 ItemId。</summary>
        public int IndexOf(RuntimeItem item) => Container?.IndexOf(item) ?? NoIndex;

        /// <summary>按当前 ItemId 升序集合的下标读取物品；下标无效或未初始化返回 false。</summary>
        public bool TryGetItem(int itemIndex, out RuntimeItem item)
        {
            item = null;
            return Container != null && Container.TryGetItem(itemIndex, out item);
        }

        /// <summary>按持久标识读取物品；标识无效、不在背包或未初始化返回 false。</summary>
        public bool TryGetItem(ItemId itemId, out RuntimeItem item)
        {
            item = null;
            return core != null && core.TryGetItem(itemId, out item);
        }

        #endregion

        #region 物品占位查询

        public bool TryGetPlacement(ItemId itemId, out InventoryPlacement placement)
        {
            placement = InventoryPlacement.Invalid;
            return TryGetItem(itemId, out RuntimeItem item) && Container.TryGetPlacement(item, out placement);
        }

        public bool TryGetPlacement(RuntimeItem item, out InventoryPlacement placement)
        {
            placement = InventoryPlacement.Invalid;
            return Container != null && Container.TryGetPlacement(item, out placement);
        }

        public bool TryGetUpperLeftIndex(RuntimeItem item, out int upperLeftIndex)
        {
            upperLeftIndex = NoIndex;
            return Container != null && Container.TryGetUpperLeftIndex(item, out upperLeftIndex);
        }

        /// <summary>升序复制物品全部占格下标（先清空 buffer）；物品不在背包或未初始化返回 false。</summary>
        public bool TryGetCellIndices(RuntimeItem item, List<int> buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            InventoryContainer container = Container;
            if (container != null) return container.TryGetCellIndices(item, buffer);

            buffer.Clear();
            return false;
        }

        #endregion

        #region 空间与容量查询

        public static GridCoord GetItemDimensions(RuntimeItem item) => InventoryContainer.GetItemDimensions(item);

        public SpaceQueryResult<RuntimeItem> QuerySpace(int startIndex, GridCoord dimensions) =>
            Container != null ? Container.QuerySpace(startIndex, dimensions) : SpaceQueryResult<RuntimeItem>.Empty;

        public SpaceQueryResult<RuntimeItem> QuerySpace(int startIndex, RuntimeItem item) =>
            Container != null ? Container.QuerySpace(startIndex, item) : SpaceQueryResult<RuntimeItem>.Empty;

        /// <summary>查询物品容量；未初始化时返回独立的空结果。</summary>
        public SlotAvailabilityResult<RuntimeItem> QueryAvailability(RuntimeItem item, int amountOverride = -1) =>
            Container != null ? Container.QueryAvailability(item, amountOverride) : new SlotAvailabilityResult<RuntimeItem>();

        /// <summary>将容量查询写入调用方复用的结果；未初始化时清空结果。</summary>
        public void FillAvailability(RuntimeItem item, int amountOverride, SlotAvailabilityResult<RuntimeItem> result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            InventoryContainer container = Container;
            if (container != null) container.FillAvailability(item, amountOverride, result);
            else result.Clear();
        }

        #endregion

        #region 物品写入与堆叠拆分

        // 模板创建与拆分由共享核心编排，实例放置与堆叠由同一权威容器提交。

        /// <summary>在指定上左格放置现有物品实例，不做堆叠合并。</summary>
        public bool TryPlaceItem(RuntimeItem item, int startIndex, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return Container.TryPlaceItem(item, startIndex, out error);
        }

        public static bool TryCreateUnownedItem(ItemManifest manifest, int stackCount, out RuntimeItem item, out string error)
            => InventoryHostCore.TryCreateUnownedItem(manifest, stackCount, out item, out error);

        /// <summary>将现有实例整批收进背包，先合并同类堆叠再落空格；失败时不改变状态。</summary>
        public bool TryAddItem(RuntimeItem item, out int remainder, out string error)
        {
            remainder = 0;
            if (!EnsureContainer(out error)) return false;
            return Container.TryAddItem(item, out remainder, out error);
        }

        /// <summary>整批接收外部物品，可供拾取使用；先合并已有同类堆叠再安放余量，失败不改变状态。成功后调用方交出实例并移除对应拾取物；已在任意背包中的物品须走转移入口。</summary>
        public bool TryReceiveItem(RuntimeItem item, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return Container.TryReceiveItem(item, out error);
        }

        /// <summary>将本背包中的物品整件或整堆转入目标背包；目标按通用接收规则合并与落格，失败时双方不变。</summary>
        public bool TryTransferItem(RuntimeItem item, BackpackInventory destination, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            if (destination == null)
            {
                error = "目标背包为空。";
                BackpackLog.Warn(LogModule.Inventory, error);
                return false;
            }

            if (!destination.EnsureContainer(out error)) return false;
            return Container.TryTransferItem(item, destination.Container, out error);
        }

        /// <summary>将本背包中的物品整件或整堆转入目标首个合法空白矩形，不合并已有堆叠；失败双方不变。落点由权威容器决定。</summary>
        public bool TryTransferItemToEmptySpace(RuntimeItem item, BackpackInventory destination, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            if (destination == null)
            {
                error = "目标背包为空。";
                BackpackLog.Warn(LogModule.Inventory, error);
                return false;
            }

            if (!destination.EnsureContainer(out error)) return false;
            return Container.TryTransferItemToEmptySpace(item, destination.Container, out error);
        }

        /// <summary>按模板创建一件新物品并整批收进背包：分配标识、克隆模板并执行首次随机化、先并同类堆叠再落首个空白区域；任一步失败整体失败且不留下半成品。</summary>
        public bool TryAddItem(ItemManifest manifest, int stackCount, out RuntimeItem item, out string error)
        {
            item = null;
            if (!EnsureContainer(out error)) return false;

            return core.TryAddItem(manifest, stackCount, out item, out error);
        }

        /// <summary>按实例移除物品并释放其全部占格。</summary>
        public bool TryRemoveItem(RuntimeItem item, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return Container.TryRemoveItem(item, out error);
        }

        /// <summary>按持久标识定位后移除物品。</summary>
        public bool TryRemoveItem(ItemId itemId, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return core.TryRemoveItem(itemId, out error);
        }

        /// <summary>把未入包的来源实例并入背包内目标堆叠，返回实际并入数量。</summary>
        public bool TryStackItem(RuntimeItem target, RuntimeItem incoming, out int movedCount, out string error)
        {
            movedCount = 0;
            if (!EnsureContainer(out error)) return false;
            return Container.TryStackItem(target, incoming, out movedCount, out error);
        }

        /// <summary>按实例修改背包内物品的堆叠数。</summary>
        public bool TrySetStackCount(RuntimeItem item, int stackCount, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return Container.TrySetStackCount(item, stackCount, out error);
        }

        /// <summary>按持久标识定位后修改堆叠数。</summary>
        public bool TrySetStackCount(ItemId itemId, int stackCount, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return core.TrySetStackCount(itemId, stackCount, out error);
        }

        /// <summary>从指定堆叠拆出独立实例（核心自任权威）：核对归属、可堆叠与数量边界、按类型目录取模板、抓源快照并以新标识恢复一份数值原样继承的新实例、落到行优先扫描出的首个空白区域；任一步失败整体零变更。</summary>
        public bool TrySplitStack(RuntimeItem source, int splitCount, out ItemId newItemId, out string error)
        {
            newItemId = ItemId.None;
            if (!EnsureContainer(out error)) return false;

            return core.TrySplitStack(source, splitCount, out newItemId, out error);
        }

        /// <summary>按持久标识定位后拆分堆叠，新实例继承源数值并获得新标识。</summary>
        public bool TrySplitStack(ItemId itemId, int splitCount, out ItemId newItemId, out string error)
        {
            newItemId = ItemId.None;
            if (!EnsureContainer(out error)) return false;
            return core.TrySplitStack(itemId, splitCount, out newItemId, out error);
        }

        #endregion

        #region 内部辅助

        public bool CanLoadMagazine(ItemId magazineId, ItemId ammoId, int roundCount, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return core.CanLoadMagazine(magazineId, ammoId, roundCount, out error);
        }

        public bool TryLoadMagazine(ItemId magazineId, ItemId ammoId, int roundCount, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return core.TryLoadMagazine(magazineId, ammoId, roundCount, out error);
        }

        public bool CanExchangeWeaponMagazine(InventoryContainer weaponContainer, ItemId weaponId,
            ItemId magazineId, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return core.CanExchangeWeaponMagazine(weaponContainer, weaponId, magazineId, out error);
        }

        public bool TryExchangeWeaponMagazine(InventoryContainer weaponContainer, ItemId weaponId,
            ItemId magazineId, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return core.TryExchangeWeaponMagazine(weaponContainer, weaponId, magazineId, out error);
        }

        public bool CanExchangeWeaponAttachment(InventoryContainer weaponContainer, ItemId weaponId,
            WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return core.CanExchangeWeaponAttachment(weaponContainer, weaponId, slot, attachmentId, out error);
        }

        public bool TryExchangeWeaponAttachment(InventoryContainer weaponContainer, ItemId weaponId,
            WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
        {
            if (!EnsureContainer(out error)) return false;
            return core.TryExchangeWeaponAttachment(weaponContainer, weaponId, slot, attachmentId, out error);
        }

        /// <summary>背包写入共用的初始化守卫。</summary>
        private bool EnsureContainer(out string error)
        {
            if (Container != null && !networkReadOnly)
            {
                error = null;
                return true;
            }

            error = networkReadOnly ? "客户端本地背包只读，请提交网络库存意图。" : "背包尚未初始化（组件未执行 Awake）。";
            BackpackLog.Warn(LogModule.Inventory, error);
            return false;
        }

        #endregion

    }
}
