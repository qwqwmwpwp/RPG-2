using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Runtime;

namespace RPG4.Backpack.Network
{
    /// <summary>库存网络宿主：客户端意图经 Command 交给权威核心；结构基线、运行差量与布局经同一有序同步流下发。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/Backpack Network Inventory")]
    public sealed partial class BackpackNetworkInventory : NetworkBehaviour, IBackpackView, IInventoryOperations, ISharedInventoryAccess
    {
        public const int DefaultRows = 6; // 权威容器默认行数。

        public const int DefaultColumns = 8; // 权威容器默认列数。

        [Header("物品类型目录（客户端按类型恢复物品）")]
        [Tooltip("客户端只收到类型标签，必须靠目录找回模板；未配置时客户端无法恢复物品，只记录诊断。")]
        [SerializeField]
        private ItemManifestCatalog catalog;

        [Header("权威容器尺寸（仅服务端使用）")]
        [Tooltip("服务端创建权威容器，行列与物品落点同步到客户端。")]
        [SerializeField, Min(1)]
        private int rows = DefaultRows;

        [SerializeField, Min(1)]
        private int columns = DefaultColumns;

        public int ConfiguredRows => rows; // 预制体配置的展示行数（本地配置，不代表服务端已同步的尺寸）。

        public int ConfiguredColumns => columns; // 预制体配置的展示列数（本地配置，不代表服务端已同步的尺寸）。

        [SyncVar(hook = nameof(OnGridSizeChanged))] private int authoritativeRows;
        [SyncVar(hook = nameof(OnGridSizeChanged))] private int authoritativeColumns;
        private readonly Dictionary<ulong, InventoryPlacement> mirrorPlacements = new Dictionary<ulong, InventoryPlacement>();
        private readonly InventoryNetworkMirror mirrorProjection = new InventoryNetworkMirror();
        private readonly HashSet<ItemId> pendingViewChanges = new HashSet<ItemId>();
        private bool fullViewRefreshPending = true;
        private bool mirrorDirty;
        private bool mirrorReady;

        public int Rows => UsesAuthorityView ? Container.Rows : authoritativeRows;
        public int Columns => UsesAuthorityView ? Container.Columns : authoritativeColumns;
        public bool IsReady => UsesAuthorityView || (isClient && core != null && mirrorReady && (isOwned || sharedClientSubscribed));

        private bool UsesAuthorityView => IsServerInventory && (!isClient || (!sharedContainer && isOwned));

        private readonly InventorySyncList entries = new InventorySyncList();
        private readonly InventoryNetworkProjection projection = new InventoryNetworkProjection();
        private readonly Dictionary<ulong, RuntimeItem> mirror = new Dictionary<ulong, RuntimeItem>(); // 客户端镜像：按物品标识索引的运行时实例。
        private readonly List<RuntimeItem> orderedItems = new List<RuntimeItem>(); // 复用缓冲，避免每次查询都分配列表。

        private InventoryHostCore core; // 共享核心：权威端持容器与编排，纯客户端只承载意图订阅与转发。
        private int syncedRevision = -1; // 上一次投影对应的容器 Revision，用于短路无变化的同步。
        private bool changedPending; // 视图已变化但尚未通知：合并到帧末触发一次。

        /// <summary>库存视图发生变化（服务端权威变化或客户端镜像更新）后触发；表现层据此重绘，批量同步完成后只触发一次，不逐条触发。</summary>
        public event Action Changed;

        public string LastError { get; private set; } // 最近一次失败原因；无失败为 null。

        public int DesyncCount { get; private set; } // 累计被拒绝的条目数量（载荷非法 / 模板缺失等），用于诊断。

