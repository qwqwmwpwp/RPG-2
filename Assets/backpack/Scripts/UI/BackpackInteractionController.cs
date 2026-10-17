using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using RPG4.Input;

namespace RPG4.Backpack.UI
{
    /// <summary>交互控制层（M6 + 右键拆分）：每帧按鼠标屏幕坐标预计算网格参数与悬停反馈（视图不参与 uGUI 射线，悬停与落点共用同一几何口径），点击时只读本帧缓存；单栏位置变化只改本地位置表，堆叠 / 拆分提交服务端意图并等待复制结果；双栏空位转移与拆分直接调用权威宿主 / 容器原子提交。右键拆分只在本机记「已取数量」与源物品显示数量（库存数据不动），左键落下时才提交，取消即原样还原。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)] // 晚于主控，保证读到的是同一帧已刷新完成的排布结果。
    public sealed class BackpackInteractionController : MonoBehaviour
    {
        private const float SplitClaimTimeout = 0.5f; // 拆分结果认领超时（秒）：超时未回传即放弃认领并记警告。

        private const float DisplayHoldTimeout = 1f; // 显示覆盖保留上限（秒）：提交类操作超时未回传就还原真实数量。

        [Header("接线")]
        [SerializeField] private BackpackGridUI grid; // 网格主控（几何 / 渲染 / 本地位置表）。

        /// <summary>网格主控引用（供外部把开关请求推给窗口）。</summary>
        public BackpackGridUI Grid => grid;

        private InputAction cancelAction; // 取消动作（backpack/cancel，ESC）；由输入宿主提供，宿主缺失时为 null。
        private InputAction pointerAction; // 指针位置动作（backpack/pointerPosition）；由输入宿主提供，宿主缺失时为 null。

        private IBackpackView inventory; // 数据来源视图（由主控在绑定本地玩家库存时注入）。
        private object eventSource; // 事件管线键：订阅 Inv.Changed 与发布 Cmd.* 意图共用。
        private EventSubscription dataSubscription; // 数据变更（Inv.Changed）订阅句柄。

        // 双栏模式使用同一个携带会话；来源固定，目标栏由指针决定。
        private bool containerMode;
        private BackpackContainerUI firstView;
        private BackpackContainerUI secondView;
        private BackpackContainerUI carryView;
        private InventoryContainer carryContainer;
        private int carrySourceStack; // 双栏拿起时的源数量快照；部分携带数量与它独立。
        private BackpackContainerUI previewView;
        private BackpackContainerUI hoveredView;

        private TileParams tile = TileParams.Empty; // 本帧指针网格参数。
        private SpaceQueryResult<RuntimeItem> space = SpaceQueryResult<RuntimeItem>.Empty; // 本帧落点的空间查询结果。
        private GridCoord carriedDimensions = GridCoord.One; // 携带物占位尺寸（空格时为 1×1）。
        private int targetIndex = SpaceQueryResult<RuntimeItem>.NoIndex; // 本帧落点（上左）线性索引。
        private BackpackHoverState hoverState = BackpackHoverState.None; // 本帧悬停反馈态。
        private int highlightedStart = SpaceQueryResult<RuntimeItem>.NoIndex; // 当前高亮区域的起始索引。
        private GridCoord highlightedDimensions = GridCoord.One; // 当前高亮区域的占位尺寸。

        private RuntimeItem carried; // 携带中的物品（左键拾起或右键拆出后跟随光标）。
        private int carriedCount; // 携带数量：整件携带 = 物品堆叠数；右键拆出的部分携带 = 已取数量。
        private bool carriedPartial; // 是否为右键拆出的虚拟部分携带（库存数据未改动，只有携带数量与显示是本机状态）。
        private Vector2Int carriedOrigin; // 携带物的原落点（取消携带时复位用）。
        private bool carriedHasOrigin; // 是否记录了原落点。
        private Vector2Int carriedGrabOffset; // 抓取格偏移（被点击格 − 物品上左格，单位=格）；携带期间固定，落点与预览都以该格贴住光标。
        private ItemId previewItemId = ItemId.None; // 携带预览物当前展示的物品标识。
        private int previewStack; // 携带预览物当前展示的数量（累加取物时数量变化也要刷新预览）。

        private ItemId hoveredItemId = ItemId.None; // 本帧悬停的物品。
        private ItemId highlightedItemId = ItemId.None; // 当前被高亮的物品框。
        private bool sessionValid; // 本帧会话是否有效（用于退出时一次性复位）。
        private int pendingPrimaryClicks; // 待处理左键点击（背包主控 PushClick 入队）。
        private int pendingSecondaryClicks; // 待处理右键点击（背包主控 PushClick 入队）。
        private bool lastInteractive; // 上一次的交互可用性；状态变化时打一条诊断日志，用于定位「点击无反应」。
        private bool interactiveLogged; // 是否已打过首次诊断日志（首帧无条件输出一次，避免「恒为未激活」时没有任何日志）。

        private ItemId pendingSplitSourceId = ItemId.None; // 待认领拆分的源物品标识（None 表示没有待认领）。
        private int pendingSplitCount; // 待认领拆分的数量。
        private BackpackTag pendingSplitType; // 待认领拆分的物品类型（比对新实例用）。
        private Vector2Int pendingSplitTargetPosition; // 拆分结果的期望落点（列、行）。
        private float pendingSplitDeadline; // 认领超时时刻；超时即放弃（物品仍在服务端落点）。
        private readonly List<ItemId> pendingSplitKnownIds = new List<ItemId>(); // 拆分前的已知标识集合（认领时做比对）。
        private readonly List<ItemId> currentItemIds = new List<ItemId>(); // 当前标识集合（复用缓冲）。
        private float sourceDisplayHoldDeadline; // 源物品显示覆盖的保留截止时刻：提交类操作等数据回传，超时或取消即还原真值。



