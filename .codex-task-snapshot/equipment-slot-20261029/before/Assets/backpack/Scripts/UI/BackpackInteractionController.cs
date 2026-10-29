using System;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.UI
{
    /// <summary>单双栏共用的物品交互会话：由宿主开始、传入输入并结束。位置与数量都由权威库存提供，操作以完整事务提交。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public sealed class BackpackInteractionController : MonoBehaviour
    {
        private const float OperationTimeout = 5f;
        [SerializeField] private RectTransform cursorLayer;
        [SerializeField] private InventorySplitQuantityUI quantityDialog;

        public RectTransform CursorLayer => cursorLayer;

        /// <summary>宿主在 Update 中传入的本帧输入；交互组件不直接读取输入系统。</summary>
        public readonly struct SessionInput
        {
            public Vector2 PointerPosition { get; }
            public bool HasPointer { get; }
            public bool PrimaryClick { get; }
            public bool SecondaryClick { get; }
            public bool Cancel { get; }
            public bool QuickTransfer { get; }
            public bool ChooseSplitQuantity { get; }

            public SessionInput(Vector2 pointerPosition, bool hasPointer, bool primaryClick, bool secondaryClick, bool cancel, bool quickTransfer = false, bool chooseSplitQuantity = false)
            {
                PointerPosition = pointerPosition;
                HasPointer = hasPointer;
                PrimaryClick = primaryClick;
                SecondaryClick = secondaryClick;
                Cancel = cancel;
                QuickTransfer = quickTransfer;
                ChooseSplitQuantity = chooseSplitQuantity;
            }
        }

        private enum DropAction { Blocked, Return, Place, SwapPosition, Merge, SwapStackCounts, Split, Transfer }

        private sealed class CarrySession
        {
            public BackpackContainerUI Source;
            public IBackpackView Inventory;
            public RuntimeItem Item;
            public int SourceCount;
            public int Count;
            public int OriginIndex;
            public int Columns;
            public int Rows;
            public Vector2Int GrabOffset;
            public GridCoord Dimensions;
            public bool IsPartial => Count < SourceCount;
        }

        private sealed class PendingOperation
        {
            public int RequestId;
            public BackpackContainerUI Destination;
            public IBackpackView Inventory;
            public float Deadline;
            public InventoryOperationResult? Result;
            public EventSubscription Subscription;
        }

        /// <summary>整个会话只有一个动态物品框，跨栏时只改变尺寸和位置。</summary>
        private sealed class CarryPreview
        {
            private BackpackItemUI view;
            private ItemId itemId;
            private int count;
            private Vector2 size;

            public void Show(BackpackContainerUI target, CarrySession carry, Vector2 point, int index, RectTransform cursorLayer)
            {
                if (view == null)
                {
                    if (target.ItemPrefab == null || cursorLayer == null) return;
                    GameObject root = Instantiate(target.ItemPrefab, cursorLayer);
                    if (!root.TryGetComponent(out view))
                    {
                        Destroy(root);
                        return;
                    }
                    root.name = "CarriedPreview";
                    RectTransform rect = (RectTransform)root.transform;
                    rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                    rect.localScale = Vector3.one;
                    CanvasGroup group = root.GetComponent<CanvasGroup>();
                    group.interactable = false;
                    group.blocksRaycasts = false;
                    view.SetAlpha(0.78f);
                    itemId = ItemId.None;
                    size = Vector2.zero;
                }

                if (view.transform.parent != cursorLayer) view.transform.SetParent(cursorLayer, false);
                Vector2 nextSize = target.GetItemPixelSize(carry.Dimensions);
                if (size != nextSize)
                {
                    size = nextSize;
                    view.SetSize(size);
                }
                if (itemId != carry.Item.ItemId || count != carry.Count)
                {
                    itemId = carry.Item.ItemId;
                    count = carry.Count;
                    view.SetItem(carry.Item.GetFragment<ImageFragment>()?.Icon, count);
                }
                view.gameObject.SetActive(true);
                if (index >= 0) view.transform.position = target.GetCellWorldPosition(index);
                else if (RectTransformUtility.ScreenPointToLocalPointInRectangle(cursorLayer, point, GetUICamera(target), out Vector2 local))
                {
                    Vector2 grabCenter = target.GetGrabCenterOffset(carry.GrabOffset);
                    view.transform.position = cursorLayer.TransformPoint(local + new Vector2(-grabCenter.x, grabCenter.y));
                }
            }

            public void Hide()
            {
                if (view != null) view.gameObject.SetActive(false);
            }

            public void Dispose()
            {
                if (view != null) Destroy(view.gameObject);
                view = null;
            }
        }

        private static int nextRequestId;
        private BackpackContainerUI[] views = Array.Empty<BackpackContainerUI>();
        private readonly CarryPreview preview = new CarryPreview();
        private CarrySession carry;
        private PendingOperation pending;
        private BackpackContainerUI splitSource;
        private ItemId splitItemId;
        private int splitSourceCount;
        private int splitIndex;
        private IBackpackView splitInventory;
        private int splitOriginIndex;
        private GridCoord splitDimensions;
        private int splitColumns;
        private int splitRows;
        private SessionInput pendingInput;
        private int receivedInputFrame = -1;
        private BackpackContainerUI hoveredView;
        private ItemId hoveredItemId;
        private BackpackContainerUI highlightedView;
        private int highlightedIndex = -1;
        private GridCoord highlightedDimensions;
        private BackpackHoverState highlightedState;
        private string operationFeedback = string.Empty;
        private float feedbackExpiresAt;
        private string contextHint = string.Empty;
        private string cachedCarryHint;
        private DropAction cachedHintAction;
        private bool cachedHintPartial;
        public Vector2 PointerPosition { get; private set; }
        public bool HasPointer { get; private set; }

        public bool IsSessionActive => views.Length > 0;
        public int SessionVersion { get; private set; }
        public bool IsWaitingForOperation => pending != null;
        public bool IsCarrying => carry != null;
        public bool IsChoosingSplitQuantity => quantityDialog != null && quantityDialog.IsOpen;
        public bool HasMultipleContainers => views.Length > 1;
        public RuntimeItem DisplayedItem => carry != null ? carry.Item
            : hoveredView != null && hoveredView.TryGetItem(hoveredItemId, out RuntimeItem item) ? item : null;
        public string ContextHint => contextHint;
        public string OperationFeedback => pending != null ? "正在提交操作，请稍候…"
            : Time.unscaledTime < feedbackExpiresAt ? operationFeedback : string.Empty;

        /// <summary>为一个或多个容器开始交互；已有会话会先完整结束。容器可等待数据源就绪。</summary>
        public void BeginSession(params BackpackContainerUI[] containers)
        {
            EndSession();
            if (cursorLayer == null)
            {
                BackpackLog.Warn(LogModule.UI, "请在 BackpackInteractionController 中接线携带预览层。");
                return;
            }
            cursorLayer.SetAsLastSibling();
            views = containers ?? Array.Empty<BackpackContainerUI>();
            contextHint = string.Empty;
            SessionVersion++;
        }

        /// <summary>只接收活动会话的本帧输入；由较晚执行的 Update 在视图刷新后消费一次。</summary>
        public void ReceiveInput(SessionInput input)
        {
            if (!IsSessionActive || !isActiveAndEnabled) return;
            pendingInput = input;
            PointerPosition = input.PointerPosition;
            HasPointer = input.HasPointer;
            receivedInputFrame = Time.frameCount;
        }

        /// <summary>结束会话，释放操作结果订阅、临时显示覆盖、携带预览及容器引用。</summary>
        public void EndSession()
        {
            CancelInteraction();
            preview.Dispose();
            views = Array.Empty<BackpackContainerUI>();
            HasPointer = false;
            operationFeedback = contextHint = string.Empty;
            feedbackExpiresAt = 0f;
        }

        private void CancelInteraction()
        {
            if (quantityDialog != null) quantityDialog.Close();
            splitSource = null;
            splitInventory = null;
            splitItemId = ItemId.None;
            if (pending != null) pending.Subscription.Dispose();
            pending = null;
            pendingInput = default;
            receivedInputFrame = -1;
            ClearFeedback();
            preview.Hide();
            CarrySession previous = carry;
            carry = null;
            if (previous?.Source == null || !ReferenceEquals(previous.Source.Inventory, previous.Inventory)) return;
            previous.Source.ClearStackDisplay(previous.Item.ItemId);
            previous.Source.ClearHiddenItem();
            if (IsInventoryAlive(previous.Source.Inventory)) previous.Source.Redraw();
        }

        private void OnDisable() => EndSession();

        private void OnDestroy() => EndSession();

        private void Update()
        {
            SessionInput input = pendingInput;
            bool hasInput = receivedInputFrame == Time.frameCount;
            pendingInput = default;
            receivedInputFrame = -1;
            if (!IsSessionActive) return;
            if (!CanInteract())
            {
                CancelInteraction();
                contextHint = "等待库存就绪…";
                return;
            }
            if (hasInput && input.Cancel)
            {
                bool wasPending = pending != null;
                CancelInteraction();
                if (wasPending) SetOperationFeedback("已停止等待，库存以同步结果为准");
                contextHint = "已取消拿取";
                return;
            }
            if (quantityDialog != null && quantityDialog.IsOpen)
            {
                if (!IsSplitSelectionCurrent())
                {
                    CancelInteraction();
                    SetOperationFeedback("物品已变化，请重新选择拆分数量");
                }
                contextHint = "选择拆出数量 · 确认后选择落点 · Esc 取消";
                return;
            }
            if (quantityDialog != null && quantityDialog.LastInteractionFrame == Time.frameCount) return;
            if (pending != null) return;
            if (carry != null && (!ReferenceEquals(carry.Source.Inventory, carry.Inventory)
                || !carry.Source.TryGetItem(carry.Item.ItemId, out RuntimeItem current)
                || current.StackCount != carry.SourceCount
                || carry.Source.Columns != carry.Columns || carry.Source.Rows != carry.Rows
                || !carry.Inventory.TryGetPlacement(carry.Item.ItemId, out InventoryPlacement currentPlacement)
                || currentPlacement.UpperLeftIndex != carry.OriginIndex || currentPlacement.Dimensions != carry.Dimensions))
            {
                CancelInteraction();
                return;
            }
            if (!hasInput || !input.HasPointer)
            {
                ClearFeedback();
                preview.Hide();
                contextHint = carry != null ? "移动鼠标选择落点 · Esc 取消" : "将鼠标移到物品上查看详情";
                return;
            }

            Vector2 point = input.PointerPosition;
            BackpackContainerUI target = ResolveView(point, out TileParams tile);
            SetHoveredItem(target, tile.TileIndex);
            // 取消与右键先于放下，避免同一帧把取消解释成一次提交。
            if (input.SecondaryClick && input.ChooseSplitQuantity && carry == null) OpenQuantityDialog(target, tile.TileIndex);
            else if (input.SecondaryClick) HandleSecondaryClick(target, tile.TileIndex);
            else if (input.PrimaryClick)
            {
                if (carry == null && input.QuickTransfer && views.Length == 2) TryQuickTransfer(target, tile.TileIndex);
                else if (carry == null) BeginCarry(target, tile.TileIndex, false);
                else TryDrop(target, tile.TileIndex);
            }
            UpdateFeedback(target, tile.TileIndex, point);
            UpdateContextHint(target, tile.TileIndex);
        }

        // 成功结果只结束等待；位置和数量始终由 Changed 驱动的权威投影刷新。
        // 结果与同步任何先后顺序都不需要本地放置、猜测数量或回滚。
        private void LateUpdate()
        {
            if (pending == null) return;
            if (!IsViewReady(pending.Destination) || !ReferenceEquals(pending.Inventory, pending.Destination.Inventory)
                || carry == null || !ReferenceEquals(carry.Inventory, carry.Source.Inventory))
            {
                CancelInteraction();
                return;
            }
            if (pending.Result.HasValue)
            {
                InventoryOperationResult result = pending.Result.Value;
                if (!result.Success) BackpackLog.Warn(LogModule.Interaction, result.Error);
                CancelInteraction();
                SetOperationFeedback(result.Success ? "操作完成" : "操作未完成，请重试");
                return;
            }
            if (Time.unscaledTime < pending.Deadline) return;
            CancelInteraction();
            SetOperationFeedback("等待超时，库存以同步结果为准");
        }

        private bool CanInteract()
        {
            if (!IsSessionActive) return false;
            foreach (BackpackContainerUI view in views)
                if (!IsViewReady(view)) return false;
            return true;
        }

        private static bool IsInventoryAlive(IBackpackView inventory) =>
            inventory != null && !(inventory is UnityEngine.Object owner && owner == null);

        private static bool IsViewReady(BackpackContainerUI view) =>
            view != null && view.isActiveAndEnabled && IsInventoryAlive(view.Inventory) && view.IsReady;

        private BackpackContainerUI ResolveView(Vector2 point, out TileParams tile)
        {
            for (int i = views.Length - 1; i >= 0; i--)
                if (views[i].TryResolveTile(point, GetUICamera(views[i]), out tile)) return views[i];
            tile = TileParams.Empty;
            return null;
        }

        private static Camera GetUICamera(BackpackContainerUI view)
        {
            Canvas canvas = view.GetComponentInParent<Canvas>();
            return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        }

        private void BeginCarry(BackpackContainerUI source, int index, bool partial)
        {
            if (source == null || !source.TryGetCellOwner(index, out ItemId id, out _)
                || !source.TryGetItem(id, out RuntimeItem item) || !source.TryGetPlacement(id, out GridDisplayPlacement placement)) return;
            if (partial && (!item.IsStackable || item.StackCount <= 1)) return;
            GridCoord clicked = GridUtils.GetPositionFromIndex(index, source.Columns);
            carry = new CarrySession
            {
                Source = source,
                Inventory = source.Inventory,
                Item = item,
                SourceCount = item.StackCount,
                Count = partial ? 1 : item.StackCount,
                OriginIndex = placement.Position.y * source.Columns + placement.Position.x,
                Columns = source.Columns,
                Rows = source.Rows,
                GrabOffset = new Vector2Int(clicked.X - placement.Position.x, clicked.Y - placement.Position.y),
                Dimensions = new GridCoord(placement.Item.Width, placement.Item.Height)
            };
            UpdateSourceDisplay();
        }

        private void TryQuickTransfer(BackpackContainerUI source, int index)
        {
            if (views.Length != 2 || source == null) return;
            BackpackContainerUI destination = views[0] == source ? views[1] : views[0];
            if (destination == source || !IsViewReady(destination)
                || ReferenceEquals(source.Inventory, destination.Inventory)) return;
            BeginCarry(source, index, false);
            if (carry == null) return;
            Submit(InventoryOperationKind.TransferItem, destination, -1, ItemId.None);
        }

        private void OpenQuantityDialog(BackpackContainerUI source, int index)
        {
            if (quantityDialog == null)
            {
                SetOperationFeedback("拆分数量面板未配置");
                return;
            }
            if (source == null || !source.TryGetCellOwner(index, out ItemId id, out _)
                || !source.TryGetItem(id, out RuntimeItem item) || !item.IsStackable || item.StackCount <= 1
                || !source.Inventory.TryGetPlacement(id, out InventoryPlacement placement)) return;
            splitSource = source;
            splitItemId = id;
            splitSourceCount = item.StackCount;
            splitIndex = index;
            splitInventory = source.Inventory;
            splitOriginIndex = placement.UpperLeftIndex;
            splitDimensions = placement.Dimensions;
            splitColumns = source.Columns;
            splitRows = source.Rows;
            ClearFeedback();
            quantityDialog.Open(item.StackCount - 1, ConfirmSplitQuantity);
        }

        private bool IsSplitSelectionCurrent() => splitSource != null
            && ReferenceEquals(splitSource.Inventory, splitInventory)
            && splitSource.Columns == splitColumns && splitSource.Rows == splitRows
            && splitSource.TryGetItem(splitItemId, out RuntimeItem item) && item.StackCount == splitSourceCount
            && splitInventory.TryGetPlacement(splitItemId, out InventoryPlacement placement)
            && placement.UpperLeftIndex == splitOriginIndex && placement.Dimensions == splitDimensions
            && splitSource.TryGetCellOwner(splitIndex, out ItemId owner, out _) && owner == splitItemId;

        private void ConfirmSplitQuantity(int count)
        {
            if (!IsSplitSelectionCurrent())
            {
                splitSource = null;
                splitInventory = null;
                splitItemId = ItemId.None;
                SetOperationFeedback("物品已变化，请重新选择拆分数量");
                return;
            }
            BackpackContainerUI source = splitSource;
            int index = splitIndex;
            splitSource = null;
            splitInventory = null;
            splitItemId = ItemId.None;
            BeginCarry(source, index, true);
            if (carry == null) return;
            carry.Count = Mathf.Clamp(count, 1, carry.SourceCount - 1);
            UpdateSourceDisplay();
        }

        private void HandleSecondaryClick(BackpackContainerUI target, int index)
        {
            if (carry == null)
            {
                BeginCarry(target, index, true);
                return;
            }
            if (!carry.IsPartial)
            {
                CancelInteraction();
                return;
            }
            if (target != carry.Source || !target.TryGetCellOwner(index, out ItemId id, out _) || id != carry.Item.ItemId) return;
            if (carry.Count >= carry.SourceCount) return;
            carry.Count++;
            UpdateSourceDisplay();
        }

        private void UpdateSourceDisplay()
        {
            if (carry.IsPartial) carry.Source.SetStackDisplay(carry.Item.ItemId, carry.SourceCount - carry.Count);
            else
            {
                carry.Source.ClearStackDisplay(carry.Item.ItemId);
                carry.Source.SetHiddenItem(carry.Item.ItemId);
            }
            carry.Source.Redraw();
        }

        private DropAction ResolveDrop(BackpackContainerUI target, int hoverIndex, out int index, out SpaceQueryResult<RuntimeItem> region)
        {
            index = -1;
            region = SpaceQueryResult<RuntimeItem>.Empty;
            if (carry == null || target == null) return DropAction.Blocked;
            if (carry.IsPartial && target == carry.Source && target.TryGetCellOwner(hoverIndex, out ItemId owner, out _) && owner == carry.Item.ItemId)
                return DropAction.Return;
            index = target.GetUpperLeftIndex(hoverIndex, carry.GrabOffset);
            if (index < 0) return DropAction.Blocked;
            region = target.QuerySpace(index, carry.Dimensions);
            bool sameInventory = ReferenceEquals(target.Inventory, carry.Inventory);
            if (region.HasSpace || (!carry.IsPartial && sameInventory && region.ValidItem?.ItemId == carry.Item.ItemId))
            {
                if (!carry.IsPartial && sameInventory) return DropAction.Place;
                if (!(carry.Inventory is IInventoryOperations source) || !source.CanSubmitTo(target.Inventory)) return DropAction.Blocked;
                return carry.IsPartial ? DropAction.Split : DropAction.Transfer;
            }
            // 合并和交换也由库存事务校验，跨库存手动落点使用命中的确切物品。
            if (region.ValidItem == null || !(carry.Inventory is IInventoryOperations writer)
                || !writer.CanSubmitTo(target.Inventory)) return DropAction.Blocked;
            RuntimeItem other = region.ValidItem;
            if (!carry.Item.IsStackable || !other.IsStackable || !carry.Item.ItemType.MatchesTagExact(other.ItemType))
                return carry.IsPartial ? DropAction.Blocked : DropAction.SwapPosition;
            int room = other.MaxStackSize - other.StackCount;
            if (room > 0) return DropAction.Merge;
            if (carry.IsPartial) return DropAction.Blocked;
            return carry.SourceCount < carry.Item.MaxStackSize ? DropAction.SwapStackCounts : DropAction.SwapPosition;
        }

        private void TryDrop(BackpackContainerUI target, int hoverIndex)
        {
            DropAction action = ResolveDrop(target, hoverIndex, out int index, out SpaceQueryResult<RuntimeItem> region);
            switch (action)
            {
                case DropAction.Return:
                    CancelInteraction();
                    return;
                case DropAction.Place:
                    Submit(InventoryOperationKind.MoveItem, target, index, ItemId.None);
                    return;
                case DropAction.SwapPosition:
                    Submit(InventoryOperationKind.SwapItems, target, index, region.ValidItem.ItemId);
                    return;
                case DropAction.Merge:
                    Submit(InventoryOperationKind.MergeStack, target, index, region.ValidItem.ItemId);
                    return;
                case DropAction.SwapStackCounts:
                    Submit(InventoryOperationKind.SwapStackCounts, target, index, region.ValidItem.ItemId);
                    return;
                case DropAction.Split:
                    Submit(InventoryOperationKind.SplitStack, target, index, ItemId.None);
                    return;
                case DropAction.Transfer:
                    Submit(InventoryOperationKind.TransferItem, target, index, ItemId.None);
                    return;
            }
            SetOperationFeedback("此处无法放置，请选择可用位置");
        }

        private void Submit(InventoryOperationKind kind, BackpackContainerUI target, int index, ItemId other)
        {
            if (!(carry.Inventory is IInventoryOperations operations) || !operations.CanSubmitTo(target.Inventory))
            {
                SetOperationFeedback("当前无法操作该库存");
                return;
            }
            int requestId = unchecked(++nextRequestId);
            if (requestId == 0) requestId = unchecked(++nextRequestId);
            pending = new PendingOperation
            {
                RequestId = requestId,
                Destination = target,
                Inventory = target.Inventory,
                Deadline = Time.unscaledTime + OperationTimeout
            };
            pending.Subscription = EventBus.Global.Subscribe<InventoryOperationResult>(
                InvChannels.OperationCompleted, carry.Inventory.EventSource, this, OnOperationCompleted);
            preview.Hide();
            carry.Source.ClearStackDisplay(carry.Item.ItemId);
            carry.Source.ClearHiddenItem();
            carry.Source.Redraw();
            ClearFeedback();
            EventBus.Global.Publish(CmdChannels.InventoryOperation, carry.Inventory.EventSource,
                new InventoryOperation(requestId, kind, carry.Item.ItemId, other, carry.Count, target.Inventory, upperLeftIndex: index));
        }

        private void OnOperationCompleted(InventoryOperationResult result)
        {
            if (pending != null && pending.RequestId == result.RequestId) pending.Result = result;
        }

        private void SetOperationFeedback(string message)
        {
            operationFeedback = message;
            feedbackExpiresAt = Time.unscaledTime + 4f;
        }

        private void UpdateContextHint(BackpackContainerUI target, int index)
        {
            if (quantityDialog != null && quantityDialog.IsOpen) { contextHint = "选择拆出数量 · 确认后选择落点 · Esc 取消"; return; }
            if (pending != null) { contextHint = "等待库存确认…"; return; }
            if (carry != null)
            {
                DropAction action = ResolveDrop(target, index, out _, out _);
                bool partial = carry.IsPartial;
                if (cachedCarryHint == null || cachedHintAction != action || cachedHintPartial != partial)
                {
                    cachedHintAction = action;
                    cachedHintPartial = partial;
                    string cancel = partial ? " · 右键原堆增加数量 · Esc 取消" : " · 右键 / Esc 取消";
                    cachedCarryHint = (action == DropAction.Blocked ? "无法放置" : action == DropAction.Merge ? "左键合并堆叠"
                        : action == DropAction.SwapPosition || action == DropAction.SwapStackCounts ? "左键交换"
                        : action == DropAction.Return ? "左键放回原堆" : action == DropAction.Transfer ? "左键转移"
                        : action == DropAction.Split ? "左键拆出并放置" : "左键放置") + cancel;
                }
                contextHint = cachedCarryHint;
                return;
            }
            RuntimeItem item = DisplayedItem;
            if (item == null) { contextHint = "将鼠标移到物品上查看详情"; return; }
            bool canSplit = item.IsStackable && item.StackCount > 1;
            contextHint = views.Length == 2
                ? canSplit ? "左键拿取 · 右键逐个拆分 · Shift + 右键选择数量 · Ctrl + 左键自动转移" : "左键拿取 · Ctrl + 左键自动转移"
                : canSplit ? "左键拿取 · 右键逐个拆分 · Shift + 右键选择数量" : "左键拿取";
        }

        private void SetHoveredItem(BackpackContainerUI target, int index)
        {
            ItemId id = ItemId.None;
            if (target != null) target.TryGetCellOwner(index, out id, out _);
            if (hoveredView == target && hoveredItemId == id) return;
            if (hoveredView != null) hoveredView.SetItemHovered(hoveredItemId, false);
            hoveredView = target;
            hoveredItemId = id;
            if (target != null) target.SetItemHovered(id, true);
        }

        private void UpdateFeedback(BackpackContainerUI target, int hoverIndex, Vector2 point)
        {
            if (carry == null || pending != null) return;
            DropAction action = ResolveDrop(target, hoverIndex, out int index, out SpaceQueryResult<RuntimeItem> region);
            bool swap = action == DropAction.Merge || action == DropAction.SwapPosition || action == DropAction.SwapStackCounts;
            BackpackHoverState state = action == DropAction.Blocked ? BackpackHoverState.Blocked
                : swap ? BackpackHoverState.Swap : BackpackHoverState.Available;
            GridCoord dimensions = carry.Dimensions;
            if (swap && target.TryGetPlacement(region.ValidItem.ItemId, out GridDisplayPlacement placement))
                dimensions = new GridCoord(placement.Item.Width, placement.Item.Height);
            int highlightIndex = swap ? region.UpperLeftIndex : index;
            if (highlightedView != target || highlightedIndex != highlightIndex || highlightedState != state || highlightedDimensions != dimensions)
            {
                if (highlightedView != null) highlightedView.ClearHoverHighlight();
                highlightedView = target;
                highlightedIndex = highlightIndex;
                highlightedDimensions = dimensions;
                highlightedState = state;
                if (target != null && highlightIndex >= 0) target.ApplyRegionHighlight(highlightIndex, dimensions, state);
            }
            preview.Show(target ?? carry.Source, carry, point, action == DropAction.Blocked ? -1 : index, cursorLayer);
        }

        private void ClearFeedback()
        {
            if (hoveredView != null) hoveredView.SetItemHovered(hoveredItemId, false);
            if (highlightedView != null) highlightedView.ClearHoverHighlight();
            hoveredView = highlightedView = null;
            hoveredItemId = ItemId.None;
            highlightedIndex = -1;
            highlightedState = BackpackHoverState.None;
        }
    }
}