        public ProjectionSyncReport LastProjectionReport => projection.LastSyncReport;
        public long ProjectionSyncCount => projection.SyncCount;
        public long ProjectionFullSyncCount => projection.FullSyncCount;
        public long ConsideredItemCount => projection.ConsideredItemCount;
        public long EncodedItemCount => projection.EncodedItemCount;
        public long EncodedPayloadBytes => projection.EncodedPayloadBytes;
        public long EncodedRuntimeCount => projection.EncodedRuntimeCount;
        public long EncodedRuntimeBytes => projection.EncodedRuntimeBytes;
        public long StructuralEntrySendCount => entries.StructuralEntrySendCount;
        public long RuntimeDeltaSendCount => entries.RuntimeDeltaSendCount;
        public long MirrorRuntimeUpdateCount => mirrorProjection.RuntimeUpdateCount;
        public long ProjectionCacheHits => projection.CacheHitCount;
        public long MirrorBatchCount => mirrorProjection.BatchCount;
        public long MirrorFullRefreshCount => mirrorProjection.FullRefreshCount;
        public long RestoredMirrorItemCount => mirrorProjection.RestoredItemCount;
        public long ReusedMirrorItemCount => mirrorProjection.ReusedItemCount;
        public int LastRestoredMirrorItemCount => mirrorProjection.LastRestoredItemCount;
        public int LastChangedMirrorItemCount => mirrorProjection.LastChangedItemCount;

        public void ResetSynchronizationStatistics()
        {
            projection.ResetStatistics();
            mirrorProjection.ResetStatistics();
            entries.ResetStatistics();
        }

        public IReadOnlyList<NetworkInventoryEntry> Entries => entries; // 当前同步条目（只读观察用；服务端写入，客户端只读）。

        private InventoryContainer Container => core != null ? core.Container : null; // 权威容器（核心持有）；客户端为 null。

        public bool IsServerInventory => isServer && Container != null; // 服务端是否持有权威容器。

        public int ItemCount => UsesAuthorityView ? Container.ItemCount : mirror.Count; // 当前可见物品数量：服务端取权威容器，客户端取镜像。

        public InventoryContainer ServerContainer => IsServerInventory ? Container : null; // 服务端权威容器，客户端为 null；外部代码直接改动容器后必须调用 SyncNow 补齐投影，否则客户端看不到变化。

        public InventoryHostCore AuthorityCore => IsServerInventory ? core : null;

        public ItemManifestCatalog Catalog => catalog; // 类型目录（客户端按类型恢复物品所需）。

        /// <summary>目录被注入 / 替换后触发：同角色上的下游系统（如装备组件）据此重建自己的镜像，依赖方向固定为「装备 → 库存」。</summary>
        public event Action<ItemManifestCatalog> CatalogChanged;

        /// <summary>事件管线键：权威端（服务端 / 主机）为权威容器实例，纯客户端为宿主自身（客户端没有容器，通知由宿主代发）。</summary>
        public object EventSource
        {
            get
            {
                if (IsServerInventory) return Container;
                return this;
            }
        }

        public bool CanSubmitTo(IBackpackView destination) => core != null && (UsesAuthorityView ? core.CanSubmitTo(destination) : CanForwardTo(destination));

        public GridCoord ResolveItemDimensions(RuntimeItem item) => InventoryContainer.GetItemDimensions(item);

        public bool CanAcceptItem(RuntimeItem item, out string error)
        {
            error = item == null ? "物品为空。" : null;
            return item != null;
        }

        public bool HasEmptySpace(GridCoord dimensions) => core != null && core.HasEmptySpace(dimensions);

        /// <summary>覆盖本实例的类型目录（须在客户端初始化前调用）；框架不提供全局兜底目录，目录缺失属于配置错误并由诊断信息暴露。</summary>
        public void UseCatalog(ItemManifestCatalog value)
        {
            catalog = value;
            if (core != null) core.Catalog = value;
            projection.Clear();
            if (IsServerInventory) { projection.Bind(Container, entries); syncedRevision = -1; }
            else
            {
                mirrorProjection.RequestFullRefresh(true);
                if (isClient) mirrorDirty = true;
            }
            CatalogChanged?.Invoke(value);
        }