        /// <summary>绑定数据来源视图并订阅 Inv.Changed；由主控在绑定本地玩家库存后调用。</summary>
        public void Bind(IBackpackView source)
        {
            if (!containerMode && ReferenceEquals(inventory, source)) return;
            Unbind();
            inventory = source;
            eventSource = source?.EventSource;
            if (eventSource != null)
            {
                dataSubscription = EventBus.Global.Subscribe(InvChannels.Changed, eventSource, this, OnInventoryChanged);
            }

            BackpackLog.Info(LogModule.Interaction, source == null ? "交互层已绑定空数据源。" : "交互层已绑定数据源。");
        }

        /// <summary>解绑数据来源、释放订阅并复位会话。</summary>
        public void Unbind()
        {
            dataSubscription.Dispose();
            dataSubscription = default;
            inventory = null;
            eventSource = null;
            ResetSession();
            containerMode = false;
            firstView = null;
            secondView = null;
        }

        /// <summary>绑定双栏转移与拆分模式；切换鼠标所在栏不会重新绑定或清除携带来源。</summary>
        public void BindContainers(BackpackContainerUI left, BackpackContainerUI right)
        {
            if (containerMode && ReferenceEquals(firstView, left) && ReferenceEquals(secondView, right)) return;
            Unbind();
            containerMode = true;
            firstView = left;
            secondView = right;
        }

        /// <summary>窗口关闭或失去输入焦点时取消尚未提交的操作。</summary>
        public void CancelInteraction() => ResetSession();

        private void OnDisable() => ResetSession();

        /// <summary>销毁时解绑。</summary>
        public void OnDestroy() => Unbind();

        /// <summary>背包主控转发入口：把左 / 右键点击排入本帧队列（取值见 <see cref="UiChannels.MouseButton"/>），由 Update 统一按网格几何分派。</summary>
        public void PushClick(int mouseButton)
        {
            BackpackLog.Debug(LogModule.Interaction, mouseButton == UiChannels.MouseButton.Left ? "点击入队：左键。" : "点击入队：右键。");
            if (mouseButton == UiChannels.MouseButton.Left) pendingPrimaryClicks++;
            else if (mouseButton == UiChannels.MouseButton.Right) pendingSecondaryClicks++;
        }

        /// <summary>库存变化时先认领拆分结果，再检查携带物是否还存在；携带中的物品已不在库存里就结束携带。</summary>
        private void OnInventoryChanged()
        {
            TryClaimSplitResult();
            if (carried != null && !grid.TryGetItem(carried.ItemId, out _))
            {
                BackpackLog.Warn(LogModule.Interaction, "携带中的物品已不在库存中，已放回。");
                EndCarry();
            }
        }

        /// <summary>每帧入口：先处理认领与显示覆盖的到期兜底，再依次预计算坐标、悬停反馈、携带预览、描述面板与输入分派；会话不可交互或指针被面板拦截时提前返回。</summary>
        private void Update()
        {
            if (containerMode)
            {
                UpdateContainerSession();
                return;
            }

            UpdatePendingExpiry();
            ResolveInputActions();
            bool interactive = IsInteractive();
            if (!interactiveLogged || interactive != lastInteractive)
            {
                interactiveLogged = true;
                lastInteractive = interactive;
                Canvas canvas = grid != null ? grid.GetComponentInParent<Canvas>() : null;
                BackpackLog.Info(
                    LogModule.Interaction,
                    $"交互会话{(interactive ? "已激活" : "未激活")}：数据源={(inventory != null)}，网格={(grid != null)}，网格可用={(grid != null && grid.isActiveAndEnabled)}，窗口打开={(grid != null && grid.IsOpen)}，画布模式={(canvas != null ? canvas.renderMode.ToString() : "无")}");
            }

            if (!interactive)
            {
                ResetSession();
                return;
            }

            sessionValid = true;
            if (!TryGetPointer(out Vector2 screenPoint))
            {
                if (pendingPrimaryClicks > 0 || pendingSecondaryClicks > 0) BackpackLog.Warn(LogModule.Interaction, "未检测到鼠标设备，点击被丢弃。");
                ResetPointerState(default, hasPoint: false);
                return;
            }

            UpdatePointerCache(screenPoint);
            UpdateHoveredItem();
            UpdateHoverHighlight();
            UpdateCarriedPreview(screenPoint);
            HandleInput(screenPoint);
        }

