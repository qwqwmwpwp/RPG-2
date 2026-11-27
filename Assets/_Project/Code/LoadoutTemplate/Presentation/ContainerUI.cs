using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using RPG4.Backpack.Core;

namespace RPG4.LoadoutTemplate
{
    /// <summary>独立模板容器：只投影外部快照，不拥有或提交真实库存。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public sealed class ContainerUI : MonoBehaviour
    {
        [Header("容器身份")]
        [SerializeField] private ContainerKind containerType = ContainerKind.ChestRig;
        [SerializeField] private string initialContainerId = "chest-rig";
        [Header("界面引用")]
        [SerializeField] private TMP_Text title;
        [SerializeField] private ContainerSlotUI containerSlot;
        [SerializeField] private RectTransform gridRoot;
        [SerializeField] private RectTransform itemLayer;
        [SerializeField] private RectTransform bodyRoot;
        [SerializeField, Min(0f)] private float minimumBodyHeight = 222f;
        [SerializeField, Min(0f)] private float headerHeight = 28f;
        [Header("初始布局")]
        [Tooltip("初始列数和行数；实际容器快照提供有效格坐标时，以快照形状为准。")]
        [SerializeField] private Vector2Int initialGridSize = new Vector2Int(3, 4);
        [Tooltip("Rectangle 使用行列数；Coordinates 使用有效坐标集合；AuthoredCells 使用预置格子的坐标。")]
        [SerializeField] private ContainerLayoutMode layoutMode = ContainerLayoutMode.Rectangle;
        [Tooltip("Coordinates 模式的有效格坐标；不存在的坐标构成布局中的空洞。")]
        [SerializeField] private Vector2Int[] initialValidCells = Array.Empty<Vector2Int>();
        [Tooltip("可选的预置格子，仅作为复用池；失效、重复或不属于当前网格的引用会跳过，不影响从预制体生成。")]
        [SerializeField] private CellUI[] cells = Array.Empty<CellUI>();
        [Tooltip("用于按容器布局生成格子的预制体，根节点须挂载 CellUI。")]
        [SerializeField] private CellUI cellPrefab;
        [SerializeField] private ItemViewUI itemPrefab;
        [SerializeField] private Vector2 cellSize = new Vector2(70f, 70f);
        [SerializeField] private Vector2 spacing;

        private IReadOnlyList<Vector2Int> initialCoordinates = Array.Empty<Vector2Int>();
        private ContainerDisplayProjection projection = ContainerDisplayProjection.Empty;
        private ContainerGridView gridView = new ContainerGridView();
        private bool initialized;
        private bool hasSnapshot;
        private bool connectionMode;
        private bool endpointReady;
        private bool snapshotRefreshPending;
        private object endpointSource;
        private object subscribedSource;
        private EventSubscription endpointChangedSubscription;
        private EventSubscription snapshotSubscription;
        private EventSubscription visualSubscription;
        private int snapshotRequest;
        private int receivedSnapshotRequest = -1;
        private ContainerDisplaySnapshot snapshot;
        private LoadoutWindowSession session;
        private RectTransform dragLayer;
        private object slotEndpoint;
        private string containerId;

        public ContainerKind ContainerType => containerType;
        public string ContainerId => initialized ? containerId : initialContainerId;
        public int BindingVersion { get; private set; }
        public Sprite ContainerIcon { get; private set; }
        public object Session => session;
        public RectTransform DragLayer => dragLayer;
        public ContainerDisplaySnapshot Snapshot => snapshot;
        public int ViewId => GetInstanceID();
        public bool HasPlacementPreview => gridView.HasPlacementPreview;
        public ContainerSlotUI Slot => containerSlot;
        public bool IsReady => initialized && hasSnapshot && isActiveAndEnabled
            && (!connectionMode || (endpointReady && session != null && session.IsActive));
        public event Action<ContainerUI> Changed;

        /// <summary>初始化标题和容器槽，并隐藏尚未绑定的预置格。</summary>
        private void Awake()
        {
            if (title != null) title.text = TitleFor(containerType);
            if (session != null && session.IsActive) return;
            foreach (CellUI cell in cells ?? Array.Empty<CellUI>())
                if (cell != null && cell.transform.parent == gridRoot) cell.gameObject.SetActive(false);
            containerSlot?.Bind(this);
            containerSlot?.BindEndpoint(null, null);
            containerSlot?.Apply(null);
        }

        /// <summary>恢复当前会话的显示订阅并安排快照刷新。</summary>
        private void OnEnable()
        {
            SubscribeVisual();
            SubscribeEndpoint();
            if (connectionMode && session != null && session.IsActive) snapshotRefreshPending = true;
        }

        /// <summary>在帧末断开已结束的会话或同步待刷新的快照。</summary>
        private void LateUpdate()
        {
            if (!connectionMode) return;
            if (session == null || !session.IsActive) { Disconnect(); return; }
            if (snapshotRefreshPending) SynchronizeEndpoint(out _);
        }

        /// <summary>取消交互和订阅，更新绑定版本并清除反馈。</summary>
        private void OnDisable()
        {
            CancelInteractions();
            UnsubscribeEndpoint();
            visualSubscription.Dispose();
            endpointReady = false;
            BindingVersion++;
            ClearFeedback();
            Changed?.Invoke(this);
        }

        /// <summary>连接结果由 UI 管理器注入，数据端点只用作事件地址。</summary>
        public void BindConnection(LoadoutWindowSession value, object storageSource, object itemSource,
            ContainerDisplaySnapshot initialSnapshot)
        {
            Disconnect();
            if (value == null || !value.IsActive || storageSource == null) return;
            connectionMode = true;
            session = value;
            endpointSource = storageSource;
            slotEndpoint = itemSource;
            containerSlot?.SetBodyVisible(itemSource != null);
            SubscribeVisual();
            SubscribeEndpoint();
            containerSlot?.Bind(this);
            containerSlot?.BindEndpoint(slotEndpoint, session, dragLayer);
            if (initialSnapshot != null && ReferenceEquals(initialSnapshot.EventSource, endpointSource))
            {
                if (!ApplySnapshotCore(initialSnapshot, true, out string error))
                {
                    InvalidateView();
                    Debug.LogWarning(error, this);
                }
            }
            else snapshotRefreshPending = true;
        }

        /// <summary>撤销当前连接和槽位绑定，使显示状态失效。</summary>
        public void Disconnect()
        {
            CancelInteractions();
            UnsubscribeEndpoint();
            visualSubscription.Dispose();
            endpointSource = null;
            slotEndpoint = null;
            session = null;
            connectionMode = false;
            snapshotRefreshPending = false;
            containerSlot?.BindEndpoint(null, null);
            containerSlot?.SetBodyVisible(true);
            InvalidateView();
        }

        /// <summary>依次取消物品和容器槽上正在进行的交互。</summary>
        private void CancelInteractions()
        {
            gridView.CancelInteractions();
            containerSlot?.CancelInteraction();
        }

        /// <summary>在当前有效会话上重新订阅拖动显示命令。</summary>
        private void SubscribeVisual()
        {
            visualSubscription.Dispose();
            if (session != null && session.IsActive && isActiveAndEnabled)
                visualSubscription = EventBus.Global.Subscribe<LoadoutVisualCommand>(
                    LoadoutChannels.Visual, session, this, OnVisual);
        }

        /// <summary>按接收到的会话命令更新放置预览。</summary>
        private void OnVisual(LoadoutVisualCommand command)
        {
            if (!ReferenceEquals(command.Session, session)) return;
            if (command.Kind == LoadoutVisualKind.Finish) { ClearFeedback(); return; }
            if (command.Kind == LoadoutVisualKind.Preview)
            {
                gridView.ClearFeedback();
                if (command.TargetViewId == ViewId) ShowPlacement(command.Preview);
            }
        }

        /// <summary>订阅当前数据端点的变化通知和快照回复。</summary>
        private void SubscribeEndpoint()
        {
            if (!isActiveAndEnabled || ReferenceEquals(subscribedSource, endpointSource)) return;
            UnsubscribeEndpoint();
            if (endpointSource == null || session == null || !session.IsActive) return;
            subscribedSource = endpointSource;
            endpointChangedSubscription = EventBus.Global.Subscribe(
                LoadoutChannels.Changed, subscribedSource, this, OnEndpointChanged);
            snapshotSubscription = EventBus.Global.Subscribe<LoadoutSnapshotReply>(
                LoadoutChannels.SnapshotReceived, this, this, OnSnapshot);
        }

        /// <summary>释放端点订阅并作废尚未处理的请求编号。</summary>
        private void UnsubscribeEndpoint()
        {
            endpointChangedSubscription.Dispose();
            snapshotSubscription.Dispose();
            subscribedSource = null;
            snapshotRequest++;
        }

        /// <summary>使旧视图失效并安排下一次快照同步。</summary>
        private void OnEndpointChanged()
        {
            InvalidateView();
            snapshotRefreshPending = true;
        }

        /// <summary>请求当前端点快照并核对同步回复编号。</summary>
        private bool SynchronizeEndpoint(out string error)
        {
            error = null;
            snapshotRefreshPending = false;
            if (!isActiveAndEnabled || endpointSource == null || session == null || !session.IsActive)
            {
                InvalidateView();
                return Fail("容器显示连接已失效。", out error);
            }
            SubscribeEndpoint();
            int request = ++snapshotRequest;
            EventBus.Global.Publish(LoadoutChannels.SnapshotRequested, endpointSource,
                new LoadoutSnapshotRequest(this, request));
            if (receivedSnapshotRequest != request)
            {
                InvalidateView();
                return Fail("容器数据端点未响应快照请求。", out error);
            }
            return true;
        }

        /// <summary>核对会话、请求编号和数据来源，再应用快照回复。</summary>
        private void OnSnapshot(LoadoutSnapshotReply reply)
        {
            if (!isActiveAndEnabled || session == null || !session.IsActive || reply.RequestId != snapshotRequest) return;
            receivedSnapshotRequest = reply.RequestId;
            if (reply.Snapshot == null || !ReferenceEquals(reply.Snapshot.EventSource, endpointSource))
            {
                InvalidateView();
                return;
            }
            if (!ApplySnapshotCore(reply.Snapshot, true, out string error))
            {
                InvalidateView();
                Debug.LogWarning(error, this);
            }
        }

        /// <summary>先废止绑定并取消交互，再隐藏视图和清空显示投影。</summary>
        private void InvalidateView()
        {
            endpointReady = false;
            snapshot = null;
            hasSnapshot = false;
            ContainerIcon = null;
            BindingVersion++;
            CancelInteractions();
            ClearFeedback();
            gridView.Hide(gridRoot, cells);
            projection = ContainerDisplayProjection.Empty;
            containerSlot?.Apply(null);
            Changed?.Invoke(this);
        }

        /// <summary>通过当前连接或初始配置建立可用的容器显示。</summary>
        private bool EnsureInitialized(out string error)
        {
            error = null;
            if (connectionMode)
            {
                if (initialized && endpointReady) return true;
                return SynchronizeEndpoint(out error);
            }
            if (initialized) return true;
            return Configure(new ContainerUIConfiguration
            {
                Id = initialContainerId, Kind = containerType, LayoutMode = layoutMode,
                GridSize = initialGridSize, ValidCells = initialValidCells,
                CellSize = cellSize, Spacing = spacing
            }, out error);
        }

        /// <summary>先验证完整布局和现有物品，再一次性应用外部配置。失败时保留已有显示状态。</summary>
        public bool Configure(ContainerUIConfiguration configuration, out string error)
        {
            error = null;
            if (connectionMode)
                return Fail("界面已建立数据连接，身份和有效格以事件快照为准。", out error);
            if (configuration == null) return Fail("未提供容器配置。", out error);
            if (!SupportedKind(configuration.Kind) || string.IsNullOrWhiteSpace(configuration.Id))
                return Fail("请配置容器类型和初始容器编号。", out error);
            if (hasSnapshot && (configuration.Kind != containerType
                || !string.Equals(configuration.Id, containerId, StringComparison.Ordinal)))
                return Fail("容器已绑定显示快照，配置不能改变其身份或类别；请使用对应容器的新快照重新绑定。", out error);
            ContainerGridLayout layout = CreateLayout(configuration.CellSize, configuration.Spacing);
            if (!layout.Validate(out error)) return false;
            ContainerGridView nextGrid = PrepareGridView();
            if (!ContainerDisplayProjection.TryBuildConfiguredShape(configuration, nextGrid.AuthoredCoordinates,
                out ContainerDisplayProjection next, out error)) return false;
            if (next.Coordinates.Count > nextGrid.CellCount && (cellPrefab == null || cellPrefab.RectTransform == null))
                return Fail("生成初始格子需要接线格子预制体。", out error);
            if (!next.TryIndexItems(projection.Items.Values, snapshot?.RejectContainers ?? true, out error)) return false;
            if (next.Items.Count > 0 && itemPrefab == null)
                return Fail("显示物品需要接线物品模板。", out error);

            containerType = configuration.Kind;
            initialContainerId = configuration.Id;
            layoutMode = configuration.LayoutMode;
            initialGridSize = configuration.GridSize;
            initialValidCells = configuration.ValidCells == null ? Array.Empty<Vector2Int>()
                : (Vector2Int[])configuration.ValidCells.Clone();
            cellSize = configuration.CellSize;
            spacing = configuration.Spacing;
            initialCoordinates = next.Coordinates;
            containerId = initialContainerId;
            projection = next;
            gridView = nextGrid;
            initialized = true;
            BindingVersion++;
            containerSlot?.Bind(this);
            containerSlot?.BindEndpoint(slotEndpoint, session, dragLayer);
            containerSlot?.Apply(ContainerIcon);
            if (title != null) title.text = TitleFor(containerType);
            gridView.Render(this, layout, projection, cellPrefab, itemPrefab);
            ClearFeedback();
            Changed?.Invoke(this);
            return true;
        }

        /// <summary>完整校验通过后才替换显示缓存；拒绝快照不会清空旧物品或改写旧形状。</summary>
        public bool ApplySnapshot(ContainerDisplaySnapshot snapshot, out string error)
        {
            error = null;
            if (connectionMode)
                return Fail("界面已建立数据连接，请通过事件同步最新快照。", out error);
            if (!EnsureInitialized(out error)) return false;
            return ApplySnapshotCore(snapshot, false, out error);
        }

        /// <summary>验证快照与显示资源，换版后再绑定和刷新视图。</summary>
        private bool ApplySnapshotCore(ContainerDisplaySnapshot snapshot, bool allowRebind, out string error)
        {
            error = null;
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Id))
                return Fail("容器快照及容器编号不能为空。", out error);
            if (snapshot.Kind != ContainerKind.None && !SupportedKind(snapshot.Kind)) return Fail("容器快照类别无效。", out error);
            if (!allowRebind && snapshot.Kind != containerType)
                return Fail("容器快照类型与当前面板不匹配。", out error);
            if (allowRebind && snapshot.ValidCells == null)
                return Fail("容器数据快照必须提供有效格坐标。", out error);
            ContainerGridLayout layout = CreateLayout(cellSize, spacing);
            if (!layout.Validate(out error)) return false;
            ContainerGridView nextGrid = PrepareGridView();
            if (!ContainerDisplayProjection.TryFromSnapshot(snapshot, initialCoordinates,
                out ContainerDisplayProjection next, out error)) return false;
            if (next.Coordinates.Count > nextGrid.CellCount && (cellPrefab == null || cellPrefab.RectTransform == null))
                return Fail("新形状需要更多格子，请接线格子预制体。", out error);
            if (next.Items.Count > 0 && (itemPrefab == null || !(itemPrefab.transform is RectTransform)))
                return Fail("显示物品需要带矩形节点的物品预制体。", out error);