        /// <summary>订阅同步列表回调，客户端在同批变更接收后原子应用镜像增量。</summary>
        private void Awake()
        {
            syncMode = SyncMode.Owner;
            syncDirection = SyncDirection.ServerToClient;
            entries.Callback += OnEntryChanged;
        }

        /// <summary>销毁时释放同步列表回调，避免残留回调访问已销毁对象。</summary>
        private void OnDestroy()
        {
            RevokeAccessSubscriptions("库存对象已销毁。");
            sharedReceive = null;
            entries.Callback -= OnEntryChanged;
            projection.Clear();
            mirrorProjection.Clear();
        }

        /// <inheritdoc/>
        public override void OnStartServer()
        {
            base.OnStartServer();
            syncMode = SyncMode.Owner;
            syncDirection = SyncDirection.ServerToClient;
            entries.SuppressAutomaticSerialization = sharedContainer;

            // 权威端：核心创建权威容器（尺寸非法由核心回退默认并记警告）并接线「自己为主端」。
            BackpackInventory local = GetComponent<BackpackInventory>();
            if (local != null)
            {
                CaptureCoreBindings(local.AuthorityCore);
                local.SetNetworkAuthority(true);
            }
            core = local != null ? local.AuthorityCore : new InventoryHostCore(rows, columns, LogModule.Network, name);
            core.SupportsSingleCellTransfers = true;
            if (catalog == null && local != null) catalog = local.Catalog;
            core.Catalog = catalog;
            core.IsAuthority = () => IsServerInventory;
            core.IdGeneratorProvider = ResolveGenerator;
            core.ForwardOperation = RequestInventoryOperation;
            core.ForwardWeaponOperation = RequestWeaponOperation;
            core.CommitConfirm = ConfirmInventoryProjection;
            core.AuthorityOperationDispatcher = DispatchAuthorityOperation;
            core.AuthorityWeaponOperationDispatcher = DispatchAuthorityWeaponOperation;

            authoritativeRows = Container.Rows;
            authoritativeColumns = Container.Columns;
            projection.Clear();
            mirror.Clear();
            mirrorPlacements.Clear();
            syncedRevision = -1;
            DesyncCount = 0;
            LastError = null;
            entries.Clear();
            projection.Bind(Container, entries);
            mirrorProjection.Clear();
            ResetSynchronizationStatistics();

            BackpackLog.Info(
                LogModule.Network,
                $"库存网络组件就绪（服务端）：netId={netId}，容器 {core.Container.Rows}×{core.Container.Columns}。");

            core.SubscribeCommands(core.Container);
            // Host 的 HUD/检查仍由复用同一核心的 PlayerEquipment 发布；纯客户端才绑定网络镜像 UI。
        }

        /// <inheritdoc/>
        public override void OnStopServer()
        {
            StopServerAccess();
            entries.SuppressAutomaticSerialization = false;
            UnbindNetworkWeaponUi();
            if (core != null) core.UnsubscribeCommands();
            RestoreCoreBindings();
            GetComponent<BackpackInventory>()?.SetNetworkAuthority(true);
            core = null;
            projection.Clear();
            syncedRevision = -1;
            base.OnStopServer();
        }

        /// <inheritdoc/>
        public override void OnStartClient()
        {
            base.OnStartClient();

            // 主机模式下 isServer 为真，权威容器即本地视图，不建镜像（否则会出现两份 RuntimeItem）。
            if (isServer) return;

            BackpackInventory local = GetComponent<BackpackInventory>();
            if (catalog == null && local != null) catalog = local.Catalog;
            local?.SetNetworkAuthority(false);
            GetComponent<EquipmentInventory>()?.SetNetworkAuthority(false);
            // 纯客户端：核心不持有容器，只把意图转发为 Command。
            core = new InventoryHostCore(LogModule.Network, name);
            core.SupportsSingleCellTransfers = true;
            core.Catalog = catalog;
            core.IsAuthority = () => false;
            core.ForwardOperation = RequestInventoryOperation;
            core.ForwardWeaponOperation = RequestWeaponOperation;
            core.ForwardCanSubmit = CanForwardTo;

            mirrorProjection.Clear();
            ResetSynchronizationStatistics();
            if (isOwned) RebuildMirror();
            core.SubscribeCommands(this);
            BindNetworkWeaponUi();
        }