        /// <summary>双栏共用携带会话：右键逐个取出，左键整件转移或拆分到空位，不合并或互换。</summary>
        private void UpdateContainerSession()
        {
            ResolveInputActions();
            if (!IsContainerViewReady(firstView) || !IsContainerViewReady(secondView))
            {
                ResetSession();
                return;
            }

            if (carried != null && (carryView == null
                || !ReferenceEquals(carryView.Container, carryContainer)
                || !carryContainer.Contains(carried) || carried.StackCount != carrySourceStack))
            {
                ResetSession();
                return;
            }

            // 取消优先于同帧点击，避免关闭/取消意图仍提交库存转移。
            if ((cancelAction != null && cancelAction.WasPressedThisFrame())
                || (pendingSecondaryClicks > 0 && carried != null && !carriedPartial))
            {
                ResetSession();
                return;
            }

            if (!TryGetPointer(out Vector2 screenPoint))
            {
                ResetSession();
                return;
            }

            BackpackContainerUI target = ResolveContainerView(screenPoint, out TileParams targetTile);
            ClearContainerHover();
            if (target != null && target.TryGetCellOwner(targetTile.TileIndex, out ItemId owner, out _))
            {
                hoveredView = target;
                hoveredItemId = owner;
                target.SetItemHovered(owner, true);
            }

            bool secondaryHandled = pendingSecondaryClicks > 0;
            if (secondaryHandled)
            {
                pendingSecondaryClicks--;
                pendingPrimaryClicks = 0; // 同帧右键优先，不把旧左键延后变成一次落下。
                if (carried == null)
                {
                    if (target != null) BeginContainerCarry(target, targetTile.TileIndex, partial: true);
                }
                else if (carriedPartial && ReferenceEquals(target, carryView) && hoveredItemId == carried.ItemId)
                {
                    carriedCount++;
                    if (carriedCount == carrySourceStack)
                    {
                        carriedPartial = false;
                        carryView.ClearStackDisplay(carried.ItemId);
                        carryView.SetHiddenItem(carried.ItemId);
                    }
                    else carryView.SetStackDisplay(carried.ItemId, carrySourceStack - carriedCount);
                    carryView.Redraw();
                }
            }

            int index = target != null
                ? target.GetUpperLeftIndex(targetTile.TileIndex, carriedGrabOffset)
                : SpaceQueryResult<RuntimeItem>.NoIndex;
            SpaceQueryResult<RuntimeItem> region = target != null && index >= 0
                ? target.QuerySpace(index, carried != null ? carriedDimensions : GridCoord.One)
                : SpaceQueryResult<RuntimeItem>.Empty;
            bool canPlace = carried != null && target != null && index >= 0
                && (region.HasSpace || (!carriedPartial && ReferenceEquals(target, carryView) && ReferenceEquals(region.ValidItem, carried)))
                && ((!carriedPartial && ReferenceEquals(target.Container, carryContainer)) || HasEmptyRegion(target.Container, carriedDimensions));
            bool canReturn = carriedPartial && ReferenceEquals(target, carryView) && hoveredItemId == carried.ItemId;

            if (carried != null)
            {
                if (target != null && index >= 0)
                    target.ApplyRegionHighlight(index, carriedDimensions, canPlace || canReturn ? BackpackHoverState.Available : BackpackHoverState.Blocked);
                ShowContainerPreview(target ?? carryView, screenPoint, canPlace || canReturn ? index : SpaceQueryResult<RuntimeItem>.NoIndex);
            }

            if (secondaryHandled) return;
            if (pendingPrimaryClicks <= 0) return;
            pendingPrimaryClicks--; // 一帧只执行一次点击；放下前使用当前帧重新计算的区域。
            if (target == null) return;
            if (carried == null)
            {
                BeginContainerCarry(target, targetTile.TileIndex);
                return;
            }

            if (canReturn)
            {
                ResetSession();
                return;
            }

            if (!canPlace) return;
            GridCoord position = GridUtils.GetPositionFromIndex(index, target.Columns);
            RuntimeItem item = carried;
            ItemId placedId = item.ItemId;
            if (carriedPartial)
            {
                if (!TrySplitContainerCarry(target, out placedId, out string splitError))
                {
                    target.ApplyRegionHighlight(index, carriedDimensions, BackpackHoverState.Blocked);
                    BackpackLog.Debug(LogModule.Interaction, "拆分被拒绝：" + splitError);
                    return;
                }
            }
            else if (!ReferenceEquals(target.Container, carryContainer)
                && !carryContainer.TryTransferItemToEmptySpace(item, target.Container, out string error))
            {
                target.ApplyRegionHighlight(index, carriedDimensions, BackpackHoverState.Blocked);
                BackpackLog.Debug(LogModule.Interaction, "跨背包转移被拒绝：" + error);
                return;
            }

            target.SetManualPosition(placedId, new Vector2Int(position.X, position.Y));
            ResetSession();
            target.Redraw();
        }

        private bool TrySplitContainerCarry(BackpackContainerUI destination, out ItemId newItemId, out string error)
        {
            CommitConfirmHandler destinationConfirm = null;
            if (!ReferenceEquals(destination.Container, carryContainer) && destination.Inventory is BackpackNetworkInventory targetNetwork)
                destinationConfirm = targetNetwork.ConfirmSplitProjection;
            if (carryView.Inventory is BackpackInventory local)
                return local.TrySplitStackToEmptySpace(carried, carriedCount, destination.Container, out newItemId, out error, destinationConfirm);
            if (carryView.Inventory is BackpackNetworkInventory network)
                return network.TrySplitStackToEmptySpace(carried, carriedCount, destination.Container, out newItemId, out error, destinationConfirm);
            newItemId = ItemId.None;
            error = "来源不支持权威拆分。";
            return false;
        }

        private static bool IsContainerViewReady(BackpackContainerUI view) =>
            view != null && view.isActiveAndEnabled && IsInventoryAlive(view.Inventory) && view.Container != null && view.IsReady;

        private static bool IsInventoryAlive(IBackpackView source) =>
            source != null && !(source is UnityEngine.Object owner && owner == null);

        // 本地显示位置可以与库存位置不同；高亮还须确认真容器能容纳完整矩形。
        private static bool HasEmptyRegion(InventoryContainer container, GridCoord dimensions)
        {
            for (int i = 0; i < container.CellCount; i++)
                if (container.QuerySpace(i, dimensions).HasSpace) return true;
            return false;
        }

        private BackpackContainerUI ResolveContainerView(Vector2 point, out TileParams resolved)
        {
            if (secondView.TryResolveTile(point, GetUICamera(secondView), out resolved)) return secondView;
            if (firstView.TryResolveTile(point, GetUICamera(firstView), out resolved)) return firstView;
            resolved = TileParams.Empty;
            return null;
        }

        private static Camera GetUICamera(BackpackContainerUI view)
        {
            Canvas canvas = view != null ? view.GetComponentInParent<Canvas>() : null;
            return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        }

        private void BeginContainerCarry(BackpackContainerUI source, int clickedIndex, bool partial = false)
        {
            if (!source.TryGetCellOwner(clickedIndex, out ItemId id, out _)
                || !source.TryGetItem(id, out RuntimeItem item)
                || !source.Container.Contains(item)
                || !source.TryGetPlacement(id, out GridDisplayPlacement placement)) return;

            if (partial && (!item.IsStackable || item.StackCount <= 1)) return;
            carryView = source;
            carryContainer = source.Container;
            carrySourceStack = item.StackCount;
            carried = item;
            carriedCount = partial ? 1 : item.StackCount;
            carriedPartial = partial;
            carriedDimensions = InventoryContainer.GetItemDimensions(item);
            GridCoord clicked = GridUtils.GetPositionFromIndex(clickedIndex, source.Columns);
            carriedGrabOffset = new Vector2Int(clicked.X - placement.Position.x, clicked.Y - placement.Position.y);
            if (partial) source.SetStackDisplay(id, item.StackCount - carriedCount);
            else source.SetHiddenItem(id);
            source.Redraw();
        }

