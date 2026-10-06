using System;
using System.Collections.Generic;
using System.Text;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using RPG4.Input;

namespace RPG4.Backpack.UI
{
    /// <summary>背包格子 UI 主控：生成格子并实例化物品预制体渲染物品框，订阅输入宿主（InputManager 的 backpack 映射）的开关与左右键并接管开关、鼠标光标与玩家输入；对外提供几何换算、本地位置表、携带预览与悬停高亮（M6 交互控制层只读这些能力），本组件不做玩法决策。物品坐标只是本机展示状态，不参与网络复制。</summary>
    [DisallowMultipleComponent]
    public class BackpackGridUI : MonoBehaviour
    {
        #region 序列化字段

        public GameObject slotPrefab; // 格子预制体（需带 Image）。
        public GameObject itemPrefab; // 物品框预制体（根节点需挂 BackpackItemUI）。
        public List<BackpackItemUI> itemUIList; // 运行时活跃的物品框登记表（代码独占维护，勿在场景中预拖）。
        [Header("网格")]
        public int initialColumns = 4; // 未绑定数据源前的占位列数；绑定后按数据源的配置尺寸重建。
        public int initialRows = 5; // 未绑定数据源前的占位行数；绑定后按数据源的配置尺寸重建。
        public float cellSize = 100f; // 单格边长（像素）；面板与物品框尺寸都按它换算。
        public Vector2 slotSpacing; // 格子之间的间距（x 横向、y 纵向）。
        public RectTransform slotLayer; // 格子图层；有 GridLayoutGroup 时由其排布，否则脚本手工摆放。
        public RectTransform itemLayer; // 物品图层；留空时创建为面板子物体。

        [Header("界面")]
        public TMP_Text hudHint; // 关闭背包时的操作提示。
        public TMP_Text overflowText; // 放不下的物品提示（窗口打开时显示）。
        public TMP_FontAsset uiFont; // 脚本运行时创建的格子 / 提示文本所用字体；留空用 TMP 默认字体（物品框文本由预制体自带字体）。

        public float bindingCheckInterval = 0.25f; // 本地玩家背包的重查周期（秒）。

        [Header("输入")]
        public string openKeyHint; // 关闭态提示里显示的按键名（仅文案）；留空时取开关键的实际绑定显示名，实际按键绑定在 InputActions.inputactions 的 backpack 映射上。

        [Header("交互（M6）")]
        public RectTransform cursorLayer; // 携带预览图层；留空时创建为面板子物体并置于物品层之上。
        public BackpackInteractionController interaction; // 交互控制层；随本组件的数据源一起绑定 / 解绑，留空则只有网格表现。

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

        /// <summary>背包输入动作的订阅句柄：三个动作、跨帧订阅状态与宿主缺失告警位；订阅 / 退订成对，回调用同一委托实例增删。</summary>
        private sealed class InputBinding
        {
            public InputAction toggle; // 开关动作（backpack/BackPackUI）。
            public InputAction primaryClick; // 左键动作（backpack/leftButton）。
            public InputAction secondaryClick; // 右键动作（backpack/rightButton）。
            public bool bound; // 是否已订阅；宿主缺失时保持 false，由 Update 逐帧重试。
            public bool warned; // 宿主缺失的警告是否已输出（只在状态翻转时打一条，避免逐帧刷屏）。

            private Action<InputAction.CallbackContext> toggleHandler; // 已注册的开关回调。
            private Action<InputAction.CallbackContext> primaryHandler; // 已注册的左键回调。
            private Action<InputAction.CallbackContext> secondaryHandler; // 已注册的右键回调。

            /// <summary>订阅宿主提供的三个动作；宿主缺失时返回 false（先退订，保证重复绑定不会叠加订阅）。</summary>
            public bool TryBind(
                InputManager host,
                Action<InputAction.CallbackContext> onToggle,
                Action<InputAction.CallbackContext> onPrimary,
                Action<InputAction.CallbackContext> onSecondary)
            {
                Unbind();
                if (host == null) return false;

                InputActions.BackpackActions actions = host.backpackInput;
                toggle = actions.BackPackUI;
                primaryClick = actions.leftButton;
                secondaryClick = actions.rightButton;

                // 映射的启用与释放由宿主独占，这里只增删回调。
                toggleHandler = onToggle;
                primaryHandler = onPrimary;
                secondaryHandler = onSecondary;
                toggle.performed += toggleHandler;
                primaryClick.performed += primaryHandler;
                secondaryClick.performed += secondaryHandler;
                bound = true;
                return true;
            }