        /// <inheritdoc/>
        public override void OnStopClient()
        {
            ClearClientAccess();
            UnbindNetworkWeaponUi();
            // StopHost 先 StopClient 后 StopServer：保留权威 core，等 OnStopServer 恢复本地绑定。
            if (!isServer)
            {
                if (core != null) core.UnsubscribeCommands();
                core = null;
                projection.Clear();
                GetComponent<BackpackInventory>()?.SetNetworkAuthority(true);
                GetComponent<EquipmentInventory>()?.SetNetworkAuthority(true);
            }
            mirror.Clear();
            mirrorPlacements.Clear();
            mirrorReady = false;
            mirrorProjection.Clear();
            pendingViewChanges.Clear();
            fullViewRefreshPending = true;
            RaiseChanged();
            base.OnStopClient();
        }

        // 客户端意图（Command）：只表达意图，最终状态由服务端复核后决定。

        /// <summary>同库存的一次完整操作只发送一条 Command；跨库存交互由权威宿主直接执行。</summary>
        private void RequestInventoryOperation(InventoryOperation operation)
        {
            SubmitClientOperation(this, operation);
        }

        [Command(requiresAuthority = true)]
        private void CmdExecuteInventoryOperation(ulong ticket, int requestId, uint sourceNetId, bool sourceEquipment, uint sourceSession,
            uint destinationNetId, bool destinationEquipment, uint destinationSession, int kind, ulong sourceId, ulong targetId, int count, int upperLeftIndex)
        {
            ReceiveInventoryOperation(ticket, requestId, sourceNetId, sourceEquipment, sourceSession, destinationNetId, destinationEquipment, destinationSession,
                kind, sourceId, targetId, count, upperLeftIndex);
        }

        // 服务端权威修改：复核通过才改容器，改完立即投影。

        /// <summary>服务端创建物品并入容器（服务端唯一的物品创建入口）：核心分配标识、按模板创建实例并写入容器，投影失败时核心回滚本次新增，不留下客户端看不到的物品。</summary>
        public bool TryServerAddItem(ItemManifest manifest, int stackCount, out ItemId itemId, out string error)
        {
            itemId = ItemId.None;
            if (!IsServerInventory)
            {
                error = "只有服务端可以创建物品。";
                BackpackLog.Warn(LogModule.Network, error);
                return false;
            }

            if (!core.TryAddItem(manifest, stackCount, out RuntimeItem item, out error))
            {
                return false;
            }

            itemId = item.ItemId;
            BackpackLog.Info(
                LogModule.Network,
                $"服务端新增物品：{itemId} {manifest.ItemType} x{stackCount}，当前 {Container.ItemCount} 件。");
            return true;
        }

        /// <summary>服务端修改堆叠数：复核标识与边界后由容器提交（数量未变化时不产生同步），投影失败即回滚原堆叠数。</summary>
        public bool TryServerSetStackCount(ItemId itemId, int stackCount, out string error)
        {
            if (!IsServerInventory)
            {
                error = "只有服务端可以修改库存。";
                BackpackLog.Warn(LogModule.Network, error);
                return false;
            }

            return core.TrySetStackCount(itemId, stackCount, out error);
        }

        /// <summary>服务端移除物品并释放占用格子。</summary>
        public bool TryServerRemoveItem(ItemId itemId, out string error)
        {
            if (!IsServerInventory)
            {
                error = "只有服务端可以修改库存。";
                BackpackLog.Warn(LogModule.Network, error);
                return false;
            }

            if (!core.TryRemoveItem(itemId, out error))
            {
                return false;
            }

            BackpackLog.Info(LogModule.Network, $"服务端移除物品：{itemId}，当前 {Container.ItemCount} 件。");
            return true;
        }