        private void ShowContainerPreview(BackpackContainerUI view, Vector2 screenPoint, int index)
        {
            if (!ReferenceEquals(previewView, view))
            {
                if (previewView != null) previewView.HideCarriedPreview();
                previewView = view;
            }

            if (view == null || !view.TryShowCarriedPreview(carried, carriedCount)) return;
            if (index >= 0)
            {
                GridCoord position = GridUtils.GetPositionFromIndex(index, view.Columns);
                view.SetCarriedPreviewCellPosition(new Vector2(position.X, position.Y));
            }
            else view.TrySetCarriedPreviewScreenPosition(screenPoint, GetUICamera(view), carriedGrabOffset);
        }

        private void ClearContainerHover()
        {
            if (hoveredView != null) hoveredView.SetItemHovered(hoveredItemId, false);
            hoveredView = null;
            hoveredItemId = ItemId.None;
            if (firstView != null) firstView.ClearHoverHighlight();
            if (secondView != null) secondView.ClearHoverHighlight();
        }

        private void ResetContainerSession()
        {
            ClearContainerHover();
            if (previewView != null) previewView.HideCarriedPreview();
            if (carryView != null)
            {
                if (carried != null) carryView.ClearStackDisplay(carried.ItemId);
                carryView.ClearHiddenItem();
                if (IsInventoryAlive(carryView.Inventory)) carryView.Redraw();
                else carryView.UnbindInventory();
            }

            carried = null;
            carriedCount = 0;
            carriedPartial = false;
            carrySourceStack = 0;
            carriedDimensions = GridCoord.One;
            carriedGrabOffset = Vector2Int.zero;
            carryView = null;
            carryContainer = null;
            previewView = null;
            pendingPrimaryClicks = 0;
            pendingSecondaryClicks = 0;
        }

        // 每帧预计算

        /// <summary>换算本帧落点索引并缓存空间查询结果（分支只读这里的结果，不自行重算）。</summary>
        private void UpdatePointerCache(Vector2 screenPoint)
        {
            grid.TryResolveTile(screenPoint, uiCamera: null, out tile);
            carriedDimensions = carried != null ? InventoryContainer.GetItemDimensions(carried) : GridCoord.One;
            targetIndex = SpaceQueryResult<RuntimeItem>.NoIndex;
            space = SpaceQueryResult<RuntimeItem>.Empty;
            if (!tile.IsValid) return;

            targetIndex = grid.GetUpperLeftIndex(tile.TileIndex, carriedGrabOffset);
            if (targetIndex < 0) return;

            space = grid.QuerySpace(targetIndex, carriedDimensions);
        }

        /// <summary>按指针所在格更新悬停物品并切换其高亮。</summary>
        private void UpdateHoveredItem()
        {
            ItemId next = ItemId.None;
            if (tile.IsValid && grid.TryGetCellOwner(tile.TileIndex, out ItemId owner, out _)) next = owner;

            if (next != hoveredItemId) hoveredItemId = next;

            if (highlightedItemId == hoveredItemId) return;
            grid.SetItemHovered(highlightedItemId, false);
            highlightedItemId = hoveredItemId;
            grid.SetItemHovered(highlightedItemId, true);
        }

        /// <summary>把本帧落点区域标成对应悬停反馈态；交换 / 堆叠分支的锚点与尺寸取自挡路物品。无悬停物时全部清零，只保留格子本身的底色。</summary>
        private void UpdateHoverHighlight()
        {
            BackpackHoverState next = BackpackHoverState.None;
            if (carried != null)
            {
                if (!tile.IsValid || targetIndex < 0)
                {
                    // 落点在网格外或推导越界：无区域可着色，只保留「不可放置」语义。
                    next = BackpackHoverState.Blocked;
                }
                else if (space.HasSpace || ReferenceEquals(space.ValidItem, carried))
                {
                    next = BackpackHoverState.Available;
                }
                else
                {
                    next = space.CanSwap ? BackpackHoverState.Swap : BackpackHoverState.Blocked;
                }
            }

            // 交换 / 堆叠分支的锚点与尺寸取自「挡路物品」，而不是携带物的落点区域。
            int start = SpaceQueryResult<RuntimeItem>.NoIndex;
            GridCoord dimensions = carriedDimensions;
            if (carried != null && next != BackpackHoverState.None)
            {
                if (next == BackpackHoverState.Swap && space.ValidItem != null)
                {
                    start = space.UpperLeftIndex;
                    dimensions = InventoryContainer.GetItemDimensions(space.ValidItem);
                }
                else
                {
                    start = targetIndex;
                }
            }

            if (hoverState == next && highlightedStart == start && highlightedDimensions == dimensions) return;
            hoverState = next;
            highlightedStart = start;
            highlightedDimensions = dimensions;
            grid.ClearHoverHighlight();
            if (next != BackpackHoverState.None && start >= 0) grid.ApplyRegionHighlight(start, dimensions, next);
        }

        /// <summary>显示并定位携带预览物（命中网格时贴格，否则自由跟随指针）。</summary>
        private void UpdateCarriedPreview(Vector2 screenPoint)
        {
            if (carried == null)
            {
                grid.HideCarriedPreview();
                return;
            }

            if (previewItemId != carried.ItemId || previewStack != carriedCount)
            {
                if (!grid.TryShowCarriedPreview(carried, carriedCount))
                {
                    previewItemId = ItemId.None;
                    previewStack = 0;
                    return;
                }

                previewItemId = carried.ItemId;
                previewStack = carriedCount;
            }

            if (tile.IsValid && targetIndex >= 0)
            {
                GridCoord origin = GridUtils.GetPositionFromIndex(targetIndex, grid.Columns);
                grid.SetCarriedPreviewCellPosition(new Vector2(origin.X, origin.Y));
                return;
            }

            grid.TrySetCarriedPreviewScreenPosition(screenPoint, uiCamera: null, carriedGrabOffset);
        }

        // 输入分派

        /// <summary>解析输入宿主（InputManager）提供的取消与指针位置两个动作；宿主缺失或已销毁时清空引用，下次调用自动重试。</summary>
        private void ResolveInputActions()
        {
            InputManager host = InputManager.Instance;
            if (host == null)
            {
                cancelAction = null;
                pointerAction = null;
                return;
            }

            InputActions.BackpackActions actions = host.backpackInput;
            cancelAction = actions.cancel;
            pointerAction = actions.pointerPosition;
        }

