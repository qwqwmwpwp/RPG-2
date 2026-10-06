// M3-only 裁剪版（蓝本 e:/github/ZDXT2/Assets/backpack/Scripts/Network/Inventory/BackpackNetworkInventory.cs，877 行）。
// 已剥离 M4 拾取 12 处：DefaultPickupDistance L20 / pickupDistance 字段 L35-38 / PickupDistance L61 / PickupRejectionCount L63 / LastPickupResult L65 /
// OnStartServer 重置 L124-125 / RequestPickup L194-204 / CmdRequestPickup L206-216 / CanSendPickupRequest L285-301 / TryServerPickup L355-404 /
// ClearPickupPrompt L406-410 / RegisterPickupRejection L848-853。
// M6 拆分链路已回填：RequestSplitStack / CmdRequestSplitStack / TryServerSplitStack（拆分编排与辅助已收进 InventoryHostCore，宿主只做服务端守卫与转发）。
// 未移植蓝本的 SplitRejectionCount + LastSplitResult 两个纯诊断字段：RPG_4 沿用既有 DesyncCount / LastError 与日志口径，不留无人读的状态。
// 已剥离 M8 钩子 4 个：ResetConsumeDiagnostics L128 / InitializeDropPipeline L129 / SubscribeCommandChannels L152 / UnsubscribeCommandChannels L100、L163。
// 后续阶段（M4 / M6 / M7 / M8）按上述行号回填；装备分片 BackpackNetworkInventory.Equipment.cs 与消耗分片 .Consumables.cs 本轮不编入。
using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    /// <summary>玩家级库存网络组件：权威操作与意图分发组合自 <see cref="InventoryHostCore"/>（服务端持权威 <see cref="InventoryContainer"/>，纯客户端无容器、只转发 Command），本组件保留 SyncList 单向投影给所属客户端（数据流为客户端意图 → Command → 服务端复核 → 容器原子修改 → 投影差异 → 客户端只读镜像，不允许反向写回）；只同步标识、类型标签、堆叠数与运行时数值快照，不同步网格位置与 Unity 引用（客户端按类型目录取回模板且不重新随机化数值）；本组件是库存唯一写入口，容器不提供事件，外部改动靠 <see cref="InventoryContainer.Revision"/> 观察。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/Backpack Network Inventory")]
    public sealed partial class BackpackNetworkInventory : NetworkBehaviour, IBackpackView
    {
        public const int DefaultRows = 6; // 权威容器默认行数。

        public const int DefaultColumns = 8; // 权威容器默认列数。

        [Header("物品类型目录（客户端按类型恢复物品）")]
        [Tooltip("客户端只收到类型标签，必须靠目录找回模板；未配置时客户端无法恢复物品，只记录诊断。")]
        [SerializeField]
        private ItemManifestCatalog catalog;

        [Header("权威容器尺寸（仅服务端使用）")]
        [Tooltip("只同步物品身份与状态，不同步位置；尺寸仅用于服务端容量与空间校验。")]
        [SerializeField, Min(1)]
        private int rows = DefaultRows;

        [SerializeField, Min(1)]
        private int columns = DefaultColumns;

        public int ConfiguredRows => rows; // 预制体配置的展示行数（本地配置，不代表服务端已同步的尺寸）。

        public int ConfiguredColumns => columns; // 预制体配置的展示列数（本地配置，不代表服务端已同步的尺寸）。

        private readonly SyncList<NetworkInventoryEntry> entries = new SyncList<NetworkInventoryEntry>();
        private readonly InventoryNetworkProjection projection = new InventoryNetworkProjection();
        private readonly Dictionary<ulong, RuntimeItem> mirror = new Dictionary<ulong, RuntimeItem>(); // 客户端镜像：按物品标识索引的运行时实例。
        private readonly List<RuntimeItem> orderedItems = new List<RuntimeItem>(); // 复用缓冲，避免每次查询都分配列表。

        private InventoryHostCore core; // 共享核心：权威端持容器与编排，纯客户端只承载意图订阅与转发。
        private ItemIdGenerator fallbackGenerator; // 未取到 NetworkManager 生成器时的兜底实例，仅调试用。
        private int syncedRevision = -1; // 上一次投影对应的容器 Revision，用于短路无变化的同步。
        private bool changedPending; // 视图已变化但尚未通知：合并到帧末触发一次。

        /// <summary>库存视图发生变化（服务端权威变化或客户端镜像更新）后触发；表现层据此重绘，批量同步完成后只触发一次，不逐条触发。</summary>
        public event Action Changed;

        public string LastError { get; private set; } // 最近一次失败原因；无失败为 null。

        public int DesyncCount { get; private set; } // 累计被拒绝的条目数量（载荷非法 / 模板缺失等），用于诊断。

        public IReadOnlyList<NetworkInventoryEntry> Entries => entries; // 当前同步条目（只读观察用；服务端写入，客户端只读）。

        private InventoryContainer Container => core != null ? core.Container : null; // 权威容器（核心持有）；客户端为 null。

        public bool IsServerInventory => isServer && Container != null; // 服务端是否持有权威容器。

        public int ItemCount => IsServerInventory ? Container.ItemCount : mirror.Count; // 当前可见物品数量：服务端取权威容器，客户端取镜像。

        public InventoryContainer ServerContainer => IsServerInventory ? Container : null; // 服务端权威容器，客户端为 null；外部代码直接改动容器后必须调用 SyncNow 补齐投影，否则客户端看不到变化。

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

        /// <summary>覆盖本实例的类型目录（须在客户端初始化前调用）；框架不提供全局兜底目录，目录缺失属于配置错误并由诊断信息暴露。</summary>
        public void UseCatalog(ItemManifestCatalog value)
        {
            catalog = value;
            if (core != null) core.Catalog = value;
            CatalogChanged?.Invoke(value);
        }

        /// <summary>订阅同步列表回调，用于客户端增量维护镜像。</summary>
        private void Awake()
        {
            entries.Callback += OnEntryChanged;
        }

        /// <summary>销毁时释放同步列表回调，避免残留回调访问已销毁对象。</summary>
        private void OnDestroy()
        {
            entries.Callback -= OnEntryChanged;
        }

        /// <inheritdoc/>
        public override void OnStartServer()
        {
            base.OnStartServer();

            // 权威端：核心创建权威容器（尺寸非法由核心回退默认并记警告）并接线「自己为主端」。
            core = new InventoryHostCore(rows, columns, LogModule.Network, name);
            core.Catalog = catalog;
            core.IsAuthority = () => IsServerInventory;
            core.IdGeneratorProvider = ResolveGenerator;
            core.ForwardSetStackCount = RequestSetStackCount;
            core.ForwardRemoveItem = RequestRemoveItem;
            core.ForwardSplitStack = RequestSplitStack;
            core.CommitConfirm = (ItemId requiredItem, out string confirmError) => SyncEntries(out confirmError, requiredItem);

            projection.Clear();
            mirror.Clear();
            syncedRevision = -1;
            DesyncCount = 0;
            LastError = null;
            entries.Clear();

            BackpackLog.Info(
                LogModule.Network,
                $"库存网络组件就绪（服务端）：netId={netId}，容器 {core.Container.Rows}×{core.Container.Columns}。");

            core.SubscribeCommands(core.Container);
        }

        /// <inheritdoc/>
        public override void OnStopServer()
        {
            if (core != null) core.UnsubscribeCommands();
            core = null;
            fallbackGenerator = null;
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

            // 纯客户端：核心不持有容器，只把意图转发为 Command。
            core = new InventoryHostCore(LogModule.Network, name);
            core.Catalog = catalog;
            core.IsAuthority = () => false;
            core.ForwardSetStackCount = RequestSetStackCount;
            core.ForwardRemoveItem = RequestRemoveItem;
            core.ForwardSplitStack = RequestSplitStack;

            RebuildMirror();
            core.SubscribeCommands(this);
        }

        /// <inheritdoc/>
        public override void OnStopClient()
        {
            if (core != null) core.UnsubscribeCommands();
            core = null;
            mirror.Clear();
            projection.Clear();
            RaiseChanged();
            base.OnStopClient();
        }

        // 客户端意图（Command）：只表达意图，最终状态由服务端复核后决定。

        /// <summary>请求修改堆叠数（客户端入口，主机模式同样走 Command 本地执行）；标识必须是服务端已下发的标识。</summary>
        public void RequestSetStackCount(ItemId itemId, int stackCount)
        {
            if (!CanSendRequest("修改堆叠数", itemId)) return;
            CmdRequestSetStackCount(itemId.Value, stackCount);
        }

        /// <summary>请求移除物品（客户端入口）。</summary>
        public void RequestRemoveItem(ItemId itemId)
        {
            if (!CanSendRequest("移除物品", itemId)) return;
            CmdRequestRemoveItem(itemId.Value);
        }

        /// <summary>请求从某个堆叠中拆出指定数量（客户端入口）；拆分创建新物品因此必须走服务端，新实例由服务端从源物品的快照恢复、数值原样继承不重新掷点，合法数量为 1 .. 源堆叠数 - 1。</summary>
        public void RequestSplitStack(ItemId itemId, int splitCount)
        {
            if (!CanSendRequest("拆分堆叠", itemId)) return;
            CmdRequestSplitStack(itemId.Value, splitCount);
        }

        /// <summary>接收堆叠修改请求并转交服务端校验；拒绝原因由共享核心或宿主守卫记录。</summary>
        [Command(requiresAuthority = true)]
        private void CmdRequestSetStackCount(ulong itemId, int stackCount)
        {
            TryServerSetStackCount(new ItemId(itemId), stackCount, out _);
        }

        /// <summary>接收移除请求并转交服务端校验；拒绝原因由共享核心或宿主守卫记录。</summary>
        [Command(requiresAuthority = true)]
        private void CmdRequestRemoveItem(ulong itemId)
        {
            TryServerRemoveItem(new ItemId(itemId), out _);
        }

        /// <summary>接收拆分请求并转交服务端校验；拒绝原因由共享核心或宿主守卫记录。</summary>
        [Command(requiresAuthority = true)]
        private void CmdRequestSplitStack(ulong itemId, int splitCount)
        {
            if (!TryServerSplitStack(new ItemId(itemId), splitCount, out ItemId newItemId, out _))
            {
                return;
            }

            BackpackLog.Info(
                LogModule.Interaction,
                $"服务端拆分成功：源 item={itemId}，已拆出 {splitCount} 件（新标识 {newItemId}），当前 {ItemCount} 件。");
        }

        /// <summary>命令类请求的本地前置判定：未启动客户端 / 非本人库存 / 标识无效一律不发送。</summary>
        private bool CanSendRequest(string action, ItemId itemId)
        {
            if (!isClient)
            {
                BackpackLog.Warn(LogModule.Network, $"未启动客户端，忽略{action}请求：item={itemId}。");
                return false;
            }

            if (!isOwned)
            {
                BackpackLog.Warn(LogModule.Network, $"非本人库存，拒绝{action}请求：item={itemId}。");
                return false;
            }

            if (!itemId.IsValid)
            {
                BackpackLog.Warn(LogModule.Network, $"{action}请求的物品标识无效（None）。");
                return false;
            }

            return true;
        }

        // 意图命令的订阅与分发已收进 InventoryHostCore（权威端直接执行、纯客户端转发为 Command），宿主只提供 Request* 与 TryServer* 两端。

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

        /// <summary>服务端从某个堆叠中拆出指定数量并返回新实例标识：核心按「源物品存在 → 可堆叠 → 数量边界（1 .. 堆叠数 - 1）→ 模板可用 → 快照可捕获 → 存在能容纳拆分结果的空白区域」依次复核，任一步失败都不修改任何状态；新实例由源物品快照恢复（换新标识 + 拆分数量），数值原样继承不重新掷点，落点由服务端在空白区域中选取，网格位置仍不同步。</summary>
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

            if (Container.Revision == syncedRevision)
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
            RebuildMirror();
            return true;
        }

        // 只读视图（服务端 = 权威容器；客户端 = 镜像）。

        /// <summary>按标识查找物品（服务端查权威容器，客户端查镜像）。</summary>
        public bool TryGetItem(ItemId itemId, out RuntimeItem item)
        {
            item = null;
            if (!itemId.IsValid) return false;
            if (IsServerInventory) return core.TryGetItem(itemId, out item);
            return mirror.TryGetValue(itemId.Value, out item);
        }

        /// <summary>按标识升序复制当前物品（确定性顺序，供调用方只读遍历；先清空目标列表）。</summary>
        public void CopyItems(List<RuntimeItem> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            if (IsServerInventory)
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
            string text = $"{mode} 物品={ItemCount} 条目={entries.Count} 拒绝={DesyncCount}";
            return LastError == null ? text : text + " 最近错误=" + LastError;
        }

        // 内部：投影同步与镜像维护。

        /// <summary>把权威容器差异投影到 <see cref="entries"/>：其它物品因缺少标识或模板被拒绝只记诊断，仅 requiredItem（本次新增 / 修改的物品）未进入投影才算失败。</summary>
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

            if (requiredItem.IsValid && !projection.TryGetItem(requiredItem, out _))
            {
                error = $"物品 {requiredItem} 未能进入投影：{report.FirstError}";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>客户端按当前目录与同步条目整体重建镜像，并汇总被拒绝的条目。</summary>
        private void RebuildMirror()
        {
            int rejected = projection.RestoreAll(entries, catalog, mirror, out string firstError);
            if (rejected > 0)
            {
                RegisterFailure("镜像恢复被拒绝：" + firstError);
                BackpackLog.Error(
                    LogModule.Network,
                    $"客户端镜像有 {rejected} 个条目无法恢复（首个原因：{firstError}）。");
            }
            else
            {
                LastError = null;
            }

            BackpackLog.Info(
                LogModule.Network,
                $"客户端库存镜像已建立：{mirror.Count} 件（初始条目 {entries.Count} 个）。");
            RaiseChanged();
        }

        /// <summary>客户端增量跟随同步列表的操作维护镜像；服务端是权威源，直接跳过。</summary>
        private void OnEntryChanged(
            SyncList<NetworkInventoryEntry>.Operation op,
            int index,
            NetworkInventoryEntry oldItem,
            NetworkInventoryEntry newItem)
        {
            // 服务端是权威源，条目变化即自身状态变化，不需要维护镜像。
            if (isServer) return;

            switch (op)
            {
                case SyncList<NetworkInventoryEntry>.Operation.OP_ADD:
                case SyncList<NetworkInventoryEntry>.Operation.OP_INSERT:
                case SyncList<NetworkInventoryEntry>.Operation.OP_SET:
                    ApplyToMirror(newItem);
                    break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_REMOVEAT:
                    mirror.Remove(oldItem.itemId);
                    break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_CLEAR:
                    mirror.Clear();
                    break;
            }

            RaiseChanged();
        }

        /// <summary>把单个条目恢复为镜像中的运行时实例；恢复失败则从镜像移除并记为不同步。</summary>
        private void ApplyToMirror(NetworkInventoryEntry entry)
        {
            if (!projection.TryRestoreItem(entry, catalog, out RuntimeItem item, out string error))
            {
                mirror.Remove(entry.itemId);
                RegisterFailure("条目恢复失败：" + error);
                BackpackLog.Error(LogModule.Network, $"客户端拒绝条目 {entry.itemId}：{error}");
                return;
            }

            mirror[entry.itemId] = item;
        }

        /// <summary>取服务端标识生成器：优先用 NetworkManager 上的实例，缺失时启用兜底实例并记一次警告。</summary>
        private ItemIdGenerator ResolveGenerator()
        {
            if (NetworkManager.singleton is BackpackNetworkManager backpackManager && backpackManager.ServerItemIds != null)
            {
                return backpackManager.ServerItemIds;
            }

            if (fallbackGenerator == null)
            {
                fallbackGenerator = new ItemIdGenerator();
                BackpackLog.Warn(
                    LogModule.Network,
                    "未找到 BackpackNetworkManager.ServerItemIds，本组件启用兜底标识生成器（仅调试用）。");
            }

            return fallbackGenerator;
        }

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
            if (!changedPending) return;

            changedPending = false;
            Changed?.Invoke();

            // 权威端由容器自行广播 Inv.Changed（source = 容器），客户端只能由宿主代发。
            if (!IsServerInventory)
            {
                EventBus.Global.Publish(InvChannels.Changed, this);
            }
        }
    }
}