        /// <summary>服务端从某个堆叠中拆出指定数量并返回新实例标识：核心按「源物品存在 → 可堆叠 → 数量边界（1 .. 堆叠数 - 1）→ 模板可用 → 快照可捕获 → 存在能容纳拆分结果的空白区域」依次复核，任一步失败都不修改任何状态；新实例由源物品快照恢复（换新标识 + 拆分数量），数值原样继承不重新掷点，落点由服务端在空白区域中选取，落点与占格尺寸同时同步。</summary>
        public bool TryServerSplitStack(ItemId itemId, int splitCount, out ItemId newItemId, out string error)
        {
            newItemId = ItemId.None;
            if (!IsServerInventory)
            {
                error = "只有服务端可以拆分库存物品。";
                BackpackLog.Warn(LogModule.Network, error);
                return false;
            }

            return core.TrySplitStack(itemId, splitCount, out newItemId, out error);
        }

        /// <summary>先验证提交物品能按本宿主目录恢复，再同步投影；None 用于回滚后的补偿。</summary>
        private bool ConfirmInventoryProjection(ItemId requiredItem, out string error)
        {
            if (!IsServerInventory)
            {
                error = "只有服务端可以确认库存投影。";
                return false;
            }

            projection.Bind(Container, entries);
            if (requiredItem.IsValid)
            {
                if (!Container.TryGetItemById(requiredItem, out RuntimeItem item))
                {
                    error = $"库存中不存在待确认物品 {requiredItem}。";
                    return false;
                }
                if (!projection.TryValidateRestoration(item, catalog, out error)) return false;
            }

            return SyncEntries(out error, requiredItem);
        }

        /// <summary>服务端清空库存（容器与同步列表一起清空）。</summary>
        public bool TryServerClearInventory(out string error)
        {
            if (!IsServerInventory)
            {
                error = "只有服务端可以修改库存。";
                return false;
            }

            Container.FillItemsByItemId(orderedItems);
            int failed = 0;
            for (int i = 0; i < orderedItems.Count; i++)
            {
                if (!Container.TryRemoveItem(orderedItems[i], out string removeError))
                {
                    failed++;
                    BackpackLog.Error(
                        LogModule.Network,
                        $"清空库存时移除 {orderedItems[i].ItemId} 失败：{removeError}");
                }
            }

            entries.Clear();
            projection.Clear();

            if (failed > 0)
            {
                // 有物品未能移除：不能盲目清空客户端列表，改为差异投影，保证镜像与容器一致。
                return SyncEntries(out error);
            }

            syncedRevision = Container.Revision;
            error = null;
            RaiseChanged();
            BackpackLog.Info(LogModule.Network, "服务端已清空库存。");
            return true;
        }

        /// <summary>主动补齐投影：外部代码直接改动容器后调用，按 <see cref="InventoryContainer.Revision"/> 短路无变化的同步。</summary>
        public bool SyncNow(out string error)
        {
            if (!IsServerInventory)
            {
                error = "只有服务端拥有权威容器，无需同步。";
                return false;
            }

            if (Container.Revision == syncedRevision && !projection.HasPendingChanges && LastError == null)
            {
                error = null;
                return true;
            }

            return SyncEntries(out error);
        }

        /// <summary>客户端：按当前注入的目录重新恢复镜像（镜像在 OnStartClient 时建立，目录若在客户端启动后才注入则必须调用本方法重放条目，否则镜像一直为空）；确实重建了客户端镜像返回 true，服务端 / 未启动客户端返回 false。</summary>
        public bool RefreshClientMirror()
        {
            if (!isClient || IsServerInventory) return false;
            mirrorProjection.RequestFullRefresh(true);
            RebuildMirror();
            return true;
        }

        // 只读视图（服务端 = 权威容器；客户端 = 镜像）。