        /// <summary>按键入队（左 / 右键由背包主控转发）分派，ESC 与鼠标坐标都取自输入宿主的 backpack 动作。</summary>
        private void HandleInput(Vector2 screenPoint)
        {
            if (pendingPrimaryClicks > 0)
            {
                pendingPrimaryClicks--;
                HandlePrimaryClick();
                return;
            }

            if (pendingSecondaryClicks > 0)
            {
                pendingSecondaryClicks--;
                HandleSecondaryClick();
                return;
            }

            if (cancelAction != null && cancelAction.WasPressedThisFrame()) HandleCancel();
        }

        /// <summary>左键分派：拾起悬停物品，或按携带形态放下（虚拟部分携带走拆分合并分支，整件携带走原分支）。</summary>
        private void HandlePrimaryClick()
        {
            // M7 装备槽优先于网格：装备栏接入后在此处先判「装备 / 卸下」，再接下面的网格分支。

            if (carried == null)
            {
                if (hoveredItemId.IsValid) BeginCarry(hoveredItemId);
                else BackpackLog.Debug(LogModule.Interaction, $"左键：没有悬停物品（落点在网格内={tile.IsValid}，格索引={tile.TileIndex}），忽略。");
                return;
            }

            if (carriedPartial)
            {
                TryPlacePartialCarry();
                return;
            }

            TryPlaceCarried();
        }

        /// <summary>右键分派：未携带时对可堆叠且多于 1 个的悬停物品开始部分携带；部分携带中只有「指针仍在源物品上」才再取 1 个（其它位置无操作，取消走 ESC）；整件携带保持原有「右键取消」。</summary>
        private void HandleSecondaryClick()
        {
            if (carried != null)
            {
                if (!carriedPartial)
                {
                    CancelCarry("右键取消携带。");
                    return;
                }

                TakeOneMore();
                return;
            }

            if (!hoveredItemId.IsValid || !grid.TryGetItem(hoveredItemId, out RuntimeItem item) || item == null)
            {
                BackpackLog.Debug(LogModule.Interaction, "右键：没有可拆分的悬停物品，忽略。");
                return;
            }

            if (!item.IsStackable || item.StackCount <= 1)
            {
                BackpackLog.Debug(
                    LogModule.Interaction,
                    () => $"右键：{item.ItemType} 不可拆分（可堆叠={item.IsStackable}，数量={item.StackCount}），忽略。");
                return;
            }

            BeginPartialCarry(item);
        }

        /// <summary>ESC 分派：带走携带物时取消携带（部分携带的已取数量还原为原堆叠，零数据写入）。</summary>
        private void HandleCancel()
        {
            if (carried != null) CancelCarry("取消携带。");
        }

        // 携带态

        /// <summary>抓取格偏移 = 本帧点击格 − 物品上左格（单位=格）；点击格非法或落在物品占格之外时回退 (0,0)，退回旧的中心格口径。</summary>
        private Vector2Int GetGrabOffset(Vector2Int upperLeft)
        {
            if (!tile.IsValid || grid == null || grid.Columns < 1) return Vector2Int.zero;
            GridCoord clicked = GridUtils.GetPositionFromIndex(tile.TileIndex, grid.Columns);
            Vector2Int offset = new Vector2Int(clicked.X - upperLeft.x, clicked.Y - upperLeft.y);
            if (offset.x < 0 || offset.y < 0) return Vector2Int.zero;
            return offset;
        }

        /// <summary>进入携带态：物品只在本地从网格隐藏，不改动库存数据。</summary>
        private void BeginCarry(ItemId id)
        {
            if (!grid.TryGetItem(id, out RuntimeItem item) || item == null) return;
            if (!grid.TryGetPlacement(id, out GridDisplayPlacement placement))
            {
                Reject("目标物品当前没有排布位置。");
                return;
            }

            carried = item;
            carriedCount = item.StackCount;
            carriedPartial = false;
            carriedOrigin = placement.Position;
            carriedHasOrigin = true;
            carriedDimensions = InventoryContainer.GetItemDimensions(item);
            carriedGrabOffset = GetGrabOffset(placement.Position);
            previewItemId = ItemId.None;
            previewStack = 0;
            sourceDisplayHoldDeadline = 0f;
            grid.ClearAllStackDisplays(); // 清掉上一轮右键拆分可能残留的显示覆盖。
            grid.SetHiddenItem(id);
            grid.Redraw();
            BackpackLog.Debug(
                LogModule.Interaction,
                () => $"拾起 {item.ItemType} ×{item.StackCount}（本地携带，未改动库存数据）。");
        }

        /// <summary>记录原因并结束携带。</summary>
        private void CancelCarry(string reason)
        {
            BackpackLog.Debug(LogModule.Interaction, reason);
            EndCarry();
        }

        /// <summary>结束携带并复位隐藏、预览与高亮状态；提交类路径已置等待期限，显示覆盖保留到数据回传（由主控自愈或超时还原），取消类路径立即还原。</summary>
        private void EndCarry()
        {
            carried = null;
            carriedCount = 0;
            carriedPartial = false;
            carriedHasOrigin = false;
            carriedDimensions = GridCoord.One;
            carriedGrabOffset = Vector2Int.zero;
            previewItemId = ItemId.None;
            previewStack = 0;
            if (sourceDisplayHoldDeadline <= 0f) grid.ClearAllStackDisplays();
            grid.ClearHiddenItem();
            grid.HideCarriedPreview();
            hoverState = BackpackHoverState.None;
            highlightedStart = SpaceQueryResult<RuntimeItem>.NoIndex;
            highlightedDimensions = GridCoord.One;
            grid.ClearHoverHighlight();
            grid.Redraw();
        }

        /// <summary>把携带物放到本帧落点：全空则放置，被单个物品占用则走堆叠 / 交换。</summary>
        private void TryPlaceCarried()
        {
            if (carried == null) return;
            if (!tile.IsValid || targetIndex < 0)
            {
                // 落点不在网格内 / 推导越界：保持携带，等下一次点击。
                return;
            }

            if (space.HasSpace || ReferenceEquals(space.ValidItem, carried))
            {
                PlaceAt(targetIndex);
                return;
            }

            if (!space.CanSwap || space.ValidItem == null) return;
            ResolveStackOrSwap(space.ValidItem, space.UpperLeftIndex);
        }