            /// <summary>退订输入动作；映射的启用与释放由宿主独占，此处不触碰映射状态。</summary>
            public void Unbind()
            {
                if (toggle != null && toggleHandler != null) toggle.performed -= toggleHandler;
                if (primaryClick != null && primaryHandler != null) primaryClick.performed -= primaryHandler;
                if (secondaryClick != null && secondaryHandler != null) secondaryClick.performed -= secondaryHandler;

                toggle = null;
                primaryClick = null;
                secondaryClick = null;
                toggleHandler = null;
                primaryHandler = null;
                secondaryHandler = null;
                bound = false;
            }
        }

        /// <summary>打开背包前的输入与光标状态快照，关闭时原样还原。</summary>
        private struct WindowInputSnapshot
        {
            private bool inputWasEnabled; // 采集时的输入组件启用状态。
            private bool cursorWasVisible; // 采集时的光标可见状态。
            private CursorLockMode cursorWasLocked; // 采集时的光标锁定状态。

            /// <summary>采集当前输入与光标状态。</summary>
            public WindowInputSnapshot(PlayerInput playerInput)
            {
                inputWasEnabled = playerInput != null && playerInput.enabled;
                cursorWasVisible = Cursor.visible;
                cursorWasLocked = Cursor.lockState;
            }

            /// <summary>按采集值暂停本地玩家输入（原本就未启用时不写回）。</summary>
            public void SuspendInput(PlayerInput playerInput)
            {
                if (playerInput != null && inputWasEnabled) playerInput.enabled = false;
            }

            /// <summary>还原采集时的输入与光标状态。</summary>
            public void Restore(PlayerInput playerInput)
            {
                if (playerInput != null) playerInput.enabled = inputWasEnabled;
                Cursor.lockState = cursorWasLocked;
                Cursor.visible = cursorWasVisible;
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

        private readonly List<SlotVisual> slotViews = new List<SlotVisual>(); // 已生成的格子（运行时生成，仅本组件使用）。
        private readonly InputBinding binding = new InputBinding(); // 输入订阅句柄（开关与左右键）。
        private readonly LocalDisplayState local = new LocalDisplayState(); // 本机显示状态（位置表 / 显示覆盖 / 隐藏物 / 悬停物）。
        private readonly RenderCache render = new RenderCache(); // 重绘缓存与视图索引。

        private RectTransform rectTransform; // 自身 RectTransform。
        private InventoryGridLayout layout; // 本机占位排布（位置不参与网络复制）。
        private EventSubscription dataSubscription; // 数据变更（Inv.Changed）订阅句柄；与绑定对象成对创建与释放。
        private bool dataDirty; // 数据变更脏标记：帧末合并为一次重绘。
        private PlayerInput playerInput; // 本机玩家的输入组件（打开背包时暂停）。
        private WindowInputSnapshot windowSnapshot; // 打开背包前的输入与光标快照。
        private bool isOpen; // 菜单是否处于打开状态。
        private float nextBindingCheck; // 下一次重查本地玩家的时间点。
        private ItemWidget carriedPreview; // 跟随光标的携带预览物。
        private bool itemPrefabWarned; // 物品预制体缺失 / 不合规的警告是否已输出（输出后不再重复尝试实例化）。

        public int Columns { get; private set; } // 当前网格列数（交互控制层换算落点用）。
        private int Rows { get; set; } // 当前网格行数（仅本组件内部使用）。
        private IBackpackView Inventory { get; set; } // 已绑定的本机玩家背包视图（单机或网络宿主）；未绑定时为 null。
        public bool IsOpen => isOpen; // 菜单是否打开。

        /// <summary>当前活跃的物品框视图（登记表的只读视图，外部查询 / 调试用；网格内物品框与携带预览无关）。</summary>
        public IReadOnlyList<BackpackItemUI> ItemViews => itemUIList;

        /// <summary>当前网格度量（由序列化字段现取现建，值类型零分配）。</summary>
        private GridMetrics Metrics => new GridMetrics(cellSize, slotSpacing);

        /// <summary>开关键的可读名称（关闭态提示文本用）：优先用手填文案，留空则取开关动作的实际绑定显示名，避免与输入资产漂移。</summary>
        private string OpenKeyLabel
        {
            get
            {
                if (!string.IsNullOrEmpty(openKeyHint)) return openKeyHint;
                return binding.toggle != null ? binding.toggle.GetBindingDisplayString() : "未设置";
            }
        }

        #endregion

        #region 生命周期

        /// <summary>缓存组件引用；格子生成在 Start 中执行。</summary>
        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
            if (overflowText != null)
            {
                overflowText.color = BackpackFeedbackPalette.Warning;
                if (uiFont != null) overflowText.font = uiFont;
            }
        }

