using System;
using RPG4.Backpack.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RPG4.LoadoutTemplate
{
    /// <summary>只维护本次交互状态。输入、表现反馈与控制器操作全部通过事件管线传递。</summary>
    [DisallowMultipleComponent]
    public sealed class LoadoutDragController : MonoBehaviour
    {
        [SerializeField] private string ownerKey = "local-player";
        [SerializeField] private RectTransform dragLayer;
        [SerializeField, Min(0.1f)] private float requestTimeout = 8f;
        private readonly object providerSource = new object();
        private LoadoutWindowSession session;
        private EventSubscription connectSubscription;
        private EventSubscription inputSubscription, targetSubscription, resultSubscription, closeSubscription;
        private EventSubscription sourceChangedSubscription, targetChangedSubscription;
        private ContainerDisplaySnapshot source, target;
        private ItemDisplaySnapshot item, targetItem;
        private bool rotated;
        private ItemDisplaySnapshot MovingItem => item?.WithRotation(rotated);
        private Vector2Int targetOrigin;
        private Vector2 position;
        private Camera pointerCamera;
        private int viewId, targetViewId, requestSequence, pendingRequestId;
        private long interactionSequence, interactionId;
        private bool dragging, waiting, canDrop;
        private float deadline;

        public bool IsDragging => dragging;
        public bool IsWaiting => waiting;
        internal bool HasConfiguredLayer => dragLayer != null;
        public string LockedItemId => item?.Id;
        public Vector2Int SourceOrigin => item?.Origin ?? Vector2Int.zero;
        public Vector2Int TargetOrigin => targetOrigin;
        public string Feedback { get; private set; } = string.Empty;

        internal void SetNetworkOwner(string value)
        {
            CancelDrag();
            ownerKey = value;
        }

        private void OnEnable()
        {
            connectSubscription = EventBus.Global.Subscribe<LoadoutDragConnectRequest>(
                LoadoutChannels.DragConnectRequested, EventBus.AnySource, this, OnConnectRequested);
        }

        private void AttachSession(LoadoutWindowSession value)
        {
            DetachSession();
            session = value;
            EventBus bus = EventBus.Global;
            inputSubscription = bus.Subscribe<LoadoutInteractionInput>(LoadoutChannels.Input, session, this, OnInput);
            targetSubscription = bus.Subscribe<LoadoutTargetReport>(LoadoutChannels.Target, session, this, OnTarget);
            resultSubscription = bus.Subscribe<LoadoutOperationResult>(LoadoutChannels.OperationCompleted, session, this, OnResult);
            closeSubscription = bus.Subscribe(LoadoutChannels.SessionClosed, session, this, CancelDrag);
        }

        private void OnDisable()
        {
            CancelDrag();
            connectSubscription.Dispose();
            DetachSession();
        }

        private void DetachSession()
        {
            inputSubscription.Dispose(); targetSubscription.Dispose();
            resultSubscription.Dispose(); closeSubscription.Dispose();
            session = null;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) CancelDrag();
        }

        private void Update()
        {
            if (!dragging && !waiting) return;
            if (session == null || !session.IsActive)
            {
                Finish(false, "背包窗口会话已关闭。");
                return;
            }
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Finish(false, "操作已取消。");
                return;
            }
            if (dragging && Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            {
                rotated = !rotated;
                canDrop = false;
                PublishVisual(LoadoutVisualKind.Move);
            }
            if (waiting && Time.unscaledTime >= deadline) Finish(false, "放置请求等待超时。");
        }

        private void OnInput(LoadoutInteractionInput input)
        {
            if (!ReferenceEquals(input.Session, session)) return;
            if (input.ViewId != viewId || input.InteractionId != interactionId || item == null) return;
            if (input.Kind == LoadoutInputKind.Cancel)
            {
                Finish(false, "物品显示已失效。");
                return;
            }
            if (waiting || !dragging) return;
            position = input.Position;
            pointerCamera = input.Camera;
            canDrop = false;
            // 同步反馈让视图先完成移动和落点探测，再决定是否提交。
            PublishVisual(LoadoutVisualKind.Move);
            if (input.Kind == LoadoutInputKind.End && dragging) Submit();
        }

        private void OnConnectRequested(LoadoutDragConnectRequest request)
        {
            if (request.Session == null || !string.Equals(request.Session.OwnerKey, ownerKey, StringComparison.Ordinal)
                || request.ReplySource == null) return;
            LoadoutInteractionInput input = request.Input;
            if (!isActiveAndEnabled || !request.Session.IsActive || !ReferenceEquals(input.Session, request.Session)
                || input.Kind != LoadoutInputKind.Begin || input.ViewId == 0)
            { ReplyConnection(request, false, "交互会话已失效。"); return; }
            if (dragging || waiting)
            { ReplyConnection(request, false, "交换器正在处理另一件物品。"); return; }
            if (input.Source == null || input.Item == null || !input.Source.IsReady
                || input.Source.EventSource == null)
            { ReplyConnection(request, false, "来源物品尚未就绪。"); return; }
            ContainerDisplaySnapshot currentSource = ReadSnapshot(input.Source.EventSource);
            ItemDisplaySnapshot current = currentSource == null ? null : FindItem(currentSource, input.Item.Id);
            if (currentSource == null || !currentSource.IsReady || current == null
                || !ReferenceEquals(currentSource.EventSource, input.Source.EventSource)
                || currentSource.Id != input.Source.Id || currentSource.Revision != input.Source.Revision
                || current.Origin != input.Item.Origin || current.Size != input.Item.Size)
            { ReplyConnection(request, false, "来源物品或位置已变化，请重新操作。"); return; }
            AttachSession(request.Session);
            source = currentSource;
            item = current;
            rotated = current.Rotated;
            viewId = input.ViewId;
            interactionId = ++interactionSequence;
            position = input.Position;
            pointerCamera = input.Camera;
            dragging = true;
            canDrop = false;
            Feedback = string.Empty;
            sourceChangedSubscription = EventBus.Global.Subscribe(LoadoutChannels.Changed,
                source.EventSource, this, OnEndpointChanged);
            long acceptedId = interactionId;
            ReplyConnection(request, true);
            // 连接结果可能同步触发视图取消，确认锁仍属于本次请求后再移动图形。
            if (dragging && interactionId == acceptedId && ReferenceEquals(session, request.Session)
                && session.IsActive) PublishVisual(LoadoutVisualKind.Begin);
        }

        private void ReplyConnection(LoadoutDragConnectRequest request, bool success, string error = null) =>
            EventBus.Global.Publish(LoadoutChannels.DragConnected, request.ReplySource,
                new LoadoutDragConnectReply(request, success ? interactionId : 0, success, error,
                    providerSource: providerSource));

        private ContainerDisplaySnapshot ReadSnapshot(object endpoint)
        {
            ContainerDisplaySnapshot result = null;
            var replySource = new object();
            EventSubscription reply = EventBus.Global.Subscribe<LoadoutSnapshotReply>(
                LoadoutChannels.SnapshotReceived, replySource, this, value =>
                {
                    if (value.RequestId == 1) result = value.Snapshot;
                });
            try
            {
                EventBus.Global.Publish(LoadoutChannels.SnapshotRequested, endpoint,
                    new LoadoutSnapshotRequest(replySource, 1));
            }
            finally { reply.Dispose(); }
            return result;
        }

        private void OnTarget(LoadoutTargetReport report)
        {
            if (!dragging || waiting || report.ViewId != viewId || report.InteractionId != interactionId
                || !ReferenceEquals(report.Session, session)) return;
            SetTarget(report.Target);
            targetViewId = report.TargetViewId;
            targetOrigin = report.Origin;
            targetItem = null;
            string reason = "没有有效落点。";
            canDrop = target != null && target.IsReady;
            if (canDrop)
            {
                targetItem = FindTargetItem(target, report.Origin);
                canDrop = LoadoutOperations.TryEvaluateDetailed(source?.Rules, target.Rules, CreateIntent(0), out LoadoutFailure failure);
                reason = failure.Reason;
            }
            Feedback = canDrop ? targetItem == null ? "允许放置。" : "允许交换。" : reason;
            PublishVisual(LoadoutVisualKind.Preview,
                new PlacementPreview(canDrop, Feedback, targetOrigin, MovingItem.Size));
        }

        private void SetTarget(ContainerDisplaySnapshot next)
        {
            if (!ReferenceEquals(target?.EventSource, next?.EventSource))
            {
                targetChangedSubscription.Dispose();
                if (next?.EventSource != null && !ReferenceEquals(source.EventSource, next.EventSource))
                    targetChangedSubscription = EventBus.Global.Subscribe(LoadoutChannels.Changed,
                        next.EventSource, this, OnEndpointChanged);
            }
            target = next;
        }

        private ItemDisplaySnapshot FindTargetItem(ContainerDisplaySnapshot snapshot, Vector2Int origin)
        {
            if (snapshot.IsSlot) return snapshot.Items.Count > 0
                && !(ReferenceEquals(source.EventSource, snapshot.EventSource) && snapshot.Items[0].Id == item.Id) ? snapshot.Items[0] : null;
            foreach (ItemDisplaySnapshot candidate in snapshot.Items)
            {
                if (ReferenceEquals(source.EventSource, snapshot.EventSource) && candidate.Id == item.Id) continue;
                if (Overlaps(origin, MovingItem.Size, candidate.Origin, candidate.Size)) return candidate;
            }
            return null;
        }

        private LoadoutDropIntent CreateIntent(int requestId) => new LoadoutDropIntent(requestId,
            LoadoutDragKind.Item, source.Id, target.Id, item.Id, targetItem?.Id,
            item.Origin, targetOrigin, targetItem?.Origin ?? Vector2Int.zero, source.Revision, target.Revision, rotated);

        private void Submit()
        {
            if (!canDrop || source == null || target == null)
            {
                Finish(false, Feedback);
                return;
            }
            // EventBus 同步派发，必须先进入等待状态再发布操作命令。
            dragging = false;
            waiting = true;
            pendingRequestId = ++requestSequence;
            float duration = float.IsNaN(requestTimeout) || float.IsInfinity(requestTimeout)
                ? 8f : Mathf.Max(.1f, requestTimeout);
            deadline = Time.unscaledTime + duration;
            var request = new LoadoutOperationRequest(session, interactionId, pendingRequestId,
                source.EventSource, target.EventSource, CreateIntent(pendingRequestId));
            Feedback = "等待放置结果。";
            EventBus.Global.Publish(LoadoutChannels.OperationRequested, source.EventSource, request);
        }

        private void OnResult(LoadoutOperationResult result)
        {
            if (!waiting || !ReferenceEquals(result.Session, session) || result.InteractionId != interactionId
                || result.RequestId != pendingRequestId) return;
            Finish(result.Success, DescribeResult(result));
        }

        private static string DescribeResult(LoadoutOperationResult result)
        {
            switch (result.Code)
            {
                case LoadoutResultCode.Success: return "操作完成。";
                case LoadoutResultCode.SessionExpired: return "交互会话已结束，请重新打开背包或战利品窗口。";
                case LoadoutResultCode.ConfigurationMismatch: return "物品配置已改变，正在重新同步。";
                case LoadoutResultCode.RevisionConflict: return "物品状态已更新，请重新操作。";
                case LoadoutResultCode.ItemUnavailable: return "物品已移动或被其他玩家取走。";
                case LoadoutResultCode.ItemReserved: return "物品正被动作使用，请稍后操作。";
                case LoadoutResultCode.RequestExpired: return "操作回执已过期，正在重新同步。";
                case LoadoutResultCode.SnapshotInvalid: return "库存状态正在重新同步。";
                case LoadoutResultCode.Timeout: return "操作确认超时，正在重新同步。";
                default: return string.IsNullOrEmpty(result.Reason) ? "当前操作未完成。" : result.Reason;
            }
        }

        private void OnEndpointChanged() => Finish(false, "容器数据已变化，请重新操作。");
        public void CancelDrag() => Finish(false, "操作已取消。");

        private void Finish(bool success, string reason)
        {
            if (item == null) return;
            // 先使当前请求失效，防止视图复位时的 OnDisable 输入重入当前交互。
            var command = new LoadoutVisualCommand(session, viewId, interactionId,
                LoadoutVisualKind.Finish, success: success, reason: reason);
            object completedSession = session;
            sourceChangedSubscription.Dispose(); targetChangedSubscription.Dispose();
            source = target = null;
            item = targetItem = null;
            viewId = targetViewId = pendingRequestId = 0;
            interactionId = 0;
            dragging = waiting = canDrop = false;
            targetOrigin = Vector2Int.zero;
            Feedback = success ? "操作完成。" : reason ?? string.Empty;
            DetachSession();
            EventBus.Global.Publish(LoadoutChannels.Visual, completedSession, command);
        }

        private void PublishVisual(LoadoutVisualKind kind, PlacementPreview preview = default)
        {
            var command = new LoadoutVisualCommand(session, viewId, interactionId, kind,
                position, pointerCamera, dragLayer, targetViewId, preview, item: MovingItem);
            EventBus.Global.Publish(LoadoutChannels.Visual, session, command);
        }

        private static ItemDisplaySnapshot FindItem(ContainerDisplaySnapshot snapshot, string id)
        {
            foreach (ItemDisplaySnapshot candidate in snapshot.Items)
                if (string.Equals(candidate.Id, id, StringComparison.Ordinal)) return candidate;
            return null;
        }

        private static bool Overlaps(Vector2Int a, Vector2Int aSize, Vector2Int b, Vector2Int bSize) =>
            (long)a.x < (long)b.x + bSize.x && (long)b.x < (long)a.x + aSize.x
            && (long)a.y < (long)b.y + bSize.y && (long)b.y < (long)a.y + aSize.y;
    }
}