        /// <summary>落到位全空的区域：只写本地位置表，不产生网络请求。</summary>
        private void PlaceAt(int index)
        {
            ItemId id = carried.ItemId;
            grid.SetManualPosition(id, new Vector2Int(index % grid.Columns, index / grid.Columns));
            BackpackLog.Debug(
                LogModule.Interaction,
                () => $"放置 {id} 到本地格 {index}（位置不同步）。");
            EndCarry();
        }

        /// <summary>落点被单个物品占用：同类且双方可堆叠走堆叠分支，否则交换位置。</summary>
        private void ResolveStackOrSwap(RuntimeItem target, int targetUpperLeftIndex)
        {
            bool sameType = target.ItemType.MatchesTagExact(carried.ItemType);
            if (!sameType || !target.IsStackable || !carried.IsStackable)
            {
                SwapPositions(target, targetUpperLeftIndex);
                return;
            }

            int room = target.MaxStackSize - target.StackCount;
            if (room <= 0)
            {
                // 目标已满：来源未满则互换数量，双方都满则退化为交换位置。
                if (carried.StackCount < carried.MaxStackSize) SwapStackCounts(target);
                else SwapPositions(target, targetUpperLeftIndex);
                return;
            }

            if (room >= carried.StackCount) MergeInto(target);
            else PartialFill(target, room);
        }

        /// <summary>目标已满且来源未满：交换双方堆叠数（提交服务端复核）。</summary>
        private void SwapStackCounts(RuntimeItem target)
        {
            int carriedCount = carried.StackCount;
            int targetCount = target.StackCount;
            if (inventory == null) return;

            PublishSetStackCount(target.ItemId, carriedCount);
            PublishSetStackCount(carried.ItemId, targetCount);
            BackpackLog.Info(
                LogModule.Interaction,
                () => $"交换堆叠数：{target.ItemType} {targetCount} ↔ {carriedCount}（提交服务端复核）。");
            EndCarry();
        }

        /// <summary>全部并入目标：抬升目标堆叠数并移除携带物（提交服务端复核）。</summary>
        private void MergeInto(RuntimeItem target)
        {
            ItemId consumedId = carried.ItemId;
            int total = target.StackCount + carried.StackCount;
            if (inventory == null) return;

            PublishSetStackCount(target.ItemId, Mathf.Min(total, target.MaxStackSize));
            grid.ClearManualPosition(consumedId);
            PublishRemoveItem(consumedId);
            BackpackLog.Info(
                LogModule.Interaction,
                () => $"全部并入：{target.ItemType} {target.StackCount} → {total}，移除 {consumedId}（提交服务端复核）。");
            EndCarry();
        }

        /// <summary>部分填充：把 room 数量并入目标，剩余留在原堆叠（提交服务端复核）。</summary>
        private void PartialFill(RuntimeItem target, int room)
        {
            int remaining = carried.StackCount - room;
            ItemId carriedId = carried.ItemId;
            if (inventory == null) return;

            PublishSetStackCount(target.ItemId, target.StackCount + room);
            PublishSetStackCount(carriedId, remaining);
            BackpackLog.Info(
                LogModule.Interaction,
                () => $"部分填充：{target.ItemType} 补入 {room}，剩余 {remaining} 留在原堆叠（提交服务端复核）。");
            EndCarry();
        }

        /// <summary>异类型交换位置：纯本地位置表互换，不产生网络请求。</summary>
        private void SwapPositions(RuntimeItem target, int targetUpperLeftIndex)
        {
            ItemId carriedId = carried.ItemId;
            int columns = grid.Columns;
            grid.SetManualPosition(carriedId, new Vector2Int(targetUpperLeftIndex % columns, targetUpperLeftIndex / columns));
            if (carriedHasOrigin) grid.SetManualPosition(target.ItemId, carriedOrigin);
            BackpackLog.Info(
                LogModule.Interaction,
                () => $"交换位置：{carriedId} ↔ {target.ItemType}（位置不同步，仅本机生效）。");
            EndCarry();
        }

        // 右键拆分（部分携带：只改本机携带数量与显示，库存数据在落下时才提交）

        /// <summary>记录一次被本机拒绝的操作（只记日志，不改状态）。</summary>
        private static void Reject(string reason) => BackpackLog.Warn(LogModule.Interaction, reason);

        /// <summary>开始部分携带：从源堆叠取出 1 个跟随光标；源物品留在原格，只把显示数量覆盖为「总数 − 已取」。</summary>
        private void BeginPartialCarry(RuntimeItem item)
        {
            if (!grid.TryGetPlacement(item.ItemId, out GridDisplayPlacement placement))
            {
                Reject("目标物品当前没有排布位置。");
                return;
            }

            carried = item;
            carriedCount = 1;
            carriedPartial = true;
            carriedOrigin = placement.Position;
            carriedHasOrigin = true;
            carriedDimensions = InventoryContainer.GetItemDimensions(item);
            carriedGrabOffset = GetGrabOffset(placement.Position);
            previewItemId = ItemId.None;
            previewStack = 0;
            sourceDisplayHoldDeadline = 0f;
            grid.ClearAllStackDisplays();
            grid.SetStackDisplay(item.ItemId, item.StackCount - carriedCount);
            grid.Redraw();
            BackpackLog.Debug(
                LogModule.Interaction,
                () => $"右键拆出 1 个：{item.ItemType} 已取 {carriedCount}/{item.StackCount}（本地携带，未改动库存数据）。");
        }