        /// <summary>启动时先生成占位格子；绑定数据源后按数据源的配置尺寸重建。</summary>
        private void Start()
        {
            BuildGrid(initialColumns, initialRows);
        }

        /// <summary>启用时复位绑定计时、关闭窗口并订阅输入动作。</summary>
        private void OnEnable()
        {
            nextBindingCheck = 0f;
            SetWindowVisible(false);
            BindInputActions();
        }

        /// <summary>未订阅输入时逐帧重试（宿主可能晚于本组件就绪，也可能被销毁后再出现）；周期性重查本地玩家库存。开关由订阅的 backpack/BackPackUI 动作触发 <see cref="Toggle"/>，此处不逐帧读设备。</summary>
        private void Update()
        {
            if (InputManager.Instance == null)
            {
                if (binding.bound) UnbindInputActions(); // 宿主已销毁：退订悬挂的动作，等宿主重新出现再绑。
            }
            else if (!binding.bound)
            {
                BindInputActions();
            }

            if (Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + Mathf.Max(0.05f, bindingCheckInterval);
                BindInventory(ResolveSource());
            }
        }

        /// <summary>帧末合并数据变更：一帧内多次广播只重绘一次。</summary>
        private void LateUpdate()
        {
            if (!dataDirty) return;

            dataDirty = false;
            Refresh();
        }

        /// <summary>禁用时退订输入并解绑。</summary>
        private void OnDisable()
        {
            UnbindInputActions();
            Unbind();
        }

        /// <summary>销毁时解绑。</summary>
        private void OnDestroy() => Unbind();

        #endregion

        #region 数据源绑定

        /// <summary>解析绑定对象：优先本地玩家身上的网络库存，未联机时回退为单机宿主的「本地玩家背包」。</summary>
        private static IBackpackView ResolveSource()
        {
            NetworkIdentity localIdentity = NetworkClient.localPlayer;
            if (localIdentity != null)
            {
                BackpackNetworkInventory network = localIdentity.GetComponent<BackpackNetworkInventory>();
                if (network != null) return network;
            }

            return BackpackRegistry.TryGetPrimary(out BackpackInventory backpack) ? backpack : null;
        }

        /// <summary>绑定本机玩家背包视图：先按数据源同步网格尺寸并渲染（物品列表在每次重绘时从数据源重取），之后才注册监听；重复绑定同一实例直接忽略。</summary>
        private void BindInventory(IBackpackView source)
        {
            if (ReferenceEquals(Inventory, source) && (source == null || dataSubscription.IsAlive)) return;
            Unbind();
            if (source == null) return;

            Inventory = source;
            playerInput = (source as Component)?.GetComponent<PlayerInput>();
            SyncGridSize(source);
            Refresh();
            SetHint(OpenKeyLabel + "   /   打开背包");
            if (source.EventSource == null)
            {
                BackpackLog.Warn(LogModule.UI, "数据源未提供事件管线键，界面不会自动刷新。");
            }
            else
            {
                dataSubscription = EventBus.Global.Subscribe(InvChannels.Changed, source.EventSource, this, OnInventoryChanged);
            }

            if (interaction != null) interaction.Bind(source);
            BackpackLog.Info(LogModule.UI, "已绑定本地玩家库存：" + DescribeSource(source));
        }