            if (!initialized) initialCoordinates = next.Coordinates;
            gridView = nextGrid;
            projection = next;
            containerId = snapshot.Id;
            containerType = snapshot.Kind;
            ContainerIcon = snapshot.Icon;
            this.snapshot = snapshot;
            initialized = true;
            hasSnapshot = true;
            if (allowRebind) endpointReady = snapshot.IsReady;
            // 在视图重建之前换版，旧拖拽在子物体失活时也能识别为过时状态。
            BindingVersion++;
            containerSlot?.Bind(this);
            containerSlot?.BindEndpoint(slotEndpoint, session, dragLayer);
            gridView.Render(this, layout, projection, cellPrefab, itemPrefab);
            if (title != null) title.text = TitleFor(containerType);
            containerSlot?.Apply(ContainerIcon);
            containerSlot?.SetDimmed(false);
            ClearFeedback();
            Changed?.Invoke(this);
            return true;
        }

        /// <summary>验证网络容器展示所需的布局和预制体。</summary>
        internal bool ValidateNetworkConfiguration(out string error)
        {
            if (!CreateLayout(cellSize, spacing).Validate(out error)) return false;
            if (cellPrefab == null || itemPrefab == null)
                return Fail("联机背包缺少格子或物品视图预制体。", out error);
            return true;
        }

        /// <summary>以当前接线构造布局，允许先用候选尺寸验证配置。</summary>
        private ContainerGridLayout CreateLayout(Vector2 size, Vector2 gap) =>
            new ContainerGridLayout(gridRoot, itemLayer, bodyRoot, minimumBodyHeight, headerHeight, size, gap);

        /// <summary>首次绑定时准备预置格子池；只有通过完整校验后才由面板持有。</summary>
        private ContainerGridView PrepareGridView()
        {
            if (initialized) return gridView;
            var candidate = new ContainerGridView();
            candidate.CollectAuthoredCells(this, gridRoot, cells);
            return candidate;
        }

        /// <summary>按当前占格索引读取覆盖指定格子的物品。</summary>
        public bool TryGetItemAt(Vector2Int coordinate, out ItemDisplaySnapshot item) =>
            projection.TryGetItemAt(coordinate, out item);

        /// <summary>读取当前快照中的指定物品。</summary>
        public bool TryGetItem(string id, out ItemDisplaySnapshot item) => projection.TryGetItem(id, out item);

        /// <summary>查找仍属于当前投影的物品视图。</summary>
        public bool TryGetItemView(string id, out ItemViewUI view)
        {
            view = null;
            return projection.TryGetItem(id, out _) && gridView.TryGetItemView(id, out view);
        }

        /// <summary>恢复当前快照中的位置；已被新快照移除的物品不会重新生成。</summary>
        public void RestoreItemVisual(string id)
        {
            if (projection.TryGetItem(id, out ItemDisplaySnapshot item))
                gridView.RestoreItem(this, CreateLayout(cellSize, spacing), item);
        }

        /// <summary>确认面板可交互且布局中存在指定格子。</summary>
        public bool IsValidCell(Vector2Int coordinate) => IsReady && projection.ContainsCell(coordinate);

        /// <summary>将屏幕点映射到就绪面板上的有效格。</summary>
        public bool TryResolveCell(Vector2 screen, Camera camera, out Vector2Int coordinate)
        {
            coordinate = default;
            return IsReady && CreateLayout(cellSize, spacing).TryResolveCell(screen, camera, out coordinate)
                && projection.ContainsCell(coordinate);
        }

        /// <summary>显示拖动操作给出的占格预览。</summary>
        public void ShowPlacement(PlacementPreview preview) => gridView.ShowPlacement(preview);

        /// <summary>清除网格和容器槽上的交互反馈。</summary>
        public void ClearFeedback()
        {
            gridView.ClearFeedback();
            SetSlotFeedback(CellFeedback.Normal);
        }

        /// <summary>计算物品当前占地对应的像素尺寸。</summary>
        public Vector2 GetItemPixelSize(Vector2Int size) => CreateLayout(cellSize, spacing).GetItemPixelSize(size);

        /// <summary>更新容器槽的交互反馈。</summary>
        public void SetSlotFeedback(CellFeedback feedback)
        {
            if (containerSlot != null) containerSlot.SetFeedback(feedback);
        }

        /// <summary>判断类别是否属于有网格面板的容器。</summary>
        private static bool SupportedKind(ContainerKind value) => value == ContainerKind.ChestRig
            || value == ContainerKind.Pocket || value == ContainerKind.Backpack || value == ContainerKind.SafeBox;
        /// <summary>返回容器显示操作失败的原因。</summary>
        private static bool Fail(string message, out string error) { error = message; return false; }
        /// <summary>取得容器类型对应的面板标题。</summary>
        private static string TitleFor(ContainerKind kind)
        {
            switch (kind)
            {
                case ContainerKind.ChestRig: return "战术弹挂";
                case ContainerKind.Pocket: return "口袋";
                case ContainerKind.Backpack: return "背包";
                case ContainerKind.SafeBox: return "安全箱";
                default: return string.Empty;
            }
        }
    }
}
