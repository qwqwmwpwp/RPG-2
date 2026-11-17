using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>离线战利品会话的权威事件适配。网络角色始终拒绝进入此路径。</summary>
    public sealed class LocalLootSession : IDisposable
    {
        private readonly LootSource source;
        private readonly RoleLoadoutController role;
        private readonly Transform actor;
        private readonly Transform anchor;
        private readonly float distance;
        private readonly LoadoutWindowSession windowSession;
        private readonly List<EventSubscription> subscriptions = new List<EventSubscription>();
        private readonly LocalLoadoutReceipts receipts;
        private bool disposed;

        private LocalLootSession(LootSource source, RoleLoadoutController role, Transform actor,
            Transform anchor, float distance, LoadoutWindowSession windowSession)
        {
            this.source = source;
            this.role = role;
            this.actor = actor;
            this.anchor = anchor;
            this.distance = distance;
            this.windowSession = windowSession;
            receipts = LocalLoadoutReceipts.Get(role, windowSession, LoadoutOperationChannel.Loot, this);
        }

        public bool IsAccessible => !disposed && windowSession.IsActive && source.IsActive
            && role != null && role.isActiveAndEnabled && !role.NetworkManaged
            && WithinDistance(actor, anchor, distance);

        public static bool CanOpen(LootSource source, RoleLoadoutController role, Transform actor,
            Transform anchor, float distance, out string error)
        {
            error = null;
            if (source == null || !source.IsActive || role == null || role.NetworkManaged || !role.isActiveAndEnabled)
            {
                error = "本地战利品来源或角色无效。";
                return false;
            }
            if (!WithinDistance(actor, anchor, distance))
            {
                error = "距离战利品过远。";
                return false;
            }
            return role.EnsureInitialized(out error);
        }

        public static bool TryOpen(LootSource source, RoleLoadoutController role, Transform actor,
            Transform anchor, float distance, LoadoutWindowSession windowSession,
            out LocalLootSession result, out string error)
        {
            result = null;
            if (!CanOpen(source, role, actor, anchor, distance, out error)) return false;
            if (windowSession == null || !windowSession.IsActive)
            {
                error = "玩家背包尚未连接。";
                return false;
            }
            var session = new LocalLootSession(source, role, actor, anchor, distance, windowSession);
            foreach (LootSection section in source.Sections)
            {
                session.SubscribeEndpoint(section.Grid);
                if (section.Body != null) session.SubscribeEndpoint(section.Body);
            }
            foreach (RoleSlotConfiguration configuration in role.Configuration)
            {
                session.SubscribeOperation(role.GetSlot(configuration.slotId));
                if ((int)configuration.slotId < 4) session.SubscribeOperation(configuration.containerController);
            }
            result = session;
            return true;
        }

        private void SubscribeEndpoint(ILoadoutEndpoint endpoint)
        {
            subscriptions.Add(EventBus.Global.Subscribe<LoadoutSnapshotRequest>(LoadoutChannels.SnapshotRequested,
                endpoint.EventSource, this, request => EventBus.Global.Publish(LoadoutChannels.SnapshotReceived,
                    request.ReplySource, new LoadoutSnapshotReply(request.RequestId, endpoint.Snapshot))));
            SubscribeOperation(endpoint);
        }

        private void SubscribeOperation(ILoadoutEndpoint endpoint)
        {
            subscriptions.Add(EventBus.Global.Subscribe<LoadoutOperationRequest>(LoadoutChannels.OperationRequested,
                endpoint.EventSource, this, request => Operate(endpoint, request)));
        }

        private void Operate(ILoadoutEndpoint endpoint, LoadoutOperationRequest request)
        {
            if (!ReferenceEquals(request.Session, windowSession) || !ReferenceEquals(request.Source, endpoint.EventSource)) return;
            ILoadoutEndpoint target = (request.Target as LoadoutEndpointAddress)?.Endpoint;
            if (!source.Owns(endpoint) && !source.Owns(target)) return;
            LoadoutOperationKey key = role.CreateOperationKey(LoadoutOperationChannel.Loot, request.RequestId, receipts.SessionId);
            LoadoutStateVersion before = role.CaptureStateVersion(source);
            if (receipts.TryReplay(request, key, before, out LoadoutOperationOutcome replay, out bool replayed, source.Id))
            {
                PublishResult(request, replay, replayed);
                return;
            }

            var failure = new LoadoutFailure(LoadoutResultCode.Success, string.Empty);
            PreparedLoadoutOperation operation = null;
            LoadoutCommitRecord commit = null;
            if (!IsAccessible)
                failure = new LoadoutFailure(LoadoutResultCode.SessionExpired, "战利品会话已失效。");
            else if (target == null || !(source.Owns(endpoint) || role.OwnsEndpoint(endpoint))
                || !(source.Owns(target) || role.OwnsEndpoint(target)))
                failure = new LoadoutFailure(LoadoutResultCode.AccessDenied, "来源或目标不属于当前战利品会话。");
            else if (role.CommitCoordinator.TryEnter(out IDisposable transaction, out failure))
            {
                using (transaction)
                {
                    if (PreparedLoadoutOperation.TryPrepareDetailed(endpoint, target, request.Intent, out operation, out failure))
                    {
                        operation.TrackDependencies(ParticipatingEndpoints());
                        if (!IsAccessible)
                            failure = new LoadoutFailure(LoadoutResultCode.SessionExpired, "战利品会话已失效。");
                        else if (role.CommitCoordinator.TryCommit(operation, key, before, source, out commit, out failure))
                            operation.StageChanges(source, role, commit);
                    }
                }
            }
            var outcome = new LoadoutOperationOutcome(key,
                commit == null ? failure.Code : LoadoutResultCode.Success, failure.Reason, commit, role.CaptureStateVersion(source));
            receipts.Store(request, outcome, source.Id);
            try
            {
                PublishResult(request, outcome);
            }
            finally
            {
                if (commit != null) operation.PublishChanges(source, role, commit);
            }
        }

        private IEnumerable<ILoadoutEndpoint> ParticipatingEndpoints()
        {
            foreach (RoleSlotConfiguration configuration in role.Configuration)
            {
                yield return role.GetSlot(configuration.slotId);
                if ((int)configuration.slotId < 4) yield return configuration.containerController;
            }
            foreach (LootSection section in source.Sections)
            {
                yield return section.Grid;
                if (section.Body != null) yield return section.Body;
            }
        }

        private static void PublishResult(LoadoutOperationRequest request, LoadoutOperationOutcome outcome, bool replayed = false)
        {
            LoadoutDiagnostics.RecordOutcome(outcome, replayed);
            EventBus.Global.Publish(LoadoutChannels.OperationCompleted, request.Session,
                new LoadoutOperationResult(request, outcome));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (EventSubscription subscription in subscriptions) subscription.Dispose();
            subscriptions.Clear();
        }

        private static bool WithinDistance(Transform actor, Transform anchor, float distance) =>
            actor != null && anchor != null && actor.gameObject.scene == anchor.gameObject.scene
            && (actor.position - anchor.position).sqrMagnitude <= Mathf.Clamp(distance, .1f, 20f) * Mathf.Clamp(distance, .1f, 20f);
    }
}