        /// <summary>把网格尺寸同步为数据源的配置尺寸：与当前不同即重建格子；尺寸非法时保留现网格并记警告。</summary>
        private void SyncGridSize(IBackpackView source)
        {
            if (source == null) return;

            int columns = source.ConfiguredColumns;
            int rows = source.ConfiguredRows;
            if (columns < 1 || rows < 1)
            {
                BackpackLog.Warn(LogModule.UI, $"数据源网格尺寸非法（{columns} × {rows}），沿用当前 {Columns} × {Rows}。");
                return;
            }

            if (columns == Columns && rows == Rows) return;

            BuildGrid(columns, rows);
            BackpackLog.Info(LogModule.UI, $"网格已同步数据源尺寸：{columns} × {rows}。");
        }

        /// <summary>数据源广播变化时只置脏，帧末统一重绘一次。</summary>
        private void OnInventoryChanged()
        {
            dataDirty = true;
        }

        /// <summary>数据源的可读描述（日志用）：网络宿主输出 netId，单机宿主输出物体名。</summary>
        private static string DescribeSource(IBackpackView source)
        {
            if (source is BackpackNetworkInventory network) return "netId=" + network.netId;
            if (source is Component component) return component.name;
            return source.ToString();
        }

        /// <summary>解绑当前数据源：还原输入与光标、退订、清空已渲染内容与配置警告。</summary>
        private void Unbind()
        {
            if (interaction != null) interaction.Unbind();
            if (isOpen) RestoreInput();
            isOpen = false;
            dataSubscription.Dispose();
            dataSubscription = default;
            dataDirty = false;
            Inventory = null;
            playerInput = null;
            render.snapshot.Clear();
            itemUIList.Clear();
            local.Clear();
            HideCarriedPreview();
            ClearHoverHighlight();
            SetWindowVisible(false);
            Refresh();
            SetHint("等待本地玩家 · " + OpenKeyLabel + " 打开背包");
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
                if (oldSlot.root != null) Destroy(oldSlot.root);
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
                    rect.sizeDelta = new Vector2(cellSize, cellSize);
                    rect.anchoredPosition = metrics.ManualSlotPosition(i, columns);
                }

