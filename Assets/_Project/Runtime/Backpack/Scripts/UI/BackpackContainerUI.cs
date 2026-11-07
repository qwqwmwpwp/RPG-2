using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.UI
{
    /// <summary>单个背包容器的可复用显示组件：显式绑定来源，生成格子并显示物品。只读取库存；窗口、玩家输入与物品操作由宿主负责。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(190)]
    [RequireComponent(typeof(RectTransform))]
    public class BackpackContainerUI : MonoBehaviour
    {
        #region 序列化字段

        [SerializeField] private GameObject slotPrefab;
        [SerializeField] private GameObject itemPrefab;

        [Header("网格")]
        [SerializeField, Min(1)] private int initialColumns = 4;
        [SerializeField, Min(1)] private int initialRows = 5;
        [SerializeField, Min(1f)] private float cellSize = 100f;
        [SerializeField] private bool fitColumnsToViewport;
        [SerializeField, Min(1f)] private float minimumCellSize = 32f;
        [SerializeField] private Vector2 slotSpacing;
        [SerializeField] private RectTransform slotLayer;
        [SerializeField] private RectTransform itemLayer;
        [SerializeField] private RectTransform viewport;

        [Header("显示")]
        [SerializeField] private TMP_Text overflowText;
        [SerializeField] private TMP_FontAsset uiFont;
        [SerializeField] private Color slotEmptyColor = BackpackFeedbackPalette.SlotEmpty;
        [SerializeField] private Color slotOccupiedColor = BackpackFeedbackPalette.SlotFilled;
        [SerializeField] private Color slotAvailableColor = BackpackFeedbackPalette.SlotAvailable;
        [SerializeField] private Color slotBlockedColor = BackpackFeedbackPalette.SlotBlocked;
        [SerializeField] private Color slotSwapColor = BackpackFeedbackPalette.SlotSwap;
        [SerializeField] private Color warningColor = BackpackFeedbackPalette.Warning;

        [Header("编辑器展示")]
        [Tooltip("预制体中保存的格子和示例物品，仅供调整界面；进入游戏后自动隐藏。")]
        [SerializeField] private GameObject editorPreview;

        #endregion

        #region 内部类型

        /// <summary>单个格子的视图引用与悬停反馈态（原 slots / slotImages / slotHoverStates 三个平行列表合一）。</summary>
        private sealed class SlotVisual
        {
            public GameObject root; // 格子实例。
            public Image image; // 格子底图（悬停换色用）。
            public BackpackHoverState hover; // 当前悬停反馈态。
        }

        /// <summary>网格度量：使用当前显示格尺寸与间距，绘制、命中和拖放共用同一像素换算。</summary>
        private readonly struct GridMetrics
        {
            private readonly float cellSize; // 单格边长（像素）。
            private readonly Vector2 spacing; // 格子间距。

            public GridMetrics(float cellSize, Vector2 spacing)
            {
                this.cellSize = cellSize;
                this.spacing = spacing;
            }

            /// <summary>面板尺寸：格数 × 边长 + 间距 ×（格数 − 1）。</summary>
            public Vector2 PaneSize(int columns, int rows) => new Vector2(
                cellSize * columns + spacing.x * (columns - 1),
                cellSize * rows + spacing.y * (rows - 1));

            /// <summary>图层尺寸：与面板同径，但格数为 1 时不留间距。</summary>
            public Vector2 LayerSize(int columns, int rows) => new Vector2(
                cellSize * columns + spacing.x * Mathf.Max(0, columns - 1),
                cellSize * rows + spacing.y * Mathf.Max(0, rows - 1));

            /// <summary>物品像素尺寸：格数 × 边长 + 间隔 ×（格数 − 1）；调用方保证格数 ≥ 1。</summary>
            public Vector2 ItemPixelSize(int width, int height) => new Vector2(
                width * cellSize + spacing.x * (width - 1),
                height * cellSize + spacing.y * (height - 1));

            /// <summary>格坐标 → 图层局部像素坐标（左上角为原点，y 向下为负）。</summary>
            public Vector2 CellToPixel(Vector2 cellPosition) => new Vector2(
                cellPosition.x * (cellSize + spacing.x),
                -cellPosition.y * (cellSize + spacing.y));

            /// <summary>抓取格中心在图层内的偏移：偏移 × 步距 + 半格（间距算在格子尾部，不进半格）。</summary>
            public Vector2 GrabCenterOffset(Vector2Int grabOffset) => new Vector2(
                grabOffset.x * (cellSize + spacing.x) + cellSize * 0.5f,
                grabOffset.y * (cellSize + spacing.y) + cellSize * 0.5f);

            /// <summary>无 GridLayoutGroup 时手工摆放的格子锚点位置（像素口径与物品框一致）。</summary>
            public Vector2 ManualSlotPosition(int index, int columns) => new Vector2(
                index % columns * (cellSize + spacing.x),
                -(index / columns) * (cellSize + spacing.y));

            /// <summary>图层局部点（左上角为原点，x 向右、y 向下为正）→ 格参数；未命中任何格时输出 <see cref="TileParams.Empty"/>。</summary>
            public bool TryResolveLocal(Vector2 local, int columns, int rows, out TileParams tile)
            {
                tile = TileParams.Empty;
                float x = local.x;
                float y = local.y;
                if (x < 0f || y < 0f) return false;

                float pitchX = Mathf.Max(1f, cellSize + spacing.x);
                float pitchY = Mathf.Max(1f, cellSize + spacing.y);
                int column = Mathf.FloorToInt(x / pitchX);
                int row = Mathf.FloorToInt(y / pitchY);
                if (column < 0 || column >= columns || row < 0 || row >= rows) return false;

                // 象限按格内相对位置判定；间隔算在格子尾部，不参与象限分界。
                float fractionX = Mathf.Clamp01((x - column * pitchX) / cellSize);
                float fractionY = Mathf.Clamp01((y - row * pitchY) / cellSize);
                TileQuadrant quadrant = fractionY < 0.5f
                    ? (fractionX < 0.5f ? TileQuadrant.TopLeft : TileQuadrant.TopRight)
                    : (fractionX < 0.5f ? TileQuadrant.BottomLeft : TileQuadrant.BottomRight);

                tile = new TileParams(new GridCoord(column, row), row * columns + column, quadrant);
                return true;
            }
        }

        /// <summary>本机预览状态（不上网）：显示数量覆盖、携带隐藏物与悬停物，含跨帧复用的清理缓冲。</summary>
        private sealed class LocalDisplayState
        {
            private readonly Dictionary<ItemId, int> stackDisplayOverrides = new Dictionary<ItemId, int>(); // 显示数量覆盖：物品 → 期望显示数量（虚拟部分携带时源物品显示「总数 − 已取」），不改变库存数据。
            private readonly List<ItemId> pruneBuffer = new List<ItemId>(); // 清理位置表与显示覆盖的复用缓冲。

            public ItemId HiddenItemId { get; private set; } = ItemId.None; // 携带期间从网格隐藏的物品（仍占格，避免其它物品回流造成抖动）。
            public ItemId HoveredItemId { get; private set; } = ItemId.None; // 当前悬停高亮的物品（重绘后需要补一次着色）。

            /// <summary>写入某物品的显示数量覆盖。</summary>
            public void SetStackDisplay(ItemId id, int displayStack)
            {
                if (!id.IsValid) return;
                stackDisplayOverrides[id] = Mathf.Max(0, displayStack);
            }

            /// <summary>清除某物品的显示数量覆盖；返回是否确有覆盖。</summary>
            public bool ClearStackDisplay(ItemId id) => stackDisplayOverrides.Remove(id);

            /// <summary>清除全部显示数量覆盖。</summary>
            public void CollectStackDisplayIds(ISet<ItemId> destination)
            {
                foreach (ItemId id in stackDisplayOverrides.Keys) destination.Add(id);
            }

            public void ClearAllStackDisplays() => stackDisplayOverrides.Clear();

            /// <summary>设置携带期间隐藏的物品。</summary>
            public void SetHiddenItem(ItemId id) => HiddenItemId = id.IsValid ? id : ItemId.None;

            /// <summary>取消隐藏。</summary>
            public void ClearHiddenItem() => HiddenItemId = ItemId.None;

            /// <summary>切换悬停目标（调用方只传有效标识）。</summary>
            public void SetHoveredItem(ItemId id, bool hovered)
            {
                if (hovered) HoveredItemId = id;
                else if (HoveredItemId == id) HoveredItemId = ItemId.None;
            }

            /// <summary>丢弃失效或已自愈的显示覆盖：物品已消失，或真实堆叠数已降到覆盖值（说明主端已生效），都改用真值显示。</summary>
            public void PruneStackDisplayOverrides(IReadOnlyDictionary<ItemId, RuntimeItem> itemsById)
            {
                if (stackDisplayOverrides.Count == 0) return;
                pruneBuffer.Clear();
                foreach (KeyValuePair<ItemId, int> pair in stackDisplayOverrides)
                {
                    if (!itemsById.TryGetValue(pair.Key, out RuntimeItem item) || item == null || item.StackCount <= pair.Value)
                    {
                        pruneBuffer.Add(pair.Key);
                    }
                }

                foreach (ItemId id in pruneBuffer) stackDisplayOverrides.Remove(id);
                pruneBuffer.Clear();
            }

            /// <summary>取物品的显示数量：有覆盖用覆盖值，否则用真实堆叠数。</summary>
            public int ResolveDisplayStack(RuntimeItem item)
            {
                if (item == null) return 0;
                return stackDisplayOverrides.TryGetValue(item.ItemId, out int display) ? display : item.StackCount;
            }

            /// <summary>清空全部本机显示状态（解绑时调用）。</summary>
            public void Clear()
            {
                stackDisplayOverrides.Clear();
                HiddenItemId = ItemId.None;
                HoveredItemId = ItemId.None;
            }
        }

        /// <summary>一次重绘的缓存与视图索引：库存快照、展示项、待回收标识、标识 → 数据 / 视图、溢出文本缓冲；缓冲实例跨帧复用。</summary>
        private sealed class RenderCache
        {
            public readonly List<RuntimeItem> snapshot = new List<RuntimeItem>(); // 库存快照（复用，不逐帧读取）。
            public readonly List<GridDisplayItem> displayItems = new List<GridDisplayItem>(); // 本帧参与排布的展示项。
            public readonly List<ItemId> obsolete = new List<ItemId>(); // 本帧须回收的物品框标识。
            public readonly Dictionary<ItemId, RuntimeItem> itemsById = new Dictionary<ItemId, RuntimeItem>(); // 标识 → 运行时物品。
            public readonly Dictionary<ItemId, int> quantities = new Dictionary<ItemId, int>();
            public readonly Dictionary<ItemId, ItemWidget> widgets = new Dictionary<ItemId, ItemWidget>(); // 标识 → 物品框视图。
            public readonly StringBuilder overflowMessage = new StringBuilder(); // 溢出文本拼装缓冲。

            /// <summary>清空本帧的展示项与标识索引（物品列表在每次重绘时重取）。</summary>
            public void ClearIndex()
            {
                displayItems.Clear();
                itemsById.Clear();
                quantities.Clear();
            }

            /// <summary>取物品框视图；未创建或已被回收时返回 false。</summary>
            public bool TryGetWidget(ItemId id, out ItemWidget widget) => widgets.TryGetValue(id, out widget);
        }

        /// <summary>单个物品的视图引用集合：根节点与预制体上的物品框视图。</summary>
        private sealed class ItemWidget
        {
            public ItemId id; // 当前物品标识，避免每次刷新重写对象名称。
            public GameObject root; // 物品框根节点（物品预制体实例）。
            public BackpackItemUI view; // 根节点上的物品框视图。
        }

        #endregion

        #region 运行时状态

        private readonly List<BackpackItemUI> itemUIList = new List<BackpackItemUI>();
        private readonly List<SlotVisual> slotViews = new List<SlotVisual>();
        private readonly LocalDisplayState local = new LocalDisplayState();
        private readonly RenderCache render = new RenderCache();
        private RectTransform rectTransform;
        private InventoryGridLayout layout;
        private EventSubscription dataSubscription;
        private EventSubscription itemSubscription;
        private readonly HashSet<ItemId> dirtyItems = new HashSet<ItemId>();
        private readonly HashSet<ItemId> localDirtyItems = new HashSet<ItemId>();
        private readonly HashSet<ItemId> refreshItems = new HashSet<ItemId>();
        private readonly HashSet<ItemId> dataItems = new HashSet<ItemId>();
        private readonly HashSet<ItemId> layoutChanges = new HashSet<ItemId>();
        private readonly Dictionary<ItemId, InventoryPlacement> changedPlacements = new Dictionary<ItemId, InventoryPlacement>();
        private int pendingDetailedSignals;
        private bool fullRefreshPending = true;
        public long RefreshCount { get; private set; }
        public long FullRefreshCount { get; private set; }
        public int LastRefreshedItemCount { get; private set; }
        private bool CanPresent => presentationVisible && isActiveAndEnabled;
        private object subscribedEventSource;
        private bool dataDirty;
        private bool presentationVisible = true;
        private bool itemPrefabWarned;
        private bool configurationWarned;
        private ItemId selectedItemId;
        private float displayedCellSize;

        public GameObject ItemPrefab => itemPrefab;
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public IBackpackView Inventory { get; private set; }
        public bool IsReady => CanPresent && Inventory != null && Inventory.IsReady && Inventory.EventSource != null && itemLayer != null && Columns > 0 && Rows > 0;
        public int ItemStackCount => render.itemsById.Count;
        public long TotalItemCount { get; private set; }
        public int OccupiedCellCount { get; private set; }
        public int OverflowItemCount => layout != null ? layout.Overflow.Count : 0;
        private GridMetrics Metrics => new GridMetrics(displayedCellSize > 0f ? displayedCellSize : cellSize, slotSpacing);

        #endregion

        #region 生命周期与数据绑定

        private void Awake()
        {
            if (editorPreview != null) editorPreview.SetActive(false);
            rectTransform = GetComponent<RectTransform>();
            ConfigureOverflowText();
        }

        private void Start()
        {
            if (CanPresent && EnsureInitialized()) RequestFullRefresh();
        }

        private void OnEnable()
        {
            // 网格尚未初始化时，等待宿主绑定或 Start 创建。
            if (!CanPresent || Columns < 1 || Rows < 1) return;
            UpdateSubscription();
            RequestFullRefresh();
        }

        private void Update()
        {
            if (!CanPresent) return;
            if (Inventory is Component component && component == null)
            {
                UnbindInventory();
                return;
            }

            if (Inventory == null) return;
            if (UpdateSubscription()) RequestFullRefresh();
            SyncGridSize(Inventory);
        }

        private void LateUpdate()
        {
            if (CanPresent && dataDirty) FlushRefresh();
        }

        /// <summary>禁用只暂停订阅；来源保持，重新启用时重新读取并订阅。</summary>
        private void OnDisable()
        {
            ReleaseSubscription();
            fullRefreshPending = true;
            dataDirty = false;
        }

        private void OnDestroy() => ReleaseSubscription();

        /// <summary>确保合法占位网格存在；重复调用不会重复生成格子。</summary>
        public bool EnsureInitialized()
        {
            if (rectTransform == null) rectTransform = GetComponent<RectTransform>();
            if (slotLayer == null || itemLayer == null || slotPrefab == null
                || !slotPrefab.TryGetComponent(out BackpackSlotUI slot) || slot.background == null)
            {
                if (!configurationWarned)
                {
                    configurationWarned = true;
                    BackpackLog.Warn(LogModule.UI, "请在预制体中接线格子层、物品层，以及带 BackpackSlotUI 和背景引用的格子预制体。");
                }
                return false;
            }

            configurationWarned = false;
            ConfigureOverflowText();
            if (Columns < 1 || Rows < 1) BuildGrid(Mathf.Max(1, initialColumns), Mathf.Max(1, initialRows));
            return true;
        }

        private void ConfigureOverflowText()
        {
            if (overflowText == null) return;
            if (overflowText.color != warningColor) overflowText.color = warningColor;
            if (uiFont != null && overflowText.font != uiFont) overflowText.font = uiFont;
        }

        /// <summary>显式绑定展示来源；同源重复调用会补接晚初始化的事件键，并保留本地显示状态。</summary>
        public void BindInventory(IBackpackView source)
        {
            bool sameSource = ReferenceEquals(Inventory, source);
            if (!sameSource) { UnbindInventory(); Inventory = source; }
            if (!CanPresent) { RequestFullRefresh(); return; }
            if (!EnsureInitialized()) return;
            bool subscriptionChanged = UpdateSubscription();
            bool gridChanged = SyncGridSize(source);
            if (!sameSource || subscriptionChanged || gridChanged)
            {
                RequestFullRefresh();
                FlushRefresh(); // 新来源立即就绪，交互会话不读取上一个来源的视图缓存。
            }
        }

        /// <summary>解除来源并使读取失效；保留物品框供下次打开复用，隐藏时不重画。</summary>
        public void UnbindInventory()
        {
            ReleaseSubscription();
            Inventory = null;
            render.snapshot.Clear();
            render.ClearIndex();
            changedPlacements.Clear();
            layout?.Clear();
            TotalItemCount = 0;
            OccupiedCellCount = 0;
            local.Clear();
            foreach (SlotVisual slot in slotViews) slot.hover = BackpackHoverState.None;
            dirtyItems.Clear();
            localDirtyItems.Clear();
            RequestFullRefresh();
        }

        /// <summary>隐藏时停止显示订阅与刷新；数据源的网络镜像生命周期保持独立。</summary>
        public void SetPresentationVisible(bool visible)
        {
            if (presentationVisible == visible) return;
            presentationVisible = visible;
            if (!visible)
            {
                ReleaseSubscription();
                fullRefreshPending = true;
                dataDirty = false;
                dirtyItems.Clear();
                localDirtyItems.Clear();
                if (overflowText != null && overflowText.gameObject.activeSelf) overflowText.gameObject.SetActive(false);
                return;
            }
            RequestFullRefresh();
            if (!CanPresent) return;
            UpdateSubscription();
            FlushRefresh();
        }

        private bool UpdateSubscription()
        {
            if (!CanPresent) return false;
            object eventSource = Inventory?.EventSource;
            if (ReferenceEquals(subscribedEventSource, eventSource)
                && (eventSource == null || dataSubscription.IsAlive && itemSubscription.IsAlive)) return false;
            ReleaseSubscription();
            subscribedEventSource = eventSource;
            if (eventSource != null)
            {
                itemSubscription = EventBus.Global.Subscribe<ItemId[]>(InvChannels.ItemsChanged, eventSource, this, OnItemsChanged);
                dataSubscription = EventBus.Global.Subscribe(InvChannels.Changed, eventSource, this, OnInventoryChanged);
            }
            return true;
        }

        private void ReleaseSubscription()
        {
            dataSubscription.Dispose();
            itemSubscription.Dispose();
            dataSubscription = itemSubscription = default;
            subscribedEventSource = null;
            pendingDetailedSignals = 0;
        }

        private void OnItemsChanged(ItemId[] ids)
        {
            pendingDetailedSignals++;
            if (ids == null) fullRefreshPending = true;
            else foreach (ItemId id in ids) dirtyItems.Add(id);
            dataDirty = true;
        }

        private void OnInventoryChanged()
        {
            if (pendingDetailedSignals > 0) pendingDetailedSignals--;
            else fullRefreshPending = true; // 兼容只有粗粒度通知的来源。
            dataDirty = true;
        }

        private void RequestFullRefresh()
        {
            fullRefreshPending = true;
            dataDirty = true;
        }

        private bool SyncGridSize(IBackpackView source)
        {
            if (source == null) return false;
            int columns = source.Columns;
            int rows = source.Rows;
            if (columns < 1 || rows < 1 || (long)columns * rows > 65536) return false;
            if (columns == Columns && rows == Rows && Mathf.Approximately(displayedCellSize, ResolveCellSize(columns))) return false;
            BuildGrid(columns, rows);
            return true;
        }

        #endregion

        #region 网格构建与图层

        private float ResolveCellSize(int columns)
        {
            float maximum = Mathf.Max(1f, cellSize);
            if (!fitColumnsToViewport || viewport == null || viewport.rect.width <= 0f || columns < 1) return maximum;
            float available = (viewport.rect.width - slotSpacing.x * Mathf.Max(0, columns - 1)) / columns;
            return Mathf.Clamp(available, Mathf.Min(maximum, Mathf.Max(1f, minimumCellSize)), maximum);
        }

        /// <summary>按列数、行数重建格子：先调整面板尺寸与布局参数，再销毁旧格子并实例化新格子。</summary>
        private void BuildGrid(int columns, int rows)
        {
            if (columns < 1 || rows < 1 || (long)columns * rows > 65536)
            {
                BackpackLog.Error(LogModule.UI, $"网格行列非法（{columns} × {rows}），本次重建已忽略。");
                return;
            }

            Columns = columns;
            Rows = rows;
            cellSize = Mathf.Max(1f, cellSize);
            displayedCellSize = ResolveCellSize(columns);

            GridMetrics metrics = Metrics;
            rectTransform.sizeDelta = metrics.PaneSize(columns, rows);

            RectTransform parent = slotLayer;
            GridLayoutGroup gridLayout = parent.GetComponent<GridLayoutGroup>();

            // 布局组件存在时由它统一格子尺寸与间距，避免预制体参数与脚本字段漂移。
            if (gridLayout != null)
            {
                gridLayout.padding = new RectOffset();
                gridLayout.childAlignment = TextAnchor.UpperLeft;
                gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                gridLayout.constraintCount = columns;
                gridLayout.cellSize = new Vector2(displayedCellSize, displayedCellSize);
                gridLayout.spacing = slotSpacing;
            }

            foreach (SlotVisual oldSlot in slotViews)
            {
                if (oldSlot.root != null)
                {
                    oldSlot.root.SetActive(false);
                    Destroy(oldSlot.root);
                }
            }

            slotViews.Clear();

            for (int i = 0; i < columns * rows; i++)
            {
                GameObject slot = Instantiate(slotPrefab, parent);
                slot.name = "Slot_" + i;
                RectTransform rect = (RectTransform)slot.transform;

                // 没有布局组件时手工摆放，像素口径与物品框一致。
                if (gridLayout == null)
                {
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                    rect.sizeDelta = new Vector2(displayedCellSize, displayedCellSize);
                    rect.anchoredPosition = metrics.ManualSlotPosition(i, columns);
                }

                Image image = slot.GetComponent<BackpackSlotUI>().background;
                if (image != null)
                {
                    image.raycastTarget = false;
                    image.color = slotEmptyColor;
                }

                slotViews.Add(new SlotVisual { root = slot, image = image, hover = BackpackHoverState.None });
            }

            EnsureItemLayer();
            RequestFullRefresh();
        }

        /// <summary>更新预制体中已接线的物品层尺寸。</summary>
        private void EnsureItemLayer()
        {
            itemLayer.anchorMin = itemLayer.anchorMax = itemLayer.pivot = new Vector2(0, 1);
            itemLayer.anchoredPosition = Vector2.zero;
            itemLayer.sizeDelta = Metrics.LayerSize(Columns, Rows);
            itemLayer.SetAsLastSibling();
        }

        #endregion

        #region 重绘管线

        /// <summary>本机预览变化仅请求刷新；与数据通知合并到帧末，悬停高亮仍直接更新单项。</summary>
        public void Redraw()
        {
            if (localDirtyItems.Count > 0 || dirtyItems.Count > 0 || fullRefreshPending) dataDirty = true;
        }

        private void FlushRefresh()
        {
            if (!CanPresent || !dataDirty) return;
            dataDirty = false;
            if (!EnsureInitialized()) return;
            SyncGridSize(Inventory);
            bool full = fullRefreshPending || !IsReady || layout == null || layout.Overflow.Count > 0;
            dataItems.Clear();
            dataItems.UnionWith(dirtyItems);
            refreshItems.Clear();
            refreshItems.UnionWith(dirtyItems);
            refreshItems.UnionWith(localDirtyItems);
            dirtyItems.Clear();
            localDirtyItems.Clear();
            fullRefreshPending = false;
            dataDirty = false;
            if (full || !RefreshChangedItems()) Refresh();
            RefreshCount++;
        }

        private bool RefreshChangedItems()
        {
            layoutChanges.Clear();
            changedPlacements.Clear();
            foreach (ItemId id in dataItems)
            {
                if (render.quantities.TryGetValue(id, out int previousCount)) TotalItemCount -= previousCount;
                bool hadPlacement = layout.TryGetPlacement(id, out GridDisplayPlacement previous);
                if (Inventory.TryGetPlacement(id, out InventoryPlacement next) && next.IsValid)
                {
                    RuntimeItem item = next.Item;
                    render.itemsById[id] = item;
                    render.quantities[id] = item.StackCount;
                    TotalItemCount += item.StackCount;
                    if (!hadPlacement || previous.Position.x != next.UpperLeftIndex % Columns
                        || previous.Position.y != next.UpperLeftIndex / Columns
                        || previous.Item.Width != next.Dimensions.X || previous.Item.Height != next.Dimensions.Y)
                    {
                        layoutChanges.Add(id);
                        changedPlacements[id] = next;
                    }
                }
                else
                {
                    render.itemsById.Remove(id);
                    render.quantities.Remove(id);
                    local.ClearStackDisplay(id);
                    if (hadPlacement) layoutChanges.Add(id);
                }
            }
            local.PruneStackDisplayOverrides(render.itemsById);
            if (layoutChanges.Count > 0)
            {
                if (!layout.ProjectChanges(layoutChanges, changedPlacements)) return false;
                OccupiedCellCount = layout.OccupiedCellCount;
                foreach (int cell in layout.ChangedCells) ApplySlotColor(cell);
            }
            foreach (ItemId id in refreshItems) RefreshItemWidget(id);
            LastRefreshedItemCount = refreshItems.Count;
            UpdateOverflow();
            return true;
        }

        private void RemoveWidget(ItemId id)
        {
            if (!render.widgets.TryGetValue(id, out ItemWidget widget)) return;
            if (widget.root != null) { widget.root.SetActive(false); Destroy(widget.root); }
            if (widget.view != null) itemUIList.Remove(widget.view);
            render.widgets.Remove(id);
        }

        private void RefreshItemWidget(ItemId id)
        {
            if (!layout.TryGetPlacement(id, out GridDisplayPlacement placement)
                || !render.itemsById.TryGetValue(id, out RuntimeItem item)) { RemoveWidget(id); return; }
            if (!render.widgets.TryGetValue(id, out ItemWidget widget) || widget.root == null)
            {
                widget = CreateWidget(itemLayer);
                if (widget == null) return;
                render.widgets[id] = widget;
                itemUIList.Add(widget.view);
            }
            if (widget.id != id) { widget.id = id; widget.root.name = "Item_" + id.Value; }
            RectTransform rect = (RectTransform)widget.root.transform;
            Vector2 position = Metrics.CellToPixel(placement.Position);
            if (rect.anchoredPosition != position) rect.anchoredPosition = position;
            widget.view.SetSize(Metrics.ItemPixelSize(placement.Item.Width, placement.Item.Height));
            bool hidden = local.HiddenItemId.IsValid && id == local.HiddenItemId;
            if (widget.root.activeSelf == hidden) widget.root.SetActive(!hidden);
            if (hidden) return;
            ApplyWidget(widget, item, local.ResolveDisplayStack(item));
            ApplyWidgetHover(widget, id == local.HoveredItemId);
        }

        /// <summary>完整重读与投影：用于首次显示、改绑、尺寸变化和粗粒度失效；正常变更按物品更新。</summary>
        private void Refresh()
        {
            if (itemLayer == null || Columns < 1 || Rows < 1) return;
            if (layout == null || layout.Columns != Columns || layout.Rows != Rows) layout = new InventoryGridLayout(Columns, Rows);

            // 绑定、尺寸变化及粗粒度来源走完整读取；正常物品通知走增量路径。
            if (Inventory != null && Inventory.IsReady) Inventory.CopyItems(render.snapshot);
            else render.snapshot.Clear();

            render.ClearIndex();
            TotalItemCount = 0;
            foreach (RuntimeItem item in render.snapshot)
            {
                if (item == null) continue;
                TotalItemCount += item.StackCount;
                GridFragment grid = item.GetFragment<GridFragment>();
                render.displayItems.Add(new GridDisplayItem(item.ItemId, Mathf.Max(1, grid?.Width ?? 1), Mathf.Max(1, grid?.Height ?? 1)));
                render.itemsById[item.ItemId] = item;
                render.quantities[item.ItemId] = item.StackCount;
            }

            // 数量覆盖只服务携带预览；所有落点直接投影权威库存。
            local.PruneStackDisplayOverrides(render.itemsById);
            layout.Project(render.displayItems, Inventory);
            OccupiedCellCount = layout.OccupiedCellCount;

            // 格子底色：悬停反馈态优先，其次空闲 / 占用两态。
            ApplySlotColors();

            // 回收已消失或本帧放不下的物品框。
            render.obsolete.Clear();
            foreach (KeyValuePair<ItemId, ItemWidget> pair in render.widgets)
            {
                if (!render.itemsById.ContainsKey(pair.Key) || !IsPlaced(pair.Key)) render.obsolete.Add(pair.Key);
            }

            foreach (ItemId id in render.obsolete) RemoveWidget(id);
            foreach (GridDisplayPlacement placement in layout.Placed) RefreshItemWidget(placement.Item.Id);
            FullRefreshCount++;
            LastRefreshedItemCount = render.itemsById.Count;
            render.snapshot.Clear();

            UpdateOverflow();
        }

        /// <summary>物品是否在本帧排布结果中。</summary>
        private bool IsPlaced(ItemId id) => layout.TryGetPlacement(id, out _);

        /// <summary>把放不下的物品汇总进溢出文本（仅在窗口打开时显示）。</summary>
        private void UpdateOverflow()
        {
            if (overflowText == null || layout == null) return;
            render.overflowMessage.Clear();
            if (layout.Overflow.Count > 0)
            {
                render.overflowMessage.Append("无法显示 ").Append(layout.Overflow.Count).Append(" 件：");
            }

            foreach (GridDisplayItem entry in layout.Overflow)
            {
                if (!render.itemsById.TryGetValue(entry.Id, out RuntimeItem item)) continue;
                render.overflowMessage.Append('\n').Append(item.ItemType).Append(" ×").Append(local.ResolveDisplayStack(item));
            }

            string message = render.overflowMessage.ToString();
            if (overflowText.text != message) overflowText.text = message;
            bool visible = CanPresent && message.Length > 0;
            if (overflowText.gameObject.activeSelf != visible) overflowText.gameObject.SetActive(visible);
        }

        /// <summary>按当前悬停态与占用态刷新全部格子底色（重绘、解绑时调用）。</summary>
        private void ApplySlotColors()
        {
            if (layout == null) return;
            for (int i = 0; i < slotViews.Count; i++) ApplySlotColor(i);
        }

        private void ApplySlotColor(int index)
        {
            if (!CanPresent || layout == null || index < 0 || index >= slotViews.Count) return;
            SlotVisual slot = slotViews[index];
            if (slot.image == null) return;
            bool occupied = index < layout.Occupied.Count && layout.Occupied[index];
            Color color = SlotColor(slot.hover, occupied);
            if (slot.image.color != color) slot.image.color = color;
        }

        /// <summary>写入单个格子的悬停反馈态；状态未变化时直接返回，避免多余的 Canvas 重建。</summary>
        private void SetSlotHoverState(int index, BackpackHoverState state)
        {
            if (index < 0 || index >= slotViews.Count) return;
            SlotVisual slot = slotViews[index];
            if (slot.hover == state) return;
            slot.hover = state;
            ApplySlotColor(index);
        }

        /// <summary>悬停反馈色优先于占用 / 空闲底色。</summary>
        private Color SlotColor(BackpackHoverState state, bool occupied)
        {
            switch (state)
            {
                case BackpackHoverState.Available: return slotAvailableColor;
                case BackpackHoverState.Blocked: return slotBlockedColor;
                case BackpackHoverState.Swap: return slotSwapColor;
                default: return occupied ? slotOccupiedColor : slotEmptyColor;
            }
        }

        #endregion

        #region 本机显示状态

        /// <summary>写入某物品的显示数量覆盖（右键拆分时源物品显示「总数 − 已取数量」）；只影响本机显示，不改动库存数据。调用方随后自行 <see cref="Redraw"/>。</summary>
        public void SetStackDisplay(ItemId id, int displayStack)
        {
            local.SetStackDisplay(id, displayStack);
            MarkLocalItem(id);
        }

        /// <summary>清除某物品的显示数量覆盖；返回是否确有覆盖。</summary>
        public bool ClearStackDisplay(ItemId id)
        {
            if (!local.ClearStackDisplay(id)) return false;
            MarkLocalItem(id);
            return true;
        }

        /// <summary>清除全部显示数量覆盖。</summary>
        public void ClearAllStackDisplays()
        {
            if (CanPresent) local.CollectStackDisplayIds(localDirtyItems);
            local.ClearAllStackDisplays();
            if (localDirtyItems.Count > 0) dataDirty = true;
        }

        /// <summary>携带期间把某物品从网格隐藏（仍占用格子，避免其它物品回流导致抖动）。</summary>
        public void SetHiddenItem(ItemId id)
        {
            ItemId previous = local.HiddenItemId;
            local.SetHiddenItem(id);
            if (previous == local.HiddenItemId) return;
            MarkLocalItem(previous);
            MarkLocalItem(local.HiddenItemId);
        }

        /// <summary>取消隐藏。</summary>
        public void ClearHiddenItem() => SetHiddenItem(ItemId.None);

        private void MarkLocalItem(ItemId id)
        {
            if (!id.IsValid) return;
            if (!CanPresent) { fullRefreshPending = true; return; }
            localDirtyItems.Add(id);
            dataDirty = true;
        }

        /// <summary>切换某物品的悬停高亮（交互层只在悬停目标变化时调用）。</summary>
        public void SetItemHovered(ItemId id, bool hovered)
        {
            if (!id.IsValid) return;
            local.SetHoveredItem(id, hovered);
            if (CanPresent && render.TryGetWidget(id, out ItemWidget widget) && widget.root != null) ApplyWidgetHover(widget, hovered);
        }

        /// <summary>清除所有格子的悬停反馈。</summary>
        public void ClearHoverHighlight()
        {
            for (int i = 0; i < slotViews.Count; i++) SetSlotHoverState(i, BackpackHoverState.None);
        }

        /// <summary>把一块区域（上左索引 + 尺寸）标成同一悬停反馈态；区域越出网格时只标网格内的部分（越界格不存在），据此贴边外伸仍显示阻塞色。</summary>
        public void ApplyRegionHighlight(int startIndex, GridCoord dimensions, BackpackHoverState state)
        {
            if (state == BackpackHoverState.None)
            {
                ClearHoverHighlight();
                return;
            }

            if (startIndex < 0 || startIndex >= Columns * Rows || dimensions.X < 1 || dimensions.Y < 1) return;
            GridCoord origin = GridUtils.GetPositionFromIndex(startIndex, Columns);
            GridCoord visible = new GridCoord(
                Mathf.Min(dimensions.X, Columns - origin.X),
                Mathf.Min(dimensions.Y, Rows - origin.Y));
            if (visible.X < 1 || visible.Y < 1) return;
            GridUtils.ForEach2D(startIndex, visible, Columns, (index, coord) => SetSlotHoverState(index, state));
        }

        /// <summary>按标识取本帧快照中的物品数据。</summary>
        public bool TryGetItem(ItemId id, out RuntimeItem item)
        {
            item = null;
            return IsReady && render.itemsById.TryGetValue(id, out item);
        }

        /// <summary>取物品当前的权威落点投影（落点 + 占位尺寸）。</summary>
        public bool TryGetPlacement(ItemId id, out GridDisplayPlacement placement)
        {
            if (IsReady && layout != null) return layout.TryGetPlacement(id, out placement);
            placement = default;
            return false;
        }

        #endregion

        #region 查询与几何换算

        public Vector2 GetItemPixelSize(GridCoord dimensions) => Metrics.ItemPixelSize(dimensions.X, dimensions.Y);

        public Vector2 GetGrabCenterOffset(Vector2Int grabOffset) => Metrics.GrabCenterOffset(grabOffset);

        /// <summary>取格子左上角的世界坐标，供会话的统一携带预览定位。</summary>
        public Vector3 GetCellWorldPosition(int index)
        {
            GridCoord cell = GridUtils.GetPositionFromIndex(index, Columns);
            return itemLayer.TransformPoint(Metrics.CellToPixel(new Vector2(cell.X, cell.Y)));
        }

        /// <summary>取某格当前的占用者（物品标识 + 其上左线性索引）。</summary>
        public bool TryGetCellOwner(int index, out ItemId ownerId, out int upperLeftIndex)
        {
            if (!IsReady || layout == null)
            {
                ownerId = ItemId.None;
                upperLeftIndex = SpaceQueryResult<RuntimeItem>.NoIndex;
                return false;
            }

            return layout.TryGetCellOwner(index, out ownerId, out upperLeftIndex);
        }

        /// <summary>基于权威落点投影预览空间；实际可写性由库存事务最终校验。</summary>
        public SpaceQueryResult<RuntimeItem> QuerySpace(int startIndex, GridCoord dimensions)
        {
            if (!IsReady || layout == null || dimensions.X < 1 || dimensions.Y < 1) return SpaceQueryResult<RuntimeItem>.Empty;
            if (!GridUtils.IsInGridBounds(startIndex, dimensions, Columns, Rows)) return SpaceQueryResult<RuntimeItem>.Empty;

            bool hasSpace = true;
            ItemId occupantId = ItemId.None;
            int occupantUpperLeft = SpaceQueryResult<RuntimeItem>.NoIndex;
            GridCoord origin = GridUtils.GetPositionFromIndex(startIndex, Columns);
            for (int row = 0; row < dimensions.Y; row++)
            {
                for (int column = 0; column < dimensions.X; column++)
                {
                    int cell = GridUtils.GetIndexFromPosition(origin + new GridCoord(column, row), Columns);
                    if (!layout.TryGetCellOwner(cell, out ItemId owner, out int upperLeft)) continue;
                    hasSpace = false;
                    if (!occupantId.IsValid)
                    {
                        occupantId = owner;
                        occupantUpperLeft = upperLeft;
                    }
                    else if (occupantId != owner)
                    {
                        // 多个不同物品挡路：既不是空位，也没有唯一可交换对象。
                        return SpaceQueryResult<RuntimeItem>.Empty;
                    }
                }
            }

            if (hasSpace) return new SpaceQueryResult<RuntimeItem>(true, null, SpaceQueryResult<RuntimeItem>.NoIndex);
            if (!occupantId.IsValid
                || occupantUpperLeft < 0
                || !render.itemsById.TryGetValue(occupantId, out RuntimeItem occupant)
                || occupant == null)
            {
                return SpaceQueryResult<RuntimeItem>.Empty;
            }

            return new SpaceQueryResult<RuntimeItem>(false, occupant, occupantUpperLeft);
        }

        /// <summary>把屏幕坐标换算成本帧网格参数（格坐标 / 线性索引 / 格内象限）；未命中任何格时输出 <see cref="TileParams.Empty"/>。</summary>
        public bool TryResolveTile(Vector2 screenPoint, Camera uiCamera, out TileParams tile)
        {
            tile = TileParams.Empty;
            if (viewport != null && !RectTransformUtility.RectangleContainsScreenPoint(viewport, screenPoint, uiCamera)) return false;
            if (itemLayer == null || Columns < 1 || Rows < 1 || cellSize <= 0f) return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(itemLayer, screenPoint, uiCamera, out Vector2 local)) return false;

            // 物品层 pivot / anchor 为左上角：局部原点在左上角，x 向右为正、y 向下为负（翻正后交给度量类型）。
            return Metrics.TryResolveLocal(new Vector2(local.x, -local.y), Columns, Rows, out tile);
        }

        /// <summary>按「抓取格偏移」（被点击格相对物品上左格的列 / 行偏移）推导上左索引：被点击的那一格贴住悬停格；锚格越界返回 -1（据此判为不可放置），不夹取，区域溢出由 QuerySpace 判阻塞。</summary>
        public int GetUpperLeftIndex(int hoverIndex, Vector2Int grabOffset)
        {
            if (hoverIndex < 0 || hoverIndex >= Columns * Rows) return SpaceQueryResult<RuntimeItem>.NoIndex;

            GridCoord hover = GridUtils.GetPositionFromIndex(hoverIndex, Columns);
            int column = hover.X - grabOffset.x;
            int row = hover.Y - grabOffset.y;
            if (column < 0 || row < 0 || column >= Columns || row >= Rows) return SpaceQueryResult<RuntimeItem>.NoIndex;
            return GridUtils.GetIndexFromPosition(new GridCoord(column, row), Columns);
        }

        #endregion

        #region 物品框视图工具

        /// <summary>实例化物品预制体并取用根节点上的物品框视图；预制体缺失或不合规时告警一次并返回 null（调用方跳过该物品）。</summary>
        private ItemWidget CreateWidget(RectTransform parent)
        {
            if (itemPrefab == null)
            {
                WarnItemPrefabOnce("未设置物品预制体（itemPrefab），背包里的物品不会显示。");
                return null;
            }

            GameObject root = Instantiate(itemPrefab, parent);
            if (!root.TryGetComponent(out BackpackItemUI view))
            {
                WarnItemPrefabOnce("物品预制体根节点没有 BackpackItemUI 组件，背包里的物品不会显示。");
                Destroy(root);
                return null;
            }

            // 预制体默认居中锚点，放置口径要求左上角对齐，统一在此覆盖。
            RectTransform rect = (RectTransform)root.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = Vector2.zero;
            rect.localScale = Vector3.one;
            root.layer = 5;
            return new ItemWidget { root = root, view = view };
        }

        /// <summary>把物品数据写入物品框：有图标用图标（无图标留空），显示数量大于 1 才显示（虚拟部分携带时由调用方传入显示值）。</summary>
        private void ApplyWidget(ItemWidget widget, RuntimeItem item, int displayStack)
        {
            if (widget?.view == null || item == null) return;
            ImageFragment image = item.GetFragment<ImageFragment>();
            widget.view.SetItem(image != null ? image.Icon : null, displayStack);
            widget.view.SetSelected(item.ItemId == selectedItemId);
        }

        public void SetSelectedItem(ItemId id)
        {
            if (selectedItemId == id) return;
            if (!CanPresent) { selectedItemId = id; return; }
            if (render.TryGetWidget(selectedItemId, out ItemWidget previous) && previous?.view != null)
                previous.view.SetSelected(false);
            selectedItemId = id;
            if (render.TryGetWidget(id, out ItemWidget current) && current?.view != null)
                current.view.SetSelected(true);
        }

        /// <summary>切换物品框的悬停底色；取消悬停由视图还原为预制体原色。</summary>
        private static void ApplyWidgetHover(ItemWidget widget, bool hovered)
        {
            if (widget?.view == null) return;
            widget.view.SetHover(hovered);
        }

        /// <summary>物品预制体缺失或不合规的告警：每组件只输出一条，避免逐帧刷屏。</summary>
        private void WarnItemPrefabOnce(string message)
        {
            if (itemPrefabWarned) return;
            itemPrefabWarned = true;
            BackpackLog.Warn(LogModule.UI, message);
        }

        #endregion
    }
}
