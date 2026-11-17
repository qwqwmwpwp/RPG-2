using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
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

        private readonly List<Vector2Int> initialCoordinates = new List<Vector2Int>();
        private readonly List<Vector2Int> authoredCoordinates = new List<Vector2Int>();
        private readonly List<CellUI> cellPool = new List<CellUI>();
        private readonly Dictionary<Vector2Int, CellUI> cellViews = new Dictionary<Vector2Int, CellUI>();
        private readonly Dictionary<string, ItemViewUI> itemViews = new Dictionary<string, ItemViewUI>(StringComparer.Ordinal);
        private readonly Vector3[] extentCorners = new Vector3[4];
        private HashSet<Vector2Int> validCells = new HashSet<Vector2Int>();
        private Dictionary<Vector2Int, string> occupancy = new Dictionary<Vector2Int, string>();
        private Dictionary<string, ItemDisplaySnapshot> items = new Dictionary<string, ItemDisplaySnapshot>(StringComparer.Ordinal);
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
        private LayoutGroup gridLayout;

        public ContainerKind ContainerType => containerType;
        public string ContainerId => initialized ? containerId : initialContainerId;
        public int BindingVersion { get; private set; }
        public Sprite ContainerIcon { get; private set; }
        public object Session => session;
        public RectTransform DragLayer => dragLayer;
        public ContainerDisplaySnapshot Snapshot => snapshot;
        public int ViewId => GetInstanceID();
        public bool HasPlacementPreview { get; private set; }
        public ContainerSlotUI Slot => containerSlot;
        public bool IsReady => initialized && hasSnapshot && isActiveAndEnabled
            && (!connectionMode || (endpointReady && session != null && session.IsActive));
        public event Action<ContainerUI> Changed;

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

        private void OnEnable()
        {
            SubscribeVisual();
            SubscribeEndpoint();
            if (connectionMode && session != null && session.IsActive) snapshotRefreshPending = true;
        }

        private void LateUpdate()
        {
            if (!connectionMode) return;
            if (session == null || !session.IsActive) { Disconnect(); return; }
            if (snapshotRefreshPending) SynchronizeEndpoint(out _);
        }

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

        private void CancelInteractions()
        {
            foreach (ItemViewUI view in itemViews.Values)
                if (view != null) view.CancelInteraction();
            containerSlot?.CancelInteraction();
        }

        private void SubscribeVisual()
        {
            visualSubscription.Dispose();
            if (session != null && session.IsActive && isActiveAndEnabled)
                visualSubscription = EventBus.Global.Subscribe<LoadoutVisualCommand>(
                    LoadoutChannels.Visual, session, this, OnVisual);
        }

        private void OnVisual(LoadoutVisualCommand command)
        {
            if (!ReferenceEquals(command.Session, session)) return;
            if (command.Kind == LoadoutVisualKind.Finish) { ClearFeedback(); return; }
            if (command.Kind == LoadoutVisualKind.Preview)
            {
                ClearGridFeedback();
                if (command.TargetViewId == ViewId) ShowPlacement(command.Preview);
            }
        }

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

        private void UnsubscribeEndpoint()
        {
            endpointChangedSubscription.Dispose();
            snapshotSubscription.Dispose();
            subscribedSource = null;
            snapshotRequest++;
        }

        private void OnEndpointChanged()
        {
            InvalidateView();
            snapshotRefreshPending = true;
        }

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

        private void InvalidateView()
        {
            endpointReady = false;
            snapshot = null;
            hasSnapshot = false;
            ContainerIcon = null;
            BindingVersion++;
            CancelInteractions();
            ClearFeedback();
            foreach (ItemViewUI view in itemViews.Values)
                if (view != null) view.gameObject.SetActive(false);
            foreach (CellUI cell in cellPool)
                if (cell != null) cell.gameObject.SetActive(false);
            foreach (CellUI cell in cells ?? Array.Empty<CellUI>())
                if (cell != null && cell.transform.parent == gridRoot) cell.gameObject.SetActive(false);
            items.Clear();
            occupancy.Clear();
            validCells.Clear();
            cellViews.Clear();
            containerSlot?.Apply(null);
            Changed?.Invoke(this);
        }

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
            if (gridRoot == null || itemLayer == null || itemLayer == gridRoot)
                return Fail("请接线格子根节点和独立物品层。", out error);
            if (itemLayer.parent != gridRoot)
                return Fail("物品层必须位于格子根节点下，共用左上原点。", out error);
            gridLayout = gridRoot.GetComponent<LayoutGroup>();
            if (gridLayout != null && gridLayout.enabled)
                return Fail("格子根节点不能启用自动布局；有效格须由显式坐标定位。", out error);
            if (!ValidMetrics(configuration.CellSize, configuration.Spacing))
                return Fail("格子尺寸和包含间距的步距必须为有限正数。", out error);
            if (bodyRoot != null && (!FiniteNonNegative(minimumBodyHeight) || !FiniteNonNegative(headerHeight)))
                return Fail("面板最小高度和标题高度必须为有限非负数。", out error);

            var references = new HashSet<CellUI>();
            var reusableCells = new List<CellUI>();
            foreach (CellUI cell in cells ?? Array.Empty<CellUI>())
            {
                // 用户删除预置格后，场景实例可能保留空覆盖；这些可选引用不能阻断默认生成。
                if (cell == null || cell.RectTransform == null || cell.transform.parent != gridRoot
                    || (cell.Owner != null && cell.Owner != this) || !references.Add(cell)) continue;
                reusableCells.Add(cell);
            }
            var originalCoordinates = new List<Vector2Int>();
            if (initialized) originalCoordinates.AddRange(authoredCoordinates);
            else foreach (CellUI cell in reusableCells) originalCoordinates.Add(cell.Coordinate);
            if (!TryBuildShape(configuration, originalCoordinates, out List<Vector2Int> shape, out error)) return false;
            int available = initialized ? cellPool.Count : reusableCells.Count;
            if (shape.Count > available && (cellPrefab == null || cellPrefab.RectTransform == null))
                return Fail("生成初始格子需要接线格子预制体。", out error);
            var nextValid = new HashSet<Vector2Int>(shape);
            if (!TryProject(items.Values, nextValid, out Dictionary<string, ItemDisplaySnapshot> nextItems,
                out Dictionary<Vector2Int, string> nextOccupancy, out error, snapshot?.RejectContainers ?? true)) return false;
            if (nextItems.Count > 0 && itemPrefab == null)
                return Fail("显示物品需要接线物品模板。", out error);

            containerType = configuration.Kind;
            initialContainerId = configuration.Id;
            layoutMode = configuration.LayoutMode;
            initialGridSize = configuration.GridSize;
            initialValidCells = configuration.ValidCells == null ? Array.Empty<Vector2Int>()
                : (Vector2Int[])configuration.ValidCells.Clone();
            cellSize = configuration.CellSize;
            spacing = configuration.Spacing;
            initialCoordinates.Clear();
            initialCoordinates.AddRange(shape);
            if (!initialized)
            {
                // 首次成功配置才缓存预置坐标；后续快照会改写池中 Cell 的运行时坐标。
                authoredCoordinates.AddRange(originalCoordinates);
                cellPool.AddRange(reusableCells);
            }
            containerId = initialContainerId;
            validCells = nextValid;
            occupancy = nextOccupancy;
            items = nextItems;
            initialized = true;
            BindingVersion++;
            containerSlot?.Bind(this);
            containerSlot?.BindEndpoint(slotEndpoint, session, dragLayer);
            containerSlot?.Apply(ContainerIcon);
            if (title != null) title.text = TitleFor(containerType);
            ApplyShape(shape);
            RefreshItems();
            ClearFeedback();
            Changed?.Invoke(this);
            return true;
        }

        private static bool TryBuildShape(ContainerUIConfiguration configuration, IReadOnlyList<Vector2Int> authored,
            out List<Vector2Int> shape, out string error)
        {
            shape = new List<Vector2Int>();
            error = null;
            switch (configuration.LayoutMode)
            {
                case ContainerLayoutMode.Rectangle:
                    long count = (long)configuration.GridSize.x * configuration.GridSize.y;
                    if (configuration.GridSize.x < 1 || configuration.GridSize.x > BackpackContainerData.MaxColumns
                        || configuration.GridSize.y < 1 || count > int.MaxValue)
                        return Fail("初始格子列数必须在 1 到 5 之间，行数须为正数且总格数不能超出索引范围。", out error);
                    for (int y = 0; y < configuration.GridSize.y; y++)
                    for (int x = 0; x < configuration.GridSize.x; x++) shape.Add(new Vector2Int(x, y));
                    break;
                case ContainerLayoutMode.AuthoredCells:
                    foreach (Vector2Int coordinate in authored) shape.Add(coordinate);
                    break;
                case ContainerLayoutMode.Coordinates:
                    if (configuration.ValidCells != null) shape.AddRange(configuration.ValidCells);
                    break;
                default: return Fail("容器布局模式无效。", out error);
            }
            var unique = new HashSet<Vector2Int>();
            foreach (Vector2Int coordinate in shape)
                if (!ValidCoordinate(coordinate) || !unique.Add(coordinate))
                    return Fail("有效格坐标无效或重复；列坐标必须在 0 到 4 之间。", out error);
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
            if (!ValidateView(out error)) return false;

            var reusableCells = new List<CellUI>();
            var originalCoordinates = new List<Vector2Int>();
            if (!initialized)
            {
                var references = new HashSet<CellUI>();
                foreach (CellUI cell in cells ?? Array.Empty<CellUI>())
                {
                    if (cell == null || cell.RectTransform == null || cell.transform.parent != gridRoot
                        || (cell.Owner != null && cell.Owner != this) || !references.Add(cell)) continue;
                    reusableCells.Add(cell);
                    originalCoordinates.Add(cell.Coordinate);
                }
            }

            var shape = new List<Vector2Int>();
            var nextValid = new HashSet<Vector2Int>();
            IReadOnlyList<Vector2Int> sourceShape = snapshot.ValidCells ?? initialCoordinates;
            foreach (Vector2Int coordinate in sourceShape)
            {
                if (!ValidCoordinate(coordinate) || !nextValid.Add(coordinate))
                    return Fail("快照有效格坐标无效或重复；列坐标必须在 0 到 4 之间。", out error);
                shape.Add(coordinate);
            }
            if (!TryProject(snapshot.Items, nextValid, out Dictionary<string, ItemDisplaySnapshot> nextItems,
                out Dictionary<Vector2Int, string> nextOccupancy, out error, snapshot.RejectContainers)) return false;
            int available = initialized ? cellPool.Count : reusableCells.Count;
            if (shape.Count > available && (cellPrefab == null || cellPrefab.RectTransform == null))
                return Fail("新形状需要更多格子，请接线格子预制体。", out error);
            if (nextItems.Count > 0 && (itemPrefab == null || !(itemPrefab.transform is RectTransform)))
                return Fail("显示物品需要带矩形节点的物品预制体。", out error);

            if (!initialized)
            {
                authoredCoordinates.AddRange(originalCoordinates);
                cellPool.AddRange(reusableCells);
                initialCoordinates.Clear();
                initialCoordinates.AddRange(shape);
            }
            validCells = nextValid;
            occupancy = nextOccupancy;
            items = nextItems;
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
            ApplyShape(shape);
            RefreshItems();
            if (title != null) title.text = TitleFor(containerType);
            containerSlot?.Apply(ContainerIcon);
            containerSlot?.SetDimmed(false);
            ClearFeedback();
            Changed?.Invoke(this);
            return true;
        }

        internal bool ValidateNetworkConfiguration(out string error)
        {
            if (!ValidateView(out error)) return false;
            if (cellPrefab == null || itemPrefab == null)
                return Fail("联机背包缺少格子或物品视图预制体。", out error);
            return true;
        }

        private bool ValidateView(out string error)
        {
            error = null;
            if (gridRoot == null || itemLayer == null || itemLayer == gridRoot)
                return Fail("请接线格子根节点和独立物品层。", out error);
            if (itemLayer.parent != gridRoot)
                return Fail("物品层必须位于格子根节点下，共用左上原点。", out error);
            gridLayout = gridRoot.GetComponent<LayoutGroup>();
            if (gridLayout != null && gridLayout.enabled)
                return Fail("格子根节点不能启用自动布局；有效格须由显式坐标定位。", out error);
            if (!ValidMetrics(cellSize, spacing))
                return Fail("格子尺寸和包含间距的步距必须为有限正数。", out error);
            if (bodyRoot != null && (!FiniteNonNegative(minimumBodyHeight) || !FiniteNonNegative(headerHeight)))
                return Fail("面板最小高度和标题高度必须为有限非负数。", out error);
            return true;
        }

        private static bool TryProject(IEnumerable<ItemDisplaySnapshot> source, HashSet<Vector2Int> shape,
            out Dictionary<string, ItemDisplaySnapshot> projected,
            out Dictionary<Vector2Int, string> occupied, out string error, bool rejectContainers = true)
        {
            projected = new Dictionary<string, ItemDisplaySnapshot>(StringComparer.Ordinal);
            occupied = new Dictionary<Vector2Int, string>();
            error = null;
            if (source == null) return true;
            foreach (ItemDisplaySnapshot item in source)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || projected.ContainsKey(item.Id))
                    return Fail("物品快照不能包含空物品、空编号或重复编号。", out error);
                PlacementPreview check = Evaluate(item, item.Origin, null, shape, occupied, rejectContainers);
                if (!check.Allowed) return Fail(check.Reason, out error);
                projected.Add(item.Id, item);
                for (int y = 0; y < item.Size.y; y++)
                for (int x = 0; x < item.Size.x; x++)
                    occupied.Add(item.Origin + new Vector2Int(x, y), item.Id);
            }
            return true;
        }

        private void ApplyShape(IReadOnlyList<Vector2Int> shape)
        {
            while (cellPool.Count < shape.Count)
                cellPool.Add(Instantiate(cellPrefab, gridRoot));
            cellViews.Clear();
            int width = 0, height = 0;
            for (int i = 0; i < cellPool.Count; i++)
            {
                CellUI cell = cellPool[i];
                bool active = i < shape.Count;
                if (active)
                {
                    Vector2Int coordinate = shape[i];
                    cell.Bind(this, coordinate);
                    SetRect(cell.RectTransform, ToPixel(coordinate), cellSize);
                    cell.SetFeedback(CellFeedback.Normal);
                    cellViews.Add(coordinate, cell);
                    width = Math.Max(width, coordinate.x + 1);
                    height = Math.Max(height, coordinate.y + 1);
                }
                if (cell.gameObject.activeSelf != active) cell.gameObject.SetActive(active);
            }
            Vector2 extent = GetItemPixelSize(new Vector2Int(width, height));
            SetRect(itemLayer, Vector2.zero, extent);
            ApplyExtent(extent);
            itemLayer.SetAsLastSibling();
        }

        private void ApplyExtent(Vector2 extent)
        {
            gridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, extent.x);
            gridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, extent.y);
            if (bodyRoot == null || bodyRoot.parent != transform || !(transform is RectTransform panel)
                || !gridRoot.IsChildOf(bodyRoot)) return;

            // 面板高度跟随内容；面板与 Body 的宽度仍由外部布局维护。
            float bodyHeight = Mathf.Max(minimumBodyHeight, BottomExtent(gridRoot));
            RectTransform slotRect = containerSlot != null ? containerSlot.RectTransform : null;
            if (slotRect != null && slotRect.IsChildOf(bodyRoot)) bodyHeight = Mathf.Max(bodyHeight, BottomExtent(slotRect));
            bodyRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, bodyHeight);
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, headerHeight + bodyHeight);
        }

        private float BottomExtent(RectTransform rect)
        {
            rect.GetWorldCorners(extentCorners);
            float extent = 0f;
            foreach (Vector3 corner in extentCorners)
                extent = Mathf.Max(extent, bodyRoot.rect.yMax - bodyRoot.InverseTransformPoint(corner).y);
            return extent;
        }

        private void RefreshItems()
        {
            var removed = new List<string>();
            foreach (KeyValuePair<string, ItemViewUI> entry in itemViews)
                if (!items.ContainsKey(entry.Key)) removed.Add(entry.Key);
            foreach (string id in removed)
            {
                if (itemViews[id] != null)
                {
                    itemViews[id].gameObject.SetActive(false);
                    Destroy(itemViews[id].gameObject);
                }
                itemViews.Remove(id);
            }
            foreach (ItemDisplaySnapshot item in items.Values)
            {
                if (!itemViews.TryGetValue(item.Id, out ItemViewUI view) || view == null)
                {
                    view = Instantiate(itemPrefab, itemLayer);
                    itemViews[item.Id] = view;
                }
                Vector2 size = GetItemPixelSize(item.Size);
                if (view.transform.parent != itemLayer) view.transform.SetParent(itemLayer, false);
                SetRect((RectTransform)view.transform, ToPixel(item.Origin), size);
                view.Bind(this, item);
                view.Apply(item, size);
                view.SetDimmed(false);
                if (!view.gameObject.activeSelf) view.gameObject.SetActive(true);
            }
        }

        public bool TryGetItemAt(Vector2Int coordinate, out ItemDisplaySnapshot item)
        {
            item = null;
            return validCells.Contains(coordinate) && occupancy.TryGetValue(coordinate, out string id)
                && items.TryGetValue(id, out item);
        }

        public bool TryGetItem(string id, out ItemDisplaySnapshot item)
        {
            item = null;
            return id != null && items.TryGetValue(id, out item);
        }

        public bool TryGetItemView(string id, out ItemViewUI view)
        {
            view = null;
            return id != null && items.ContainsKey(id) && itemViews.TryGetValue(id, out view) && view != null;
        }

        /// <summary>恢复当前快照中的位置；已被新快照移除的物品不会重新生成。</summary>
        public void RestoreItemVisual(string id)
        {
            if (!TryGetItemView(id, out ItemViewUI view) || !items.TryGetValue(id, out ItemDisplaySnapshot item)) return;
            view.transform.SetParent(itemLayer, false);
            Vector2 size = GetItemPixelSize(item.Size);
            SetRect((RectTransform)view.transform, ToPixel(item.Origin), size);
            view.Bind(this, item);
            view.Apply(item, size);
            view.SetDragState(false);
            view.SetDimmed(false);
        }

        public bool IsValidCell(Vector2Int coordinate) => IsReady && validCells.Contains(coordinate);

        public bool TryResolveCell(Vector2 screen, Camera camera, out Vector2Int coordinate)
        {
            coordinate = default;
            if (!IsReady || gridRoot == null
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(gridRoot, screen, camera, out Vector2 local)) return false;
            float x = local.x - gridRoot.rect.xMin, y = gridRoot.rect.yMax - local.y;
            if (!FiniteNonNegative(x) || !FiniteNonNegative(y)) return false;
            float pitchX = cellSize.x + spacing.x, pitchY = cellSize.y + spacing.y;
            if (x / pitchX >= int.MaxValue || y / pitchY >= int.MaxValue) return false;
            int column = Mathf.FloorToInt(x / pitchX), row = Mathf.FloorToInt(y / pitchY);
            if (x - column * pitchX >= cellSize.x || y - row * pitchY >= cellSize.y) return false;
            coordinate = new Vector2Int(column, row);
            return validCells.Contains(coordinate);
        }

        public PlacementPreview EvaluatePlacement(ItemDisplaySnapshot item, Vector2Int origin, string ignoredItemId = null)
        {
            return EvaluatePlacementIgnoring(item, origin, ignoredItemId == null ? null : new[] { ignoredItemId });
        }

        public PlacementPreview EvaluatePlacementIgnoring(ItemDisplaySnapshot item, Vector2Int origin, params string[] ignoredIds)
        {
            if (!IsReady) return new PlacementPreview(false, "容器尚未就绪。", origin, item?.Size ?? Vector2Int.one);
            return Evaluate(item, origin, ignoredIds, validCells, occupancy, snapshot?.RejectContainers ?? true);
        }

        private static PlacementPreview Evaluate(ItemDisplaySnapshot item, Vector2Int origin, IReadOnlyCollection<string> ignoredIds,
            HashSet<Vector2Int> shape, Dictionary<Vector2Int, string> occupied, bool rejectContainers)
        {
            Vector2Int size = item?.Size ?? Vector2Int.one;
            string reason = null;
            if (item == null) reason = "未提供物品。";
            else if (string.IsNullOrWhiteSpace(item.Id) || item.Count < 1) reason = "物品编号或显示数量无效。";
            else if (rejectContainers && item.IsContainer) reason = "容器内部禁止存放任何容器。";
            else if (size.x < 1 || size.y < 1 || origin.x < 0 || origin.y < 0
                || (long)origin.x + size.x > BackpackContainerData.MaxColumns || (long)origin.y + size.y > int.MaxValue
                || (long)size.x * size.y > shape.Count) reason = "物品超出有效格范围。";
            if (reason != null) return new PlacementPreview(false, reason, origin, size);
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                Vector2Int coordinate = origin + new Vector2Int(x, y);
                if (!shape.Contains(coordinate))
                    return new PlacementPreview(false, "物品覆盖了无效格或容器边界。", origin, size);
                if (occupied.TryGetValue(coordinate, out string owner) && !ContainsId(ignoredIds, owner))
                    return new PlacementPreview(false, "目标格已有其他物品。", origin, size);
            }
            return new PlacementPreview(true, string.Empty, origin, size);
        }

        public bool CanAcceptContainer(ContainerKind kind) => IsReady && kind != ContainerKind.None && kind == containerType;

        public void ShowPlacement(PlacementPreview preview)
        {
            ClearGridFeedback();
            HasPlacementPreview = true;
            // 只遍历已存在的有效格，空洞既不生成格子，也不成为射线目标。
            foreach (KeyValuePair<Vector2Int, CellUI> entry in cellViews)
            {
                long x = (long)entry.Key.x - preview.Origin.x;
                long y = (long)entry.Key.y - preview.Origin.y;
                if (x >= 0 && y >= 0 && x < preview.Size.x && y < preview.Size.y)
                    entry.Value.SetFeedback(preview.Allowed ? CellFeedback.Allowed : CellFeedback.Blocked);
            }
        }

        public void ClearFeedback()
        {
            ClearGridFeedback();
            SetSlotFeedback(CellFeedback.Normal);
        }

        private void ClearGridFeedback()
        {
            HasPlacementPreview = false;
            foreach (CellUI cell in cellViews.Values) if (cell != null) cell.SetFeedback(CellFeedback.Normal);
        }

        public void SetItemDimmed(string id, bool dimmed)
        {
            if (id != null && itemViews.TryGetValue(id, out ItemViewUI view) && view != null) view.SetDimmed(dimmed);
        }

        public Vector2 GetItemPixelSize(Vector2Int size) => new Vector2(
            size.x < 1 ? 0f : cellSize.x * size.x + spacing.x * (size.x - 1),
            size.y < 1 ? 0f : cellSize.y * size.y + spacing.y * (size.y - 1));

        public void SetSlotFeedback(CellFeedback feedback)
        {
            if (containerSlot != null) containerSlot.SetFeedback(feedback);
        }

        private Vector2 ToPixel(Vector2Int coordinate) => new Vector2(
            coordinate.x * (cellSize.x + spacing.x), -coordinate.y * (cellSize.y + spacing.y));

        private static void SetRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
        }

        private static bool ValidCoordinate(Vector2Int value) => BackpackContainerData.IsSupportedCoordinate(value);
        private static bool SupportedKind(ContainerKind value) => value == ContainerKind.ChestRig
            || value == ContainerKind.Pocket || value == ContainerKind.Backpack || value == ContainerKind.SafeBox;
        private static bool FiniteNonNegative(float value) => value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool FinitePositive(float value) => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool ValidMetrics(Vector2 size, Vector2 gap) => FinitePositive(size.x) && FinitePositive(size.y)
            && FinitePositive(size.x + gap.x) && FinitePositive(size.y + gap.y);
        private static bool ContainsId(IReadOnlyCollection<string> values, string id)
        {
            if (values == null) return false;
            foreach (string value in values) if (string.Equals(value, id, StringComparison.Ordinal)) return true;
            return false;
        }
        private static bool Fail(string message, out string error) { error = message; return false; }
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