                Image image = slot.GetComponent<Image>();
                if (image != null)
                {
                    image.raycastTarget = false;
                    image.color = BackpackFeedbackPalette.SlotEmpty;
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
        public void Redraw() => Refresh();

        /// <summary>重读数据源并按当前物品重排、重绘格子底色 / 物品框 / 溢出文本；只在绑定、库存变化与格子重建时执行，不逐帧读取物品。</summary>
        private void Refresh()
        {
            if (itemLayer == null || Columns < 1 || Rows < 1) return;
            GridMetrics metrics = Metrics;
            if (layout == null || layout.Columns != Columns || layout.Rows != Rows) layout = new InventoryGridLayout(Columns, Rows);

            // 每次重绘都重取物品列表：堆叠数是活引用会自更新，但增删物品（拆分出的新实例、被移除的源物品）必须重读列表才可见。
            if (Inventory != null) Inventory.CopyItems(render.snapshot);

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
                if (obsolete.root != null) Destroy(obsolete.root);
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
            overflowText.gameObject.SetActive(isOpen && render.overflowMessage.Length > 0);
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
        private static Color SlotColor(BackpackHoverState state, bool occupied)
        {
            switch (state)
            {
                case BackpackHoverState.Available: return BackpackFeedbackPalette.SlotAvailable;
                case BackpackHoverState.Blocked: return BackpackFeedbackPalette.SlotBlocked;
                case BackpackHoverState.Swap: return BackpackFeedbackPalette.SlotSwap;
                default: return occupied ? BackpackFeedbackPalette.SlotFilled : BackpackFeedbackPalette.SlotEmpty;
            }
        }

        #endregion

        #region 输入与开关

        /// <summary>订阅输入宿主（InputManager）提供的 backpack 动作：取开关与左右键三个动作并注册回调；宿主缺失时只记一条警告、不做输入回退，界面照常可用，未绑定态由 Update 逐帧重试。</summary>
        private void BindInputActions()
        {
            if (binding.TryBind(InputManager.Instance, OnToggleAction, OnPrimaryClickAction, OnSecondaryClickAction))
            {
                binding.warned = false;
                BackpackLog.Info(LogModule.UI, "已订阅输入宿主（InputManager）的 backpack 动作。");
                return;
            }

            if (binding.warned) return;
            binding.warned = true;
            BackpackLog.Warn(LogModule.UI, "场景里没有 InputManager，背包收不到开关与点击。");
        }

        /// <summary>退订输入动作；映射的启用与释放由宿主独占，此处不触碰映射状态。</summary>
        private void UnbindInputActions() => binding.Unbind();

        /// <summary>开关动作（backpack/BackPackUI）：按下即请求切换。</summary>
        private void OnToggleAction(InputAction.CallbackContext context)
        {
            if (context.performed) Toggle();
        }

        /// <summary>左键动作（backpack/leftButton）：转交交互控制层入队。</summary>
        private void OnPrimaryClickAction(InputAction.CallbackContext context)
        {
            if (context.performed) PushClick(UiChannels.MouseButton.Left);
        }

        /// <summary>右键动作（backpack/rightButton）：转交交互控制层入队。</summary>
        private void OnSecondaryClickAction(InputAction.CallbackContext context)
        {
            if (context.performed) PushClick(UiChannels.MouseButton.Right);
        }

        /// <summary>把点击推给交互控制层；背包开关状态由控制层自行判定，此处只做转发。</summary>
        private void PushClick(int mouseButton)
        {
            BackpackLog.Debug(LogModule.UI, mouseButton == UiChannels.MouseButton.Left ? "背包主控：收到左键。" : "背包主控：收到右键。");
            if (interaction == null)
            {
                BackpackLog.Warn(LogModule.UI, "背包主控：interaction 未接线，点击被丢弃。");
                return;
            }

            interaction.PushClick(mouseButton);
        }

        /// <summary>请求切换背包开关（输入动作与外部按钮的意图入口）：窗口、光标与输入都按同一条状态联动。</summary>
        public void Toggle()
        {
            if (Inventory == null || Columns < 1 || Rows < 1) return;
            ApplyOpenState(!isOpen);
        }

        /// <summary>应用开关状态：打开时接管光标并暂停本地玩家输入，关闭时还原打开前保存的状态。</summary>
        private void ApplyOpenState(bool open)
        {
            if (isOpen == open) return;
            if (open)
            {
                SyncGridSize(Inventory); // 每次打开都按数据源当前尺寸重建，容量变化即时生效。
                windowSnapshot = new WindowInputSnapshot(playerInput);
                windowSnapshot.SuspendInput(playerInput);
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                RestoreInput();
            }

            isOpen = open;
            SetWindowVisible(open);
            if (hudHint != null) hudHint.gameObject.SetActive(!open);
            UpdateOverflow();
            BackpackLog.Info(LogModule.UI, open ? "背包已打开。" : "背包已关闭。");
        }

        /// <summary>还原打开背包前保存的光标与输入状态。</summary>
        private void RestoreInput() => windowSnapshot.Restore(playerInput);

        /// <summary>用 CanvasGroup 显隐窗口：组件自身不做 SetActive，避免脚本挂在窗口上时被停用后无法再监听开关键。</summary>
        private void SetWindowVisible(bool visible)
        {
            GameObject target = gameObject;
            if (!target.TryGetComponent(out CanvasGroup canvasGroup)) canvasGroup = target.AddComponent<CanvasGroup>();
            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.interactable = visible;
            canvasGroup.blocksRaycasts = visible;
            if (visible && !target.activeSelf) target.SetActive(true);
        }

        /// <summary>更新关闭态的操作提示。</summary>
        private void SetHint(string text)
        {
            if (hudHint == null) return;
            hudHint.text = text;
            hudHint.gameObject.SetActive(!isOpen);
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
            ((RectTransform)carriedPreview.root.transform).anchoredPosition = Metrics.CellToPixel(cellPosition);
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
