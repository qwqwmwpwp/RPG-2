using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    public enum LoadoutInputKind { Begin, Move, End, Cancel }
    public enum LoadoutVisualKind { Begin, Move, Preview, Finish }

    /// <summary>视图发布的交互输入。Session 是按引用比较的会话路由键。</summary>
    public readonly struct LoadoutInteractionInput
    {
        public LoadoutInteractionInput(object session, int viewId, long interactionId, LoadoutInputKind kind,
            ContainerDisplaySnapshot source, ItemDisplaySnapshot item, Vector2 position, Camera camera = null)
        {
            Session = session; ViewId = viewId; InteractionId = interactionId; Kind = kind;
            Source = source; Item = item; Position = position; Camera = camera;
        }
        public object Session { get; }
        public int ViewId { get; }
        public long InteractionId { get; }
        public LoadoutInputKind Kind { get; }
        public ContainerDisplaySnapshot Source { get; }
        public ItemDisplaySnapshot Item { get; }
        public Vector2 Position { get; }
        public Camera Camera { get; }
    }

    /// <summary>交换器给视图的表现指令。目标预览广播后由 TargetViewId 对应的视图绘制。</summary>
    public readonly struct LoadoutVisualCommand
    {
        public LoadoutVisualCommand(object session, int viewId, long interactionId, LoadoutVisualKind kind,
            Vector2 position = default, Camera camera = null, RectTransform dragLayer = null,
            int targetViewId = 0, PlacementPreview preview = default, bool success = false, string reason = null, ItemDisplaySnapshot item = null)
        {
            Session = session; ViewId = viewId; InteractionId = interactionId; Kind = kind;
            Position = position; Camera = camera; DragLayer = dragLayer; TargetViewId = targetViewId;
            Item = item; Preview = preview; Success = success; Reason = reason ?? string.Empty;
        }
        public object Session { get; }
        public int ViewId { get; }
        public long InteractionId { get; }
        public LoadoutVisualKind Kind { get; }
        public Vector2 Position { get; }
        public Camera Camera { get; }
        public RectTransform DragLayer { get; }
        public int TargetViewId { get; }
        public PlacementPreview Preview { get; }
        public ItemDisplaySnapshot Item { get; }
        public bool Success { get; }
        public string Reason { get; }
    }

    /// <summary>物品视图移动完成后，以实际显示位置探测得到的目标上下文。</summary>
    public readonly struct LoadoutTargetReport
    {
        public LoadoutTargetReport(object session, int viewId, long interactionId, int targetViewId,
            ContainerDisplaySnapshot target, Vector2Int origin, string targetItemId = null)
        {
            Session = session; ViewId = viewId; InteractionId = interactionId;
            TargetViewId = targetViewId; Target = target; Origin = origin; TargetItemId = targetItemId;
        }
        public object Session { get; }
        public int ViewId { get; }
        public long InteractionId { get; }
        public int TargetViewId { get; }
        public ContainerDisplaySnapshot Target { get; }
        public Vector2Int Origin { get; }
        public string TargetItemId { get; }
    }

    public readonly struct LoadoutSnapshotRequest
    {
        public LoadoutSnapshotRequest(object replySource, int requestId)
        { ReplySource = replySource; RequestId = requestId; }
        public object ReplySource { get; }
        public int RequestId { get; }
    }

    public readonly struct LoadoutSnapshotReply
    {
        public LoadoutSnapshotReply(int requestId, ContainerSnapshot snapshot, string error = null)
        { RequestId = requestId; Snapshot = ContainerDisplaySnapshot.Capture(snapshot); Error = error ?? string.Empty; }
        public int RequestId { get; }
        public ContainerDisplaySnapshot Snapshot { get; }
        public string Error { get; }
    }

    /// <summary>只携带本次来源与目标的操作请求，不携带角色全部容器的索引。</summary>
    public readonly struct LoadoutOperationRequest
    {
        public LoadoutOperationRequest(object session, long interactionId, int requestId,
            object source, object target, LoadoutDropIntent intent)
        {
            Session = session; InteractionId = interactionId; RequestId = requestId;
            Source = source; Target = target; Intent = intent;
        }
        public object Session { get; }
        public long InteractionId { get; }
        public int RequestId { get; }
        public object Source { get; }
        public object Target { get; }
        public LoadoutDropIntent Intent { get; }
    }

    public readonly struct LoadoutOperationResult
    {
        public LoadoutOperationResult(LoadoutOperationRequest request, bool success, string reason = null)
        {
            Session = request.Session; InteractionId = request.InteractionId; RequestId = request.RequestId;
            Success = success; Reason = reason ?? string.Empty;
            Outcome = null;
        }
        public LoadoutOperationResult(LoadoutOperationRequest request, LoadoutOperationOutcome outcome)
        {
            Session = request.Session; InteractionId = request.InteractionId; RequestId = request.RequestId;
            Outcome = outcome;
            Success = outcome != null && outcome.Success;
            Reason = outcome?.Message ?? string.Empty;
        }
        public object Session { get; }
        public long InteractionId { get; }
        public int RequestId { get; }
        public bool Success { get; }
        public string Reason { get; }
        public LoadoutOperationOutcome Outcome { get; }
        public LoadoutResultCode Code => Outcome?.Code ?? (Success ? LoadoutResultCode.Success : LoadoutResultCode.Unknown);
    }
}