        /// <summary>按标识查找物品（服务端查权威容器，客户端查镜像）。</summary>
        public bool TryGetItem(ItemId itemId, out RuntimeItem item)
        {
            item = null;
            if (!itemId.IsValid) return false;
            if (UsesAuthorityView) return core.TryGetItem(itemId, out item);
            return mirror.TryGetValue(itemId.Value, out item);
        }

        public bool TryGetPlacement(ItemId itemId, out InventoryPlacement placement)
        {
            placement = InventoryPlacement.Invalid;
            if (!IsReady || !itemId.IsValid) return false;
            if (UsesAuthorityView) return Container.TryGetItemById(itemId, out RuntimeItem item)
                && Container.TryGetPlacement(item, out placement);
            return mirrorPlacements.TryGetValue(itemId.Value, out placement);
        }

        /// <summary>按标识升序复制当前物品（确定性顺序，供调用方只读遍历；先清空目标列表）。</summary>
        public void CopyItems(List<RuntimeItem> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            if (UsesAuthorityView)
            {
                Container.FillItemsByItemId(destination);
                return;
            }

            destination.Clear();
            foreach (RuntimeItem item in mirror.Values)
            {
                destination.Add(item);
            }

            destination.Sort(InventoryNetworkProjection.ItemIdComparison);
        }

        /// <summary>可诊断状态文本（日志与外部面板共用）。</summary>
        public string DescribeState()
        {
            string mode = IsServerInventory ? "服务端（权威容器）" : (isClient ? "客户端（镜像）" : "未启动");
            string text = $"{mode} 物品={ItemCount} 条目={entries.Count} 拒绝={DesyncCount}"
                + $" 检查={ConsideredItemCount} 编码={EncodedItemCount} 缓存命中={ProjectionCacheHits}"
                + $" 运行编码={EncodedRuntimeCount} 结构发送={StructuralEntrySendCount} 运行差量发送={RuntimeDeltaSendCount}"
                + $" 编码载荷字节={EncodedPayloadBytes} 镜像恢复={RestoredMirrorItemCount} 镜像复用={ReusedMirrorItemCount}"
                + $" 镜像运行更新={MirrorRuntimeUpdateCount}"
                + $" 最近批次变化={LastChangedMirrorItemCount} 恢复={LastRestoredMirrorItemCount}";
            return LastError == null ? text : text + " 最近错误=" + LastError;
        }

        // 内部：投影同步与镜像维护。

