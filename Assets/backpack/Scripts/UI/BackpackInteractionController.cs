using System;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.UI
{
    /// <summary>单双栏共用的物品交互会话：由宿主开始、传入输入并结束。位置留在本机，库存变化以完整操作提交。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public sealed class BackpackInteractionController : MonoBehaviour
    {
        private const float OperationTimeout = 5f;
        [SerializeField] private RectTransform cursorLayer;

        public RectTransform CursorLayer => cursorLayer;

        /// <summary>宿主在 Update 中传入的本帧输入；交互组件不直接读取输入系统。</summary>
        public readonly struct SessionInput
        {
            public Vector2 PointerPosition { get; }
            public bool HasPointer { get; }
            public bool PrimaryClick { get; }
            public bool SecondaryClick { get; }
            public bool Cancel { get; }

            public SessionInput(Vector2 pointerPosition, bool hasPointer, bool primaryClick, bool secondaryClick, bool cancel)
            {
                PointerPosition = pointerPosition;
                HasPointer = hasPointer;
                PrimaryClick = primaryClick;
                SecondaryClick = secondaryClick;
                Cancel = cancel;
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
            public Vector2Int Origin;
            public Vector2Int GrabOffset;
            public GridCoord Dimensions;
            public bool IsPartial => Count < SourceCount;
        }

        private sealed class PendingOperation
        {
            public int RequestId;
            public BackpackContainerUI Destination;
            public IBackpackView Inventory;
            public Vector2Int Position;
            public bool PlaceResult;
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
        private SessionInput pendingInput;
        private int receivedInputFrame = -1;
        private BackpackContainerUI hoveredView;
        private ItemId hoveredItemId;
        private BackpackContainerUI highlightedView;
        private int highlightedIndex = -1;
        private GridCoord highlightedDimensions;
        private BackpackHoverState highlightedState;

        public bool IsSessionActive => views.Length > 0;
        public int SessionVersion { get; private set; }

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
            SessionVersion++;
        }

        /// <summary>只接收活动会话的本帧输入；由较晚执行的 Update 在视图刷新后消费一次。</summary>
        public void ReceiveInput(SessionInput input)
        {
            if (!IsSessionActive || !isActiveAndEnabled) return;
            pendingInput = input;
            receivedInputFrame = Time.frameCount;
        }

        /// <summary>结束会话，释放操作结果订阅、临时显示覆盖、携带预览及容器引用。</summary>
        public void EndSession()
        {
            CancelInteraction();
            preview.Dispose();
            views = Array.Empty<BackpackContainerUI>();
        }

        private void CancelInteraction()
        {
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
                return;
            }
            if (hasInput && input.Cancel)
            {
                CancelInteraction();
                return;
            }
            if (pending != null) return;
            if (carry != null && (!ReferenceEquals(carry.Source.Inventory, carry.Inventory)
                || !carry.Source.TryGetItem(carry.Item.ItemId, out RuntimeItem current)
                || current.StackCount != carry.SourceCount))
            {
                CancelInteraction();
                return;
            }
            if (!hasInput || !input.HasPointer)
            {
                ClearFeedback();
                preview.Hide();
                return;
            }

            Vector2 point = input.PointerPosition;
            BackpackContainerUI target = ResolveView(point, out TileParams tile);
            SetHoveredItem(target, tile.TileIndex);
            // 取消与右键先于放下，避免同一帧把取消解释成一次提交。
            if (input.SecondaryClick) HandleSecondaryClick(target, tile.TileIndex);
            else if (input.PrimaryClick)
            {
                if (carry == null) BeginCarry(target, tile.TileIndex, false);
                else TryDrop(target, tile.TileIndex);
            }
            UpdateFeedback(target, tile.TileIndex, point);
        }

        // 视图先刷新数据，再用返回的确切 ID 放置结果；不依赖网络通知的到达顺序。
        private void LateUpdate()
        {
            if (pending == null) return;
            if (!IsViewReady(pending.Destination) || !ReferenceEquals(pending.Inventory, pending.Destination.Inventory)
                || !ReferenceEquals(carry.Inventory, carry.Source.Inventory))
            {
                CancelInteraction();
                return;
            }
            if (pending.Result.HasValue)
            {
                InventoryOperationResult result = pending.Result.Value;
                if (!result.Success)
                {
                    BackpackLog.Warn(LogModule.Interaction, result.Error);
                    CancelInteraction();
                    return;
                }
                bool sourceVisible = carry.Source.TryGetItem(carry.Item.ItemId, out RuntimeItem source);
                bool sourceSynced = result.SourceStackCount == 0 ? !sourceVisible
                    : sourceVisible && source.StackCount == result.SourceStackCount;
                bool resultSynced = pending.Destination.TryGetItem(result.ResultItemId, out RuntimeItem resultItem)
                    && resultItem.StackCount == result.ResultStackCount;
                if (sourceSynced && resultSynced)
                {
                    BackpackContainerUI destination = pending.Destination;
                    if (pending.PlaceResult) destination.SetManualPosition(result.ResultItemId, pending.Position);
                    bool refreshDestination = destination != carry.Source;
                    CancelInteraction();
                    if (refreshDestination) destination.Redraw();
                    return;
                }
            }
            if (Time.unscaledTime < pending.Deadline) return;
            BackpackLog.Warn(LogModule.Interaction, "库存操作结果等待超时，显示已恢复。库存以实际返回的数据为准。");
            CancelInteraction();
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

        private static GridCoord GetDimensions(RuntimeItem item)
        {
            GridFragment fragment = item.GetFragment<GridFragment>();
            return new GridCoord(Mathf.Max(1, fragment?.Width ?? 1), Mathf.Max(1, fragment?.Height ?? 1));
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
                Origin = placement.Position,
                GrabOffset = new Vector2Int(clicked.X - placement.Position.x, clicked.Y - placement.Position.y),
                Dimensions = GetDimensions(item)
            };
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
                if (!(carry.Inventory is IInventoryOperations source) || !source.CanSubmitTo(target.Inventory)
                    || !(target.Inventory is IInventoryOperations destination) || !destination.HasEmptySpace(carry.Dimensions)) return DropAction.Blocked;
                return carry.IsPartial ? DropAction.Split : DropAction.Transfer;
            }
            // 双栏保留空位转移规则；合并和交换只用于单栏。
            if (views.Length != 1 || !sameInventory || region.ValidItem == null) return DropAction.Blocked;
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
                    target.SetManualPosition(carry.Item.ItemId, new Vector2Int(index % target.Columns, index / target.Columns));
                    CancelInteraction();
                    target.Redraw();
                    return;
                case DropAction.SwapPosition:
                    target.SetManualPosition(carry.Item.ItemId, new Vector2Int(region.UpperLeftIndex % target.Columns, region.UpperLeftIndex / target.Columns));
                    target.SetManualPosition(region.ValidItem.ItemId, carry.Origin);
                    CancelInteraction();
                    return;
                case DropAction.Merge:
                    Submit(InventoryOperationKind.MergeStack, target, index, region.ValidItem.ItemId, false);
                    return;
                case DropAction.SwapStackCounts:
                    Submit(InventoryOperationKind.SwapStackCounts, target, index, region.ValidItem.ItemId, false);
                    return;
                case DropAction.Split:
                    Submit(InventoryOperationKind.SplitStack, target, index, ItemId.None, true);
                    return;
                case DropAction.Transfer:
                    Submit(InventoryOperationKind.TransferItem, target, index, ItemId.None, true);
                    return;
            }
        }

        private void Submit(InventoryOperationKind kind, BackpackContainerUI target, int index, ItemId other, bool placeResult)
        {
            if (!(carry.Inventory is IInventoryOperations operations) || !operations.CanSubmitTo(target.Inventory)) return;
            int requestId = unchecked(++nextRequestId);
            if (requestId == 0) requestId = unchecked(++nextRequestId);
            pending = new PendingOperation
            {
                RequestId = requestId,
                Destination = target,
                Inventory = target.Inventory,
                Position = new Vector2Int(index % target.Columns, index / target.Columns),
                PlaceResult = placeResult,
                Deadline = Time.unscaledTime + OperationTimeout
            };
            pending.Subscription = EventBus.Global.Subscribe<InventoryOperationResult>(
                InvChannels.OperationCompleted, carry.Inventory.EventSource, this, OnOperationCompleted);
            preview.Hide();
            ClearFeedback();
            EventBus.Global.Publish(CmdChannels.InventoryOperation, carry.Inventory.EventSource,
                new InventoryOperation(requestId, kind, carry.Item.ItemId, other, carry.Count, target.Inventory));
        }

        private void OnOperationCompleted(InventoryOperationResult result)
        {
            if (pending != null && pending.RequestId == result.RequestId) pending.Result = result;
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
            GridCoord dimensions = swap ? GetDimensions(region.ValidItem) : carry.Dimensions;
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
