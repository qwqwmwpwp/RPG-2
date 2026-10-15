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
    [RequireComponent(typeof(RectTransform))]
    public class BackpackContainerUI : MonoBehaviour
    {
        #region 序列化字段

        public GameObject slotPrefab;
        public GameObject itemPrefab;
        public List<BackpackItemUI> itemUIList = new List<BackpackItemUI>();

        [Header("网格")]
        [Min(1)] public int initialColumns = 4;
        [Min(1)] public int initialRows = 5;
        [Min(1f)] public float cellSize = 100f;
        public Vector2 slotSpacing;
        public RectTransform slotLayer;
        public RectTransform itemLayer;

        [Header("显示")]
        public TMP_Text overflowText;
        public TMP_FontAsset uiFont;
        public Color slotEmptyColor = BackpackFeedbackPalette.SlotEmpty;
        public Color slotOccupiedColor = BackpackFeedbackPalette.SlotFilled;
        public Color slotAvailableColor = BackpackFeedbackPalette.SlotAvailable;
        public Color slotBlockedColor = BackpackFeedbackPalette.SlotBlocked;
        public Color slotSwapColor = BackpackFeedbackPalette.SlotSwap;
        public Color warningColor = BackpackFeedbackPalette.Warning;

        [Header("可选携带预览")]
        public RectTransform cursorLayer;

        #endregion

        #region 内部类型

        /// <summary>单个格子的视图引用与悬停反馈态（原 slots / slotImages / slotHoverStates 三个平行列表合一）。</summary>
        private sealed class SlotVisual
        {
            public GameObject root; // 格子实例。
            public Image image; // 格子底图（悬停换色用）。
            public BackpackHoverState hover; // 当前悬停反馈态。
        }

        /// <summary>网格度量（只读值类型）：由序列化字段 cellSize / slotSpacing 现取现建，只承载纯像素换算，保证同一公式只有一处定义。</summary>
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

        /// <summary>本机显示状态（不上网）：本地位置表、显示数量覆盖、携带隐藏物与悬停物，含跨帧复用的清理缓冲。</summary>
        private sealed class LocalDisplayState
        {
            private readonly Dictionary<ItemId, Vector2Int> manualPositions = new Dictionary<ItemId, Vector2Int>(); // 本地位置表：物品 → 落点（列、行）。
            private readonly Dictionary<ItemId, int> stackDisplayOverrides = new Dictionary<ItemId, int>(); // 显示数量覆盖：物品 → 期望显示数量（虚拟部分携带时源物品显示「总数 − 已取」），不改变库存数据。
            private readonly List<ItemId> pruneBuffer = new List<ItemId>(); // 清理位置表与显示覆盖的复用缓冲。

            public ItemId HiddenItemId { get; private set; } = ItemId.None; // 携带期间从网格隐藏的物品（仍占格，避免其它物品回流造成抖动）。
            public ItemId HoveredItemId { get; private set; } = ItemId.None; // 当前悬停高亮的物品（重绘后需要补一次着色）。

            public IReadOnlyDictionary<ItemId, Vector2Int> ManualPositions => manualPositions; // 供排布读取的本地位置表。

            /// <summary>当前是否还有显示数量覆盖（超时兜底据此判断是否真的需要还原）。</summary>
            public bool HasStackDisplayOverride => stackDisplayOverrides.Count > 0;

            /// <summary>写入物品的本地位置（列、行）；越界位置在排布时会被忽略并自动寻位。</summary>
            public void SetManualPosition(ItemId id, Vector2Int position)
            {
                if (!id.IsValid) return;
                manualPositions[id] = position;
            }

            /// <summary>清除某物品的本地位置记录；返回是否确有记录。</summary>
            public bool ClearManualPosition(ItemId id) => manualPositions.Remove(id);

            /// <summary>写入某物品的显示数量覆盖。</summary>
            public void SetStackDisplay(ItemId id, int displayStack)
            {
                if (!id.IsValid) return;
                stackDisplayOverrides[id] = Mathf.Max(0, displayStack);
            }

            /// <summary>清除某物品的显示数量覆盖；返回是否确有覆盖。</summary>
            public bool ClearStackDisplay(ItemId id) => stackDisplayOverrides.Remove(id);

            /// <summary>清除全部显示数量覆盖（结束携带、会话复位与超时兜底用）。</summary>
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

            /// <summary>丢弃位置表中已不在库存里的物品，避免长期挂留失效标识。</summary>
            public void PruneManualPositions(IReadOnlyDictionary<ItemId, RuntimeItem> itemsById)
            {
                if (manualPositions.Count == 0) return;
                pruneBuffer.Clear();
                foreach (KeyValuePair<ItemId, Vector2Int> pair in manualPositions)
                {
                    if (!itemsById.ContainsKey(pair.Key)) pruneBuffer.Add(pair.Key);
                }

                foreach (ItemId id in pruneBuffer) manualPositions.Remove(id);
                pruneBuffer.Clear();
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
                manualPositions.Clear();
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
            public readonly Dictionary<ItemId, ItemWidget> widgets = new Dictionary<ItemId, ItemWidget>(); // 标识 → 物品框视图。
            public readonly StringBuilder overflowMessage = new StringBuilder(); // 溢出文本拼装缓冲。

            /// <summary>清空本帧的展示项与标识索引（物品列表在每次重绘时重取）。</summary>
            public void ClearIndex()
            {
                displayItems.Clear();
                itemsById.Clear();
            }

            /// <summary>取物品框视图；未创建或已被回收时返回 false。</summary>
            public bool TryGetWidget(ItemId id, out ItemWidget widget) => widgets.TryGetValue(id, out widget);
        }

        /// <summary>单个物品的视图引用集合：根节点与预制体上的物品框视图。</summary>
        private sealed class ItemWidget
        {
            public GameObject root; // 物品框根节点（物品预制体实例）。
            public BackpackItemUI view; // 根节点上的物品框视图。
        }

        #endregion

        #region 运行时状态

        private readonly List<SlotVisual> slotViews = new List<SlotVisual>();
        private readonly LocalDisplayState local = new LocalDisplayState();
        private readonly RenderCache render = new RenderCache();
        private RectTransform rectTransform;
        private InventoryGridLayout layout;
        private EventSubscription dataSubscription;
        private object subscribedEventSource;
        private bool dataDirty;
        private bool presentationVisible = true;
        private ItemWidget carriedPreview;
        private bool itemPrefabWarned;

        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public int GridColumns => Columns;
        public int GridRows => Rows;
        public IBackpackView Inventory { get; private set; }
        /// <summary>单机 / 服务端的实际容器；纯客户端只读镜像没有容器，返回 null。</summary>
        public InventoryContainer Container => Inventory?.EventSource as InventoryContainer;
        public bool IsReady => Inventory != null && Inventory.EventSource != null && itemLayer != null && Columns > 0 && Rows > 0;
        public IReadOnlyList<BackpackItemUI> ItemViews => itemUIList;
        private GridMetrics Metrics => new GridMetrics(cellSize, slotSpacing);

        #endregion

        #region 生命周期与数据绑定

        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
            if (itemUIList == null) itemUIList = new List<BackpackItemUI>();
            ConfigureOverflowText();
        }

        private void Start() => EnsureInitialized();

        private void OnEnable()
        {
            // 不在 Awake 前创建格子：兼容壳可先把旧 Inspector 配置传入。
            if (Columns < 1 || Rows < 1) return;
            UpdateSubscription();
            Redraw();
        }

        private void Update()
        {
            if (Inventory is Component component && component == null)
            {
                UnbindInventory();
                return;
            }

            if (Inventory == null) return;
            if (UpdateSubscription()) Redraw();
            else SyncGridSize(Inventory);
        }

        private void LateUpdate()
        {
            if (!dataDirty) return;
            dataDirty = false;
            Redraw();
        }

        /// <summary>禁用只暂停订阅；来源保持，重新启用时重新读取并订阅。</summary>
        private void OnDisable() => ReleaseSubscription();

        private void OnDestroy() => ReleaseSubscription();

        /// <summary>确保合法占位网格存在；重复调用不会重复生成格子。</summary>
        public void EnsureInitialized()
        {
            if (rectTransform == null) rectTransform = GetComponent<RectTransform>();
            if (itemUIList == null) itemUIList = new List<BackpackItemUI>();
            ConfigureOverflowText();
            if (Columns > 0 && Rows > 0) return;
            BuildGrid(Mathf.Max(1, initialColumns), Mathf.Max(1, initialRows));
        }

        private void ConfigureOverflowText()
        {
            if (overflowText == null) return;
            overflowText.color = warningColor;
            if (uiFont != null) overflowText.font = uiFont;
        }

        /// <summary>显式绑定展示来源；同源重复调用会补接晚初始化的事件键，并保留本地显示状态。</summary>
        public void BindInventory(IBackpackView source)
        {
            EnsureInitialized();
            bool sameSource = ReferenceEquals(Inventory, source);
            if (!sameSource)
            {
                UnbindInventory();
                Inventory = source;
            }

            if (source == null) return;
            bool subscriptionChanged = UpdateSubscription();
            bool gridChanged = SyncGridSize(source);
            // 尺寸改变时 BuildGrid 已重绘；同源、同事件键且同尺寸无需重读物品。
            if (!gridChanged && (!sameSource || subscriptionChanged)) Refresh();
        }

        /// <summary>释放来源与订阅，清空物品显示；占位网格保留供下一次绑定复用。</summary>
        public void UnbindInventory()
        {
            ReleaseSubscription();
            dataDirty = false;
            Inventory = null;
            render.snapshot.Clear();
            local.Clear();
            HideCarriedPreview();
            ClearHoverHighlight();
            Refresh();
        }

        /// <summary>只控制溢出提示的显示条件；窗口显隐由宿主负责。</summary>
        public void SetPresentationVisible(bool visible)
        {
            presentationVisible = visible;
            UpdateOverflow();
        }

        private bool UpdateSubscription()
        {
            object eventSource = Inventory?.EventSource;
            if (ReferenceEquals(subscribedEventSource, eventSource)
                && (eventSource == null || dataSubscription.IsAlive || !isActiveAndEnabled)) return false;

            ReleaseSubscription();
            subscribedEventSource = eventSource;
            if (eventSource != null && isActiveAndEnabled)
            {
                dataSubscription = EventBus.Global.Subscribe(InvChannels.Changed, eventSource, this, OnInventoryChanged);
            }

            return true;
        }

        private void ReleaseSubscription()
        {
            dataSubscription.Dispose();
            dataSubscription = default;
            subscribedEventSource = null;
        }

        private void OnInventoryChanged() => dataDirty = true;

        private bool SyncGridSize(IBackpackView source)
        {
            if (source == null) return false;
            InventoryContainer container = source.EventSource as InventoryContainer;
            int columns = container != null ? container.Columns : source.ConfiguredColumns;
            int rows = container != null ? container.Rows : source.ConfiguredRows;
            if (columns < 1 || rows < 1 || (long)columns * rows > 65536) return false;
            if (columns == Columns && rows == Rows) return false;
            BuildGrid(columns, rows);
            return true;
        }

        #endregion

        #region 网格构建与图层

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

            GridMetrics metrics = Metrics;
            rectTransform.sizeDelta = metrics.PaneSize(columns, rows);

            RectTransform parent = slotLayer != null ? slotLayer : rectTransform;
            GridLayoutGroup gridLayout = parent.GetComponent<GridLayoutGroup>();

            // 布局组件存在时由它统一格子尺寸与间距，避免预制体参数与脚本字段漂移。
            if (gridLayout != null)
            {
                gridLayout.padding = new RectOffset();
                gridLayout.childAlignment = TextAnchor.UpperLeft;
                gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                gridLayout.constraintCount = columns;
                gridLayout.cellSize = new Vector2(cellSize, cellSize);
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
                GameObject slot;
                if (slotPrefab != null) slot = Instantiate(slotPrefab, parent);
                else
                {
                    slot = new GameObject("Slot", typeof(RectTransform), typeof(Image));
                    slot.transform.SetParent(parent, false);
                    slot.layer = gameObject.layer;
                }
                slot.name = "Slot_" + i;
                RectTransform rect = (RectTransform)slot.transform;

                // 没有布局组件时手工摆放，像素口径与物品框一致。
                if (gridLayout == null)
                {
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                    rect.sizeDelta = new Vector2(cellSize, cellSize);
                    rect.anchoredPosition = metrics.ManualSlotPosition(i, columns);
                }

                Image image = slot.GetComponent<Image>();
                if (image != null)
                {
                    image.raycastTarget = false;
                    image.color = slotEmptyColor;
                }

                slotViews.Add(new SlotVisual { root = slot, image = image, hover = BackpackHoverState.None });
            }

            EnsureItemLayer();
            Refresh();
        }

        /// <summary>物品图层：留空时创建为面板子物体，并标记忽略布局，避免被网格布局当成又一格。</summary>
        private void EnsureItemLayer()
        {
            if (itemLayer == null)
            {
                itemLayer = new GameObject("ItemLayer", typeof(RectTransform)).GetComponent<RectTransform>();
                itemLayer.SetParent(rectTransform, false);
                itemLayer.gameObject.layer = 5;
            }

            if (!itemLayer.TryGetComponent(out LayoutElement layoutElement)) layoutElement = itemLayer.gameObject.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;
            itemLayer.anchorMin = itemLayer.anchorMax = itemLayer.pivot = new Vector2(0, 1);
            itemLayer.anchoredPosition = Vector2.zero;
            itemLayer.sizeDelta = Metrics.LayerSize(Columns, Rows);
            itemLayer.SetAsLastSibling();
            EnsureCursorLayer();
        }

        /// <summary>携带预览图层：留空时创建为面板子物体，并标记忽略布局、置于物品层之上。</summary>
        private void EnsureCursorLayer()
        {
            if (cursorLayer == null)
            {
                cursorLayer = new GameObject("CursorLayer", typeof(RectTransform)).GetComponent<RectTransform>();
                cursorLayer.SetParent(rectTransform, false);
                cursorLayer.gameObject.layer = 5;
            }

            if (!cursorLayer.TryGetComponent(out LayoutElement layoutElement)) layoutElement = cursorLayer.gameObject.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;
            cursorLayer.anchorMin = cursorLayer.anchorMax = cursorLayer.pivot = new Vector2(0, 1);
            cursorLayer.anchoredPosition = Vector2.zero;
            cursorLayer.sizeDelta = itemLayer.sizeDelta;
            cursorLayer.SetAsLastSibling();
        }

        #endregion

        #region 重绘管线

        /// <summary>本机状态（本地位置表、隐藏物、悬停态）变化后重绘一次；数据层变化经 Inv.Changed 广播置脏、帧末合并，不经过此方法。</summary>
        public void Redraw()
        {
            EnsureInitialized();
            SyncGridSize(Inventory);
            Refresh();
        }

        /// <summary>重读数据源并按当前物品重排、重绘格子底色 / 物品框 / 溢出文本；只在绑定、库存变化与格子重建时执行，不逐帧读取物品。</summary>
        private void Refresh()
        {
            if (itemLayer == null || Columns < 1 || Rows < 1) return;
            GridMetrics metrics = Metrics;
            if (layout == null || layout.Columns != Columns || layout.Rows != Rows) layout = new InventoryGridLayout(Columns, Rows);

            // 每次重绘都重取物品列表：堆叠数是活引用会自更新，但增删物品（拆分出的新实例、被移除的源物品）必须重读列表才可见。
            if (Inventory != null) Inventory.CopyItems(render.snapshot);
            else render.snapshot.Clear();

            render.ClearIndex();
            foreach (RuntimeItem item in render.snapshot)
            {
                if (item == null) continue;
                GridFragment grid = item.GetFragment<GridFragment>();
                render.displayItems.Add(new GridDisplayItem(item.ItemId, Mathf.Max(1, grid?.Width ?? 1), Mathf.Max(1, grid?.Height ?? 1)));
                render.itemsById[item.ItemId] = item;
            }

            // 本地位置表优先于自动寻位；已消失的物品同步清掉位置记录。
            local.PruneManualPositions(render.itemsById);
            local.PruneStackDisplayOverrides(render.itemsById);
            layout.Arrange(render.displayItems, local.ManualPositions);

            // 格子底色：悬停反馈态优先，其次空闲 / 占用两态。
            ApplySlotColors();

            // 回收已消失或本帧放不下的物品框。
            render.obsolete.Clear();
            foreach (KeyValuePair<ItemId, ItemWidget> pair in render.widgets)
            {
                if (!render.itemsById.ContainsKey(pair.Key) || !IsPlaced(pair.Key)) render.obsolete.Add(pair.Key);
            }

            foreach (ItemId id in render.obsolete)
            {
                ItemWidget obsolete = render.widgets[id];
                if (obsolete.root != null)
                {
                    obsolete.root.SetActive(false);
                    Destroy(obsolete.root);
                }
                if (obsolete.view != null) itemUIList.Remove(obsolete.view);
                render.widgets.Remove(id);
            }

            // 创建或复用物品框，并按落点换算像素位置与尺寸。
            foreach (GridDisplayPlacement placement in layout.Placed)
            {
                if (!render.widgets.TryGetValue(placement.Item.Id, out ItemWidget widget) || widget.root == null)
                {
                    widget = CreateWidget(itemLayer);
                    if (widget == null) continue; // 预制体缺失或不合规：跳过本帧，避免连带异常。
                    render.widgets[placement.Item.Id] = widget;
                    itemUIList.Add(widget.view);
                }

                RectTransform rect = (RectTransform)widget.root.transform;
                rect.anchoredPosition = metrics.CellToPixel(placement.Position);
                widget.view.SetSize(metrics.ItemPixelSize(placement.Item.Width, placement.Item.Height));

                // 携带中的物品隐藏但仍占格（位置记录保持不变，放回时可原样复位）。
                bool hidden = local.HiddenItemId.IsValid && placement.Item.Id == local.HiddenItemId;
                widget.root.SetActive(!hidden);
                if (hidden) continue;

                RuntimeItem placed = render.itemsById[placement.Item.Id];
                ApplyWidget(widget, placed, local.ResolveDisplayStack(placed));
                if (local.HoveredItemId.IsValid && placement.Item.Id == local.HoveredItemId) ApplyWidgetHover(widget, true);
            }

            UpdateOverflow();
        }

        /// <summary>物品是否在本帧排布结果中。</summary>
        private bool IsPlaced(ItemId id)
        {
            foreach (GridDisplayPlacement placement in layout.Placed)
            {
                if (placement.Item.Id == id) return true;
            }

            return false;
        }

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

            overflowText.text = render.overflowMessage.ToString();
            overflowText.gameObject.SetActive(presentationVisible && render.overflowMessage.Length > 0);
        }

        /// <summary>按当前悬停态与占用态刷新全部格子底色（重绘、解绑时调用）。</summary>
        private void ApplySlotColors()
        {
            if (layout == null) return;
            for (int i = 0; i < slotViews.Count; i++)
            {
                SlotVisual slot = slotViews[i];
                if (slot.image == null) continue;
                bool occupied = i < layout.Occupied.Count && layout.Occupied[i];
                slot.image.color = SlotColor(slot.hover, occupied);
            }
        }

        /// <summary>写入单个格子的悬停反馈态；状态未变化时直接返回，避免多余的 Canvas 重建。</summary>
        private void SetSlotHoverState(int index, BackpackHoverState state)
        {
            if (index < 0 || index >= slotViews.Count) return;
            SlotVisual slot = slotViews[index];
            if (slot.hover == state) return;
            slot.hover = state;
            if (layout == null || slot.image == null) return;
            bool occupied = index < layout.Occupied.Count && layout.Occupied[index];
            slot.image.color = SlotColor(state, occupied);
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

        /// <summary>写入物品的本地位置（列、行）；越界位置在排布时会被忽略并自动寻位。</summary>
        public void SetManualPosition(ItemId id, Vector2Int position) => local.SetManualPosition(id, position);

        /// <summary>清除某物品的本地位置记录；返回是否确有记录。</summary>
        public bool ClearManualPosition(ItemId id) => local.ClearManualPosition(id);

        /// <summary>写入某物品的显示数量覆盖（右键拆分时源物品显示「总数 − 已取数量」）；只影响本机显示，不改动库存数据。调用方随后自行 <see cref="Redraw"/>。</summary>
        public void SetStackDisplay(ItemId id, int displayStack) => local.SetStackDisplay(id, displayStack);

        /// <summary>清除某物品的显示数量覆盖；返回是否确有覆盖。</summary>
        public bool ClearStackDisplay(ItemId id) => local.ClearStackDisplay(id);

        /// <summary>清除全部显示数量覆盖（结束携带、会话复位与超时兜底用）。</summary>
        public void ClearAllStackDisplays() => local.ClearAllStackDisplays();

        /// <summary>当前是否还有显示数量覆盖（超时兜底据此判断是否真的需要还原）。</summary>
        public bool HasStackDisplayOverride => local.HasStackDisplayOverride;

        /// <summary>携带期间把某物品从网格隐藏（仍占用格子，避免其它物品回流导致抖动）。</summary>
        public void SetHiddenItem(ItemId id) => local.SetHiddenItem(id);

        /// <summary>取消隐藏。</summary>
        public void ClearHiddenItem() => local.ClearHiddenItem();

        /// <summary>切换某物品的悬停高亮（交互层只在悬停目标变化时调用）。</summary>
        public void SetItemHovered(ItemId id, bool hovered)
        {
            if (!id.IsValid) return;
            local.SetHoveredItem(id, hovered);
            if (render.TryGetWidget(id, out ItemWidget widget) && widget.root != null) ApplyWidgetHover(widget, hovered);
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

        /// <summary>复制本帧快照中的全部物品标识（拆分结果认领时做标识比对用）。</summary>
        public void CopyItemIds(List<ItemId> destination)
        {
            if (destination == null) return;
            destination.Clear();
            foreach (RuntimeItem item in render.snapshot)
            {
                if (item != null) destination.Add(item.ItemId);
            }
        }

        /// <summary>按标识取本帧快照中的物品数据。</summary>
        public bool TryGetItem(ItemId id, out RuntimeItem item) => render.itemsById.TryGetValue(id, out item);

        /// <summary>取物品当前的本机排布（落点 + 占位尺寸）。</summary>
        public bool TryGetPlacement(ItemId id, out GridDisplayPlacement placement)
        {
            if (layout != null)
            {
                for (int i = 0; i < layout.Placed.Count; i++)
                {
                    if (layout.Placed[i].Item.Id == id)
                    {
                        placement = layout.Placed[i];
                        return true;
                    }
                }
            }

            placement = default;
            return false;
        }

        #endregion

        #region 携带预览

        private const float CarriedAlpha = 0.78f; // 携带预览物的整体透明度。

        /// <summary>确保携带预览物存在并显示指定物品（半透明、不接收指针事件）；显示数量由调用方给出（预览与源物品同标识，不能走按标识的覆盖表）。</summary>
        public bool TryShowCarriedPreview(RuntimeItem item, int displayStack)
        {
            if (item == null) return false;
            EnsureCursorLayer();
            if (cursorLayer == null) return false;

            if (carriedPreview == null)
            {
                carriedPreview = CreateWidget(cursorLayer);
                if (carriedPreview == null) return false;
            }

            GridFragment grid = item.GetFragment<GridFragment>();
            carriedPreview.root.SetActive(true);
            carriedPreview.view.SetSize(
                Metrics.ItemPixelSize(Mathf.Max(1, grid?.Width ?? 1), Mathf.Max(1, grid?.Height ?? 1)));
            ApplyWidget(carriedPreview, item, displayStack);
            SetWidgetAlpha(carriedPreview, CarriedAlpha);
            carriedPreview.root.name = "CarriedPreview"; // ApplyWidget 按物品改名，预览物在最后统一命名。
            return true;
        }

        /// <summary>把携带预览物定位到网格局部坐标（左上角为原点，单位为格）。</summary>
        public void SetCarriedPreviewCellPosition(Vector2 cellPosition)
        {
            if (carriedPreview == null || carriedPreview.root == null) return;
            // 预览可以位于双栏共用的上层；落点仍按当前网格的局部坐标换算。
            carriedPreview.root.transform.position = itemLayer.TransformPoint(Metrics.CellToPixel(cellPosition));
        }

        /// <summary>把携带预览物定位到屏幕坐标（未命中网格时的自由跟随）：被点击的那一格中心对准光标，与网格内贴格口径一致。</summary>
        public bool TrySetCarriedPreviewScreenPosition(Vector2 screenPoint, Camera uiCamera, Vector2Int grabOffset)
        {
            if (carriedPreview == null || carriedPreview.root == null || cursorLayer == null) return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(cursorLayer, screenPoint, uiCamera, out Vector2 local)) return false;

            Vector2 grabCenter = Metrics.GrabCenterOffset(grabOffset);
            float x = local.x;
            float y = -local.y;
            ((RectTransform)carriedPreview.root.transform).anchoredPosition =
                new Vector2(x - grabCenter.x, -(y - grabCenter.y));
            return true;
        }

        /// <summary>隐藏并复位携带预览物。</summary>
        public void HideCarriedPreview()
        {
            if (carriedPreview == null || carriedPreview.root == null) return;
            carriedPreview.root.SetActive(false);
            SetWidgetAlpha(carriedPreview, 1f);
        }

        #endregion

        #region M6 查询与几何换算

        /// <summary>取某格当前的占用者（物品标识 + 其上左线性索引）。</summary>
        public bool TryGetCellOwner(int index, out ItemId ownerId, out int upperLeftIndex)
        {
            if (layout == null)
            {
                ownerId = ItemId.None;
                upperLeftIndex = SpaceQueryResult<RuntimeItem>.NoIndex;
                return false;
            }

            return layout.TryGetCellOwner(index, out ownerId, out upperLeftIndex);
        }

        /// <summary>基于本地排布做落点空间查询：语义与 InventoryContainer.QuerySpace 一致但以本机显示位置为准，是本交互层同一帧内唯一的落点判定来源。</summary>
        public SpaceQueryResult<RuntimeItem> QuerySpace(int startIndex, GridCoord dimensions)
        {
            if (layout == null || dimensions.X < 1 || dimensions.Y < 1) return SpaceQueryResult<RuntimeItem>.Empty;
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
        private static void ApplyWidget(ItemWidget widget, RuntimeItem item, int displayStack)
        {
            if (widget?.view == null || item == null) return;
            widget.root.name = "Item_" + item.ItemId.Value;
            ImageFragment image = item.GetFragment<ImageFragment>();
            widget.view.SetItem(image != null ? image.Icon : null, displayStack);
        }

        /// <summary>切换物品框的悬停底色；取消悬停由视图还原为预制体原色。</summary>
        private static void ApplyWidgetHover(ItemWidget widget, bool hovered)
        {
            if (widget?.view == null) return;
            widget.view.SetHover(hovered);
        }

        /// <summary>写入物品框整体透明度（携带预览用）。</summary>
        private static void SetWidgetAlpha(ItemWidget widget, float alpha)
        {
            if (widget?.view == null) return;
            widget.view.SetAlpha(alpha);
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