        /// <summary>把权威容器差异投影到 <see cref="entries"/>：任意物品投影被拒绝，或 requiredItem（本次新增 / 修改的物品）未进入投影时均返回失败。</summary>
        private bool SyncEntries(out string error, ItemId requiredItem = default)
        {
            if (!IsServerInventory)
            {
                error = "只有服务端可以同步库存投影。";
                return false;
            }

            syncedRevision = Container.Revision;
            projection.SyncContainer(Container, entries, out ProjectionSyncReport report);

            if (report.Rejected > 0)
            {
                RegisterFailure("投影被拒绝：" + report.FirstError);
                BackpackLog.Error(
                    LogModule.Network,
                    $"库存投影存在 {report.Rejected} 件无法同步的物品（首个原因：{report.FirstError}）。");
            }

            if (report.HasChanges)
            {
                RaiseChanged();
            }

            if (report.Rejected > 0)
            {
                error = report.FirstError;
                return false;
            }

            if (requiredItem.IsValid && !projection.TryGetItem(requiredItem, out _))
            {
                error = $"物品 {requiredItem} 未能进入投影：{report.FirstError}";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>初始/恢复时读取完整条目，正常变更仅处理暂存增量；失败保留上一完整镜像。</summary>
        private void RebuildMirror()
        {
            mirrorDirty = false;
            mirrorReady = mirrorProjection.TryApply(sharedClientSubscribed ? sharedClientStream.Entries : entries, catalog, Rows, Columns, projection,
                mirror, mirrorPlacements, out string error);
            if (!mirrorReady)
            {
                RegisterFailure("镜像恢复被拒绝：" + error);
                BackpackLog.Error(LogModule.Network, LastError);
            }
            else LastError = null;
            if (!mirrorReady || mirrorProjection.LastFullRefresh)
            {
                fullViewRefreshPending = true;
                pendingViewChanges.Clear();
            }
            else if (!fullViewRefreshPending)
                foreach (ItemId id in mirrorProjection.LastChangedItems) pendingViewChanges.Add(id);
            RaiseChanged();
        }

        private void OnGridSizeChanged(int previous, int current)
        {
            if (isServer) return;
            mirrorProjection.RequestFullRefresh();
            mirrorDirty = true;
            RaiseChanged();
        }

        /// <summary>同批条目全部应用后再恢复布局，避免交换位置的中间条目被误判重叠。</summary>
        private void OnEntryChanged(SyncList<NetworkInventoryEntry>.Operation op, int index,
            NetworkInventoryEntry oldItem, NetworkInventoryEntry newItem)
        {
            if (isServer) { RecordSharedChange(op, index, newItem); return; }
            switch (op)
            {
                case SyncList<NetworkInventoryEntry>.Operation.OP_ADD:
                case SyncList<NetworkInventoryEntry>.Operation.OP_INSERT:
                    mirrorProjection.Added(newItem);
                    break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_SET:
                    mirrorProjection.Replaced(oldItem, newItem);
                    break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_REMOVEAT:
                    mirrorProjection.Removed(oldItem);
                    break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_CLEAR:
                    mirrorProjection.RequestFullRefresh();
                    break;
            }
            mirrorDirty = true;
            RaiseChanged();
        }

        /// <summary>与离线箱子共用单调生成器；启服保留既存物品身份，不从 1 重新发号。</summary>
        private ItemIdGenerator ResolveGenerator() => InventoryHostCore.SharedItemIds;

        /// <summary>累加不同步计数并记录最近一次失败原因。</summary>
        private void RegisterFailure(string reason)
        {
            DesyncCount++;
            LastError = reason;
        }

        /// <summary>标记视图已变化；实际通知合并到帧末触发一次，避免晚加入客户端的逐条快照造成 O(n²) 重绘开销。</summary>
        private void RaiseChanged()
        {
            changedPending = true;
        }

        /// <summary>帧末统一派发一次视图变化通知；纯客户端没有容器，额外由宿主自身补发粗粒度变更信号供表现层订阅。</summary>
        private void LateUpdate()
        {
            if (IsServerInventory) MaintainServerAccess();
            if (IsServerInventory && (Container.Revision != syncedRevision || projection.HasPendingChanges)) SyncEntries(out _);
            if ((!IsServerInventory || sharedClientSubscribed) && mirrorDirty) RebuildMirror();
            if (IsServerInventory) FlushSharedChanges();
            if (!changedPending) { FlushNetworkWeaponUi(); return; }

            changedPending = false;
            bool detailed = !IsServerInventory && (EventBus.Global.SubscriptionCount(InvChannels.ItemsChanged, this) > 0
                || EventBus.Global.SubscriptionCount(InvChannels.ItemsChanged, EventBus.AnySource) > 0);
            ItemId[] changed = null;
            if (detailed && !fullViewRefreshPending && pendingViewChanges.Count > 0)
            {
                changed = new ItemId[pendingViewChanges.Count];
                pendingViewChanges.CopyTo(changed);
            }
            pendingViewChanges.Clear();
            fullViewRefreshPending = false;
            Changed?.Invoke();

            // 权威端由容器自行广播 Inv.Changed（source = 容器），客户端只能由宿主代发。
            if (!IsServerInventory)
            {
                if (detailed) EventBus.Global.Publish<ItemId[]>(InvChannels.ItemsChanged, this, changed);
                EventBus.Global.Publish(InvChannels.Changed, this);
            }
            FlushNetworkWeaponUi();
        }
    }
}