        /// <summary>对当前源堆叠再取 1 个；指针离开源物品则什么都不做（取消请按 ESC）。取满整堆后源物品隐藏，携带口径退化为整件携带。</summary>
        private void TakeOneMore()
        {
            if (carried == null || !carriedPartial) return;
            if (!tile.IsValid || hoveredItemId != carried.ItemId)
            {
                BackpackLog.Debug(LogModule.Interaction, "右键：指针不在当前源物品上，忽略（取消携带请按 ESC）。");
                return;
            }

            if (carriedCount >= carried.StackCount)
            {
                BackpackLog.Debug(LogModule.Interaction, "右键：源堆叠已全部取完，忽略。");
                return;
            }

            carriedCount++;
            if (carriedCount >= carried.StackCount)
            {
                // 已全部取出：源物品隐藏，落点分派改走整件携带口径（数据仍未改动）。
                grid.ClearStackDisplay(carried.ItemId);
                grid.SetHiddenItem(carried.ItemId);
                carriedPartial = false;
            }
            else
            {
                grid.SetStackDisplay(carried.ItemId, carried.StackCount - carriedCount);
            }

            grid.Redraw();
            BackpackLog.Debug(
                LogModule.Interaction,
                () => $"右键再取 1 个：{carried.ItemType} 已取 {carriedCount}/{carried.StackCount}。");
        }

        /// <summary>部分携带的落地分派：落点全空提交一次拆分并把结果认领到点击格；落点是源物品自己视为放回；落点为同类可堆叠则合并 / 部分填充；其余情况这 N 个退回源堆叠。</summary>
        private void TryPlacePartialCarry()
        {
            if (carried == null || !carriedPartial) return;
            if (!tile.IsValid || targetIndex < 0) return; // 落点不在网格内 / 推导越界：保持携带，等下一次点击。

            if (space.HasSpace)
            {
                DropPartialToEmptyCell(targetIndex);
                return;
            }

            if (ReferenceEquals(space.ValidItem, carried))
            {
                CancelCarry("部分携带落回源物品：已退回原堆叠。");
                return;
            }

            if (!space.CanSwap || space.ValidItem == null)
            {
                CancelCarry("部分携带落点被多个物品挡路：已退回原堆叠。");
                return;
            }

            RuntimeItem target = space.ValidItem;
            bool sameType = target.ItemType.MatchesTagExact(carried.ItemType);
            if (!sameType || !target.IsStackable || !carried.IsStackable)
            {
                CancelCarry($"部分携带落点是 {target.ItemType}（非同类可堆叠）：已退回原堆叠。");
                return;
            }

            int room = target.MaxStackSize - target.StackCount;
            if (room <= 0)
            {
                CancelCarry($"{target.ItemType} 已满：已退回原堆叠。");
                return;
            }

            MergePartialInto(target, Mathf.Min(room, carriedCount));
        }

        /// <summary>把携带的 moved 个并入同类目标、源堆叠相应减少（提交服务端复核）；未取出的那一部分仍留在源堆叠。</summary>
        private void MergePartialInto(RuntimeItem target, int moved)
        {
            if (inventory == null || eventSource == null)
            {
                CancelCarry("数据源未就绪：已退回原堆叠。");
                return;
            }

            ItemId sourceId = carried.ItemId;
            int sourceRemaining = carried.StackCount - moved;
            BackpackTag type = target.ItemType;
            PublishSetStackCount(target.ItemId, target.StackCount + moved);
            if (sourceRemaining > 0)
            {
                PublishSetStackCount(sourceId, sourceRemaining);
                grid.SetStackDisplay(sourceId, sourceRemaining); // 覆盖改为「源落定后的数量」，数据回传后由主控自愈丢弃。
            }
            else
            {
                PublishRemoveItem(sourceId);
            }

            HoldSourceDisplayUntilDataArrives();
            BackpackLog.Info(
                LogModule.Interaction,
                () => $"部分合并：{type} +{moved}，源堆叠剩 {sourceRemaining}（提交服务端复核）。");
            EndCarry();
        }

        /// <summary>部分携带落到空位：提交一次拆分让服务端生成新实例，并登记认领信息（广播回传后把新实例写进玩家点击的那一格）。</summary>
        private void DropPartialToEmptyCell(int index)
        {
            if (inventory == null || eventSource == null)
            {
                CancelCarry("数据源未就绪：已退回原堆叠。");
                return;
            }

            int columns = Mathf.Max(1, grid.Columns);
            BackpackTag type = carried.ItemType;
            int count = carriedCount;
            Vector2Int position = new Vector2Int(index % columns, index / columns);

            pendingSplitSourceId = carried.ItemId;
            pendingSplitCount = count;
            pendingSplitType = type;
            pendingSplitTargetPosition = position;
            pendingSplitDeadline = Time.unscaledTime + SplitClaimTimeout;
            grid.CopyItemIds(pendingSplitKnownIds);
            PublishSplitStack(pendingSplitSourceId, count);
            HoldSourceDisplayUntilDataArrives();
            BackpackLog.Info(
                LogModule.Interaction,
                () => $"落空位：请求拆分 {type} ×{count}，等待回传后认领到本机格 {position}。");
            EndCarry();
        }

        /// <summary>提交类操作后保留源物品的显示覆盖：等数据回传由主控自愈丢弃，超时未回传则由 <see cref="UpdatePendingExpiry"/> 还原真值。</summary>
        private void HoldSourceDisplayUntilDataArrives()
        {
            sourceDisplayHoldDeadline = Time.unscaledTime + DisplayHoldTimeout;
        }

        /// <summary>认领拆分结果：找出回传后新增的同类同数量实例，写进玩家点击的那一格（位置不上网）；没有新实例返回 false 继续等。</summary>
        private bool TryClaimSplitResult()
        {
            if (!pendingSplitSourceId.IsValid || grid == null) return false;

            currentItemIds.Clear();
            grid.CopyItemIds(currentItemIds);
            ItemId claimed = ItemId.None;
            for (int i = 0; i < currentItemIds.Count; i++)
            {
                ItemId id = currentItemIds[i];
                if (pendingSplitKnownIds.Contains(id)) continue;
                if (!grid.TryGetItem(id, out RuntimeItem item) || item == null) continue;
                if (item.StackCount != pendingSplitCount) continue;
                if (!item.ItemType.MatchesTagExact(pendingSplitType)) continue;
                claimed = id;
                break;
            }

            if (!claimed.IsValid) return false;

            Vector2Int position = pendingSplitTargetPosition;
            grid.SetManualPosition(claimed, position);
            grid.Redraw();
            ClearPendingSplit();
            BackpackLog.Info(LogModule.Interaction, () => $"已认领拆分结果并落到本机格 {position}（位置不同步）。");
            return true;
        }

