using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>预测与提交共用的完整候选状态验证。</summary>
    public static class LoadoutOperations
    {
        public static bool TryEvaluate(ContainerSnapshot source, ContainerSnapshot target,
            LoadoutDropIntent intent, out string error)
        {
            bool success = TryEvaluateDetailed(source, target, intent, out LoadoutFailure failure);
            error = success ? null : failure.Reason;
            return success;
        }

        public static bool TryEvaluateDetailed(ContainerSnapshot source, ContainerSnapshot target,
            LoadoutDropIntent intent, out LoadoutFailure failure) =>
            TryEvaluateDetailed(LoadoutRuleSnapshot.Capture(source), LoadoutRuleSnapshot.Capture(target), intent, out failure);

        public static bool TryEvaluateDetailed(LoadoutRuleSnapshot source, LoadoutRuleSnapshot target,
            LoadoutDropIntent intent, out LoadoutFailure failure) =>
            TryBuildDetailed(source, target, intent, out _, out _, out failure);

        internal static bool TryBuildDetailed(LoadoutRuleSnapshot source, LoadoutRuleSnapshot target, LoadoutDropIntent intent,
            out List<LoadoutRuleItem> sourceItems, out List<LoadoutRuleItem> targetItems, out LoadoutFailure failure)
        {
            sourceItems = null; targetItems = null; failure = default;
            if (source == null || target == null || !source.IsReady || !target.IsReady)
                return Fail(LoadoutResultCode.NotReady, "来源或目标当前不可用。", out failure);
            if (intent.Kind != LoadoutDragKind.Item && intent.Kind != LoadoutDragKind.Container)
                return Fail(LoadoutResultCode.InvalidRequest, "未知的物品交互类型。", out failure);
            bool same = source.EndpointToken == target.EndpointToken;
            if (same && !string.Equals(source.DependencySignature, target.DependencySignature, StringComparison.Ordinal))
                return Fail(LoadoutResultCode.RevisionConflict, "同一端点的来源与目标快照不一致。", out failure);
            if (!ValidateItemIdentities(source.Items, out failure)
                || !ValidateItemIdentities(target.Items, out failure)) return false;
            if (!SameId(source.Id, intent.SourceContainerId) || !SameId(target.Id, intent.TargetContainerId)
                || source.Revision != intent.SourceVersion || target.Revision != intent.TargetVersion)
                return Fail(LoadoutResultCode.RevisionConflict, "端点身份或版本已改变。", out failure);
            if (!same && SameId(source.Id, target.Id)) return Fail(LoadoutResultCode.SnapshotInvalid, "不同端点不能使用相同实例编号。", out failure);
            LoadoutRuleItem moving = Find(source.Items, intent.ItemId);
            if (moving == null || moving.Origin != intent.SourceOrigin)
                return Fail(LoadoutResultCode.ItemUnavailable, "来源物品或位置已改变。", out failure);
            LoadoutRuleItem previous = intent.IsSwap ? Find(target.Items, intent.TargetItemId) : null;
            if (moving.Reserved || previous != null && previous.Reserved)
                return Fail(LoadoutResultCode.ItemReserved, "物品或容器内的消耗品正被使用动作占用。", out failure);
            if (intent.IsSwap && (previous == null || previous.Origin != intent.TargetItemOrigin || SameId(previous.Id, moving.Id)))
                return Fail(LoadoutResultCode.ItemUnavailable, "交换目标已改变。", out failure);
            if (!same)
            {
                var identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (LoadoutRuleItem item in source.Items)
                    if (item == null || !identities.Add(item.Id)) return Fail(LoadoutResultCode.SnapshotInvalid, "来源物品实例无效或重复。", out failure);
                foreach (LoadoutRuleItem item in target.Items)
                    if (item == null || !identities.Add(item.Id)) return Fail(LoadoutResultCode.SnapshotInvalid, "不同端点中存在重复物品实例。", out failure);
            }
            if (target.WithdrawalOnly && !same) return Fail(LoadoutResultCode.AccessDenied, "暂不允许向战利品源放入或交换物品。", out failure);
            if (!same && source.WithdrawalOnly && intent.IsSwap) return Fail(LoadoutResultCode.AccessDenied, "拾取战利品需要空目标，不能交换。", out failure);
            LoadoutRuleItem placed = moving.WithRotation(intent.Rotated ?? moving.Rotated);
            Vector2Int destination = target.IsSlot ? Vector2Int.zero : intent.Origin;
            if (same && destination == moving.Origin && placed.Rotated == moving.Rotated) return Fail(LoadoutResultCode.InvalidRequest, "物品仍在原位置。", out failure);
            if (previous != null && !target.IsSlot && !Overlap(destination, placed.Size, previous.Origin, previous.Size))
                return Fail(LoadoutResultCode.InvalidRequest, "交换物品不在目标占地内。", out failure);
            sourceItems = CopyExcept(source.Items, moving.Id, same ? previous?.Id : null);
            targetItems = same ? sourceItems : CopyExcept(target.Items, previous?.Id, null);
            targetItems.Add(placed.At(destination));
            if (previous != null) sourceItems.Add(previous.At(source.IsSlot ? Vector2Int.zero : moving.Origin));
            if (!ValidateRules(source, sourceItems, out failure)) return false;
            return same || ValidateRules(target, targetItems, out failure);
        }

        internal static bool ValidateRules(LoadoutRuleSnapshot endpoint, IReadOnlyList<LoadoutRuleItem> items, out LoadoutFailure failure)
        {
            failure = default;
            if (endpoint.IsSlot)
            {
                if (items.Count > 1) return Fail(LoadoutResultCode.CapacityExceeded, "物品槽只能保存一个实例。", out failure);
                if (items.Count == 0) return true;
                if (endpoint.Rule == null) return Fail(LoadoutResultCode.ConfigurationMismatch, "物品槽未配置标签规则。", out failure);
                LoadoutRuleItem item = items[0];
                if (!ValidItem(item)) return Fail(LoadoutResultCode.SnapshotInvalid, "物品实例数据无效。", out failure);
                if (item.IsContainer && (!item.HasStorage || item.Count != 1))
                    return Fail(LoadoutResultCode.SnapshotInvalid, "容器物品必须关联一个独立储物实例且不可堆叠。", out failure);
                return Accepts(endpoint.Rule, item, out failure);
            }
            var valid = new HashSet<Vector2Int>();
            foreach (Vector2Int cell in endpoint.ValidCells ?? Array.Empty<Vector2Int>())
                if (!BackpackContainerData.IsSupportedCoordinate(cell) || !valid.Add(cell))
                    return Fail(LoadoutResultCode.SnapshotInvalid, "容器有效格坐标无效或重复；列坐标必须在 0 到 4 之间。", out failure);
            var occupied = new HashSet<Vector2Int>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (LoadoutRuleItem item in items)
            {
                if (!ValidItem(item) || !ids.Add(item.Id)) return Fail(LoadoutResultCode.SnapshotInvalid, "物品实例数据无效或重复。", out failure);
                if (endpoint.RejectContainers && item.IsContainer) return Fail(LoadoutResultCode.RuleRejected, "该储物容器禁止存放容器物品。", out failure);
                if (endpoint.Rule != null && !Accepts(endpoint.Rule, item, out failure)) return false;
                if ((long)item.Origin.x + item.Size.x > BackpackContainerData.MaxColumns)
                    return Fail(LoadoutResultCode.CapacityExceeded, "物品占地超出容器最多 5 列的边界。", out failure);
                if ((long)item.Size.x * item.Size.y > valid.Count) return Fail(LoadoutResultCode.CapacityExceeded, "物品占地超过容器容量。", out failure);
                for (int y = 0; y < item.Size.y; y++)
                for (int x = 0; x < item.Size.x; x++)
                {
                    var cell = item.Origin + new Vector2Int(x, y);
                    if (!valid.Contains(cell)) return Fail(LoadoutResultCode.CapacityExceeded, "物品覆盖无效格或超出容器边界。", out failure);
                    if (!occupied.Add(cell)) return Fail(LoadoutResultCode.CapacityExceeded, "物品占地重叠。", out failure);
                }
            }
            return true;
        }

        private static bool ValidateItemIdentities(IReadOnlyList<LoadoutRuleItem> items, out LoadoutFailure failure)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (LoadoutRuleItem item in items)
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
                    return Fail(LoadoutResultCode.SnapshotInvalid, "物品实例数据无效或重复。", out failure);
            failure = default;
            return true;
        }

        private static bool ValidItem(LoadoutRuleItem item) => item != null && !string.IsNullOrWhiteSpace(item.Id)
            && item.RuntimeValid && item.StorageValid
            && item.Count > 0 && item.Size.x > 0 && item.Size.y > 0 && item.Origin.x >= 0 && item.Origin.y >= 0
            && (long)item.Origin.x + item.Size.x <= int.MaxValue && (long)item.Origin.y + item.Size.y <= int.MaxValue;
        private static LoadoutRuleItem Find(IReadOnlyList<LoadoutRuleItem> items, string id)
        {
            foreach (LoadoutRuleItem item in items) if (item != null && SameId(item.Id, id)) return item;
            return null;
        }
        private static List<LoadoutRuleItem> CopyExcept(IReadOnlyList<LoadoutRuleItem> items, string first, string second)
        {
            var result = new List<LoadoutRuleItem>();
            foreach (LoadoutRuleItem item in items) if (item != null && !SameId(item.Id, first) && !SameId(item.Id, second)) result.Add(item);
            return result;
        }
        private static bool Overlap(Vector2Int a, Vector2Int aSize, Vector2Int b, Vector2Int bSize) =>
            (long)a.x < (long)b.x + bSize.x && (long)b.x < (long)a.x + aSize.x
            && (long)a.y < (long)b.y + bSize.y && (long)b.y < (long)a.y + aSize.y;
        private static bool SameId(string first, string second) => string.Equals(first, second, StringComparison.Ordinal);
        private static bool Fail(LoadoutResultCode code, string reason, out LoadoutFailure failure)
        { failure = new LoadoutFailure(code, reason); return false; }

        private static bool Accepts(LoadoutRuleConstraint rule, LoadoutRuleItem item, out LoadoutFailure failure)
        {
            if (!rule.Accepts(item.ItemType, item.EquipmentType, out string reason))
                return Fail(LoadoutResultCode.RuleRejected, reason, out failure);
            failure = default;
            return true;
        }

        internal static bool Validate(ContainerSnapshot endpoint, IReadOnlyList<ItemDisplayData> items, out string error)
        {
            LoadoutRuleSnapshot snapshot = LoadoutRuleSnapshot.Capture(endpoint, items);
            bool success = ValidateRules(snapshot, snapshot.Items, out LoadoutFailure failure);
            error = success ? null : failure.Reason;
            return success;
        }
    }

    /// <summary>本地端点入口共用角色版本和通知边界，静默调用仍推进版本。</summary>
    internal sealed class LocalLoadoutOperation
    {
        private readonly ILoadoutEndpoint source, target;
        private readonly LoadoutDropIntent intent;
        private readonly RoleLoadoutController owner, otherOwner;
        private readonly LoadoutOperationKey key, otherKey;
        private readonly LoadoutStateVersion before, otherBefore;
        private readonly LoadoutCommitCoordinator coordinator, otherCoordinator;
        private PreparedLoadoutOperation prepared;
        private LoadoutCommitRecord commit;
        private bool staged, published;

        internal LocalLoadoutOperation(ILoadoutEndpoint source, ILoadoutEndpoint target, LoadoutDropIntent intent,
            int requestId, string session, string fallbackEpoch)
        {
            this.source = source; this.target = target; this.intent = intent;
            RoleLoadoutController sourceOwner = LocalOwner(source);
            RoleLoadoutController targetOwner = LocalOwner(target);
            // 网络角色仍由服务器拥有版本。此低层适配不替代既有调用方的权限边界。
            owner = sourceOwner != null ? sourceOwner : targetOwner;
            otherOwner = sourceOwner != null && targetOwner != null && sourceOwner != targetOwner ? targetOwner : null;
            key = owner != null ? owner.CreateOperationKey(LoadoutOperationChannel.Local, requestId, session)
                : new LoadoutOperationKey(0, fallbackEpoch, session, LoadoutOperationChannel.Local, requestId);
            coordinator = owner != null ? owner.CommitCoordinator : new LoadoutCommitCoordinator(null, fallbackEpoch, source);
            otherCoordinator = otherOwner != null ? otherOwner.CommitCoordinator : null;
            before = Current();
            otherKey = otherOwner != null ? otherOwner.CreateOperationKey(LoadoutOperationChannel.Local, requestId, session) : default;
            otherBefore = otherOwner != null ? otherOwner.CaptureStateVersion() : default;
        }

        internal bool TryExecute(out LoadoutFailure failure)
        {
            if (!coordinator.TryEnter(out IDisposable transaction, out failure, otherCoordinator)) return false;
            using var transactionScope = transaction;
            if (!PreparedLoadoutOperation.TryPrepareDetailed(source, target, intent, out prepared, out failure)) return false;
            TrackOwnerDependencies(owner);
            TrackOwnerDependencies(otherOwner);
            return coordinator.TryCommit(prepared, key, before, null, out commit, out failure,
                otherCoordinator, otherKey, otherBefore);
        }

        internal LoadoutOperationOutcome GetOutcome(LoadoutFailure failure) => new LoadoutOperationOutcome(key,
            commit != null ? LoadoutResultCode.Success : failure.Code,
            commit != null ? string.Empty : failure.Reason, commit, commit?.After ?? Current());

        internal void StageChanges()
        {
            if (commit == null || staged) return;
            staged = true;
            // 在任何结果或角色订阅者回调之前，双方都先占据各自提交队列中的顺序。
            coordinator.Queue(commit, () => prepared.PublishChanges());
            if (otherCoordinator != null) otherCoordinator.Queue(prepared.OtherCommittedRecord, null);
        }

        internal void PublishChanges()
        {
            if (commit == null || published) return;
            StageChanges();
            published = true;
            try
            {
                coordinator.Flush();
            }
            finally
            {
                otherCoordinator?.Flush();
            }
        }

        private LoadoutStateVersion Current() => coordinator.Capture();

        private void TrackOwnerDependencies(RoleLoadoutController role)
        {
            if (role == null) return;
            var participants = new List<ILoadoutEndpoint>();
            foreach (RoleSlotConfiguration configuration in role.Configuration)
            {
                participants.Add(role.GetSlot(configuration.slotId));
                if (configuration.containerController != null) participants.Add(configuration.containerController);
            }
            prepared.TrackDependencies(participants);
        }

        internal static RoleLoadoutController Owner(ILoadoutEndpoint value)
        {
            if (value is LoadoutSlotEndpoint slot) return slot.RoleOwner;
            if (value is BackpackContainerController grid) return grid.RuntimeOwner;
            return null;
        }

        internal static RoleLoadoutController LocalOwner(ILoadoutEndpoint value)
        {
            RoleLoadoutController role = Owner(value);
            return role != null && !role.NetworkManaged ? role : null;
        }
    }

    /// <summary>事件路由地址。控制器端点只在数据层内部解析，UI 无须持有组件。</summary>
    internal sealed class LoadoutEndpointAddress
    {
        internal LoadoutEndpointAddress(ILoadoutEndpoint endpoint) { Endpoint = endpoint; }
        internal ILoadoutEndpoint Endpoint { get; }
    }

    /// <summary>数据端点的事件适配。结果先结束交互，再广播模型变化。</summary>
    internal sealed class LoadoutEndpointEvents : IDisposable
    {
        private readonly ILoadoutEndpoint endpoint;
        private EventSubscription snapshots;
        private EventSubscription operations;
        internal LoadoutEndpointEvents(ILoadoutEndpoint endpoint)
        {
            this.endpoint = endpoint;
            snapshots = EventBus.Global.Subscribe<LoadoutSnapshotRequest>(LoadoutChannels.SnapshotRequested,
                endpoint.EventSource, this, OnSnapshotRequested);
            operations = EventBus.Global.Subscribe<LoadoutOperationRequest>(LoadoutChannels.OperationRequested,
                endpoint.EventSource, this, OnOperationRequested);
        }
        private void OnSnapshotRequested(LoadoutSnapshotRequest request) =>
            EventBus.Global.Publish(LoadoutChannels.SnapshotReceived, request.ReplySource,
                new LoadoutSnapshotReply(request.RequestId, endpoint.Snapshot));
        private void OnOperationRequested(LoadoutOperationRequest request)
        {
            if (!ReferenceEquals(request.Source, endpoint.EventSource)) return;
            ILoadoutEndpoint target = (request.Target as LoadoutEndpointAddress)?.Endpoint;
            // 外部战利品操作由所属窗口统一验证距离、会话和双方归属，不能在角色源事件中抢先提交。
            if (endpoint is IExternalLootEndpoint || target is IExternalLootEndpoint) return;
            RoleLoadoutController sourceOwner = LocalLoadoutOperation.Owner(endpoint);
            RoleLoadoutController targetOwner = LocalLoadoutOperation.Owner(target);
            RoleLoadoutController receiptRole = LocalLoadoutOperation.LocalOwner(endpoint)
                ?? LocalLoadoutOperation.LocalOwner(target);
            object receiptOwner = receiptRole != null ? (object)receiptRole : endpoint;
            LocalLoadoutReceipts receipts = LocalLoadoutReceipts.Get(receiptOwner, request.Session,
                LoadoutOperationChannel.Local, this);
            var operation = new LocalLoadoutOperation(endpoint, target, request.Intent,
                request.RequestId, receipts.SessionId, receipts.FallbackEpoch);
            LoadoutOperationOutcome current = operation.GetOutcome(default);
            if (receipts.TryReplay(request, current.Key, current.Current, out LoadoutOperationOutcome replay, out bool replayed))
            {
                PublishResult(request, replay, replayed);
                return;
            }
            LoadoutFailure failure;
            bool success;
            if (request.Session is LoadoutWindowSession window && !window.IsActive)
            {
                failure = new LoadoutFailure(LoadoutResultCode.SessionExpired, "库存窗口会话已结束。");
                success = false;
            }
            else if (sourceOwner != null && sourceOwner.NetworkManaged || targetOwner != null && targetOwner.NetworkManaged)
            {
                failure = new LoadoutFailure(LoadoutResultCode.AccessDenied, "网络库存必须通过服务器操作入口提交。");
                success = false;
            }
            else success = operation.TryExecute(out failure);
            if (success) operation.StageChanges();
            LoadoutOperationOutcome outcome = operation.GetOutcome(failure);
            receipts.Store(request, outcome);
            try
            {
                PublishResult(request, outcome);
            }
            finally
            {
                if (success) operation.PublishChanges();
            }
        }
        private static void PublishResult(LoadoutOperationRequest request, LoadoutOperationOutcome outcome, bool replayed = false)
        {
            LoadoutDiagnostics.RecordOutcome(outcome, replayed);
            EventBus.Global.Publish(LoadoutChannels.OperationCompleted, request.Session,
                new LoadoutOperationResult(request, outcome));
        }
        public void Dispose() { snapshots.Dispose(); operations.Dispose(); }
    }
}