        /// <summary>清除待认领拆分状态。</summary>
        private void ClearPendingSplit()
        {
            pendingSplitSourceId = ItemId.None;
            pendingSplitCount = 0;
            pendingSplitType = default;
            pendingSplitTargetPosition = Vector2Int.zero;
            pendingSplitDeadline = 0f;
            pendingSplitKnownIds.Clear();
        }

        /// <summary>每帧兜底：主控快照在帧末才刷新，故认领需跨帧重试；认领超时（拆分被拒或回传丢失）即放弃并记警告；显示覆盖超时即还原库存真值。</summary>
        private void UpdatePendingExpiry()
        {
            if (pendingSplitSourceId.IsValid)
            {
                if (!TryClaimSplitResult() && Time.unscaledTime >= pendingSplitDeadline)
                {
                    BackpackLog.Warn(LogModule.Interaction, "拆分结果未在超时内回传，已放弃认领（物品仍在服务端算出的落点）。");
                    ClearPendingSplit();
                }
            }

            if (sourceDisplayHoldDeadline > 0f && Time.unscaledTime >= sourceDisplayHoldDeadline)
            {
                sourceDisplayHoldDeadline = 0f;
                if (grid != null && grid.HasStackDisplayOverride)
                {
                    grid.ClearAllStackDisplays();
                    grid.Redraw();
                    BackpackLog.Warn(LogModule.Interaction, "提交后未收到数据回传，已把显示数量还原为库存真值。");
                }
            }
        }

        // 意图命令发布（source = 绑定数据源的事件管线键；命令不保证成功，结果由 InvChannels 广播回传）

        /// <summary>发布「设置堆叠数」意图。</summary>
        private void PublishSetStackCount(ItemId itemId, int stackCount)
        {
            if (eventSource == null) return;
            EventBus.Global.Publish(CmdChannels.SetStackCount, eventSource, itemId, stackCount);
        }

        /// <summary>发布「移除物品」意图。</summary>
        private void PublishRemoveItem(ItemId itemId)
        {
            if (eventSource == null) return;
            EventBus.Global.Publish(CmdChannels.RemoveItem, eventSource, itemId);
        }

        /// <summary>发布「拆分堆叠」意图。</summary>
        private void PublishSplitStack(ItemId itemId, int splitCount)
        {
            if (eventSource == null) return;
            EventBus.Global.Publish(CmdChannels.SplitStack, eventSource, itemId, splitCount);
        }

        // 状态复位

        /// <summary>数据源、网格是否就绪，且背包窗口处于打开状态。</summary>
        private bool IsInteractive()
        {
            return !ChestBackpackUI.BlocksPlayerBackpackInput && inventory != null && grid != null && grid.isActiveAndEnabled && grid.IsOpen;
        }

        /// <summary>退出交互会话：一次性复位全部指针、携带与高亮状态，并丢弃排队中的点击。</summary>
        private void ResetSession()
        {
            if (containerMode)
            {
                ResetContainerSession();
                return;
            }

            bool hadState = sessionValid;
            sessionValid = false;
            pendingPrimaryClicks = 0;
            pendingSecondaryClicks = 0;
            if (!hadState) return;

            carried = null;
            carriedCount = 0;
            carriedPartial = false;
            carriedHasOrigin = false;
            carriedDimensions = GridCoord.One;
            carriedGrabOffset = Vector2Int.zero;
            previewItemId = ItemId.None;
            previewStack = 0;
            hoveredItemId = ItemId.None;
            hoverState = BackpackHoverState.None;
            highlightedStart = SpaceQueryResult<RuntimeItem>.NoIndex;
            targetIndex = SpaceQueryResult<RuntimeItem>.NoIndex;
            space = SpaceQueryResult<RuntimeItem>.Empty;
            tile = TileParams.Empty;
            sourceDisplayHoldDeadline = 0f;
            ClearPendingSplit();

            if (grid != null)
            {
                grid.SetItemHovered(highlightedItemId, false);
                highlightedItemId = ItemId.None;
                grid.ClearAllStackDisplays();
                grid.ClearHiddenItem();
                grid.HideCarriedPreview();
                grid.ClearHoverHighlight();
            }
        }

        /// <summary>指针不可用时复位指针相关缓存。</summary>
        private void ResetPointerState(Vector2 screenPoint, bool hasPoint)
        {
            tile = TileParams.Empty;
            space = SpaceQueryResult<RuntimeItem>.Empty;
            targetIndex = SpaceQueryResult<RuntimeItem>.NoIndex;
            pendingPrimaryClicks = 0;
            pendingSecondaryClicks = 0;

            if (hoveredItemId.IsValid) hoveredItemId = ItemId.None;

            if (highlightedItemId.IsValid)
            {
                grid.SetItemHovered(highlightedItemId, false);
                highlightedItemId = ItemId.None;
            }

            if (hoverState != BackpackHoverState.None)
            {
                hoverState = BackpackHoverState.None;
                highlightedStart = SpaceQueryResult<RuntimeItem>.NoIndex;
                highlightedDimensions = GridCoord.One;
                grid.ClearHoverHighlight();
            }

            // 携带物继续跟随真实指针，避免指针离开网格就跳到网格角落。
            if (carried != null && hasPoint)
            {
                // 指针离开网格时落点不可用，携带预览退回自由跟随：仍以被点击的那一格对准光标。
                grid.TrySetCarriedPreviewScreenPosition(screenPoint, uiCamera: null, carriedGrabOffset);
            }
        }

        /// <summary>读取当前指针屏幕坐标（输入宿主的 pointerPosition 动作）；宿主缺失或无指针设备时返回 false。</summary>
        private bool TryGetPointer(out Vector2 screenPoint)
        {
            if (pointerAction == null || !InputManager.HasPointer)
            {
                screenPoint = default;
                return false;
            }

            screenPoint = pointerAction.ReadValue<Vector2>();
            return true;
        }
    }
}
