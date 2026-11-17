using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>角色权威版本、事务互斥、提交事实和顺序发布的唯一拥有者。</summary>
    internal sealed class LoadoutCommitCoordinator
    {
        private readonly RoleLoadoutController role;
        private readonly ILoadoutEndpoint standaloneEndpoint;
        private readonly Queue<(LoadoutCommitRecord commit, Action publish)> publications
            = new Queue<(LoadoutCommitRecord, Action)>();
        private bool executing;
        private bool transactionInvalidated;
        private bool restoring;
        private bool publishing;

        internal LoadoutCommitCoordinator(RoleLoadoutController role, string epoch = null,
            ILoadoutEndpoint standaloneEndpoint = null)
        {
            this.role = role;
            this.standaloneEndpoint = standaloneEndpoint;
            Epoch = epoch ?? Guid.NewGuid().ToString("N");
        }

        internal string Epoch { get; private set; }
        internal long Revision { get; private set; }
        internal bool IsExecuting => executing;
        internal bool IsPublishing => publishing || publications.Count != 0;
        internal bool CanAdvance => Revision < long.MaxValue;
        internal event Action<LoadoutCommitRecord> Committed;

        internal LoadoutStateVersion Capture(LootSource source = null)
        {
            if (standaloneEndpoint != null)
            {
                ContainerSnapshot snapshot = standaloneEndpoint is UnityEngine.Object value && value == null
                    ? null : standaloneEndpoint.Snapshot;
                return new LoadoutStateVersion(Epoch, Revision, snapshot?.Id, snapshot?.Revision ?? 0);
            }
            return new LoadoutStateVersion(Epoch, Revision, source?.Id, source?.Revision ?? (role != null && role.NetworkManaged ? -1 : 0));
        }

        internal void ResetVersion(string epoch, long revision = 0)
        {
            if (string.IsNullOrEmpty(epoch) || revision < 0) throw new ArgumentException("角色版本无效。");
            Epoch = epoch;
            Revision = revision;
        }

        private void AdvanceVersion()
        {
            if (!CanAdvance) throw new InvalidOperationException("角色库存版本已达到上限。");
            Revision++;
        }

        internal bool TryEnter(out IDisposable transaction, out LoadoutFailure failure,
            LoadoutCommitCoordinator other = null)
        {
            transaction = null;
            if (executing || other != null && other.executing)
                return Fail(LoadoutResultCode.NotReady, "角色库存正在完成另一笔操作。", out failure);
            if (!CanAdvance || other != null && !other.CanAdvance)
                return Fail(LoadoutResultCode.RevisionConflict, "角色库存版本已达到上限。", out failure);
            executing = true;
            transactionInvalidated = false;
            if (other != null)
            {
                other.executing = true;
                other.transactionInvalidated = false;
            }
            transaction = new Transaction(this, other);
            failure = default;
            return true;
        }

        internal bool TryEnterRestore(out IDisposable transaction, out LoadoutFailure failure)
        {
            transaction = null;
            if (executing || IsPublishing)
                return Fail(LoadoutResultCode.NotReady, "角色旧世代库存事务尚未结束。", out failure);
            executing = true;
            restoring = true;
            transactionInvalidated = false;
            transaction = new Transaction(this, null);
            failure = default;
            return true;
        }

        // 暂停必须立即生效，包括准备中和版本耗尽时；其通知仍进入同一提交队列。
        internal void CommitSystemState(LoadoutOperationKey key, Action applyState, Action publish)
        {
            LoadoutStateVersion before = Capture();
            applyState();
            if (executing) transactionInvalidated = true;
            if (CanAdvance) AdvanceVersion();
            var commit = new LoadoutCommitRecord(key, before, Capture(), Array.Empty<LoadoutEndpointChange>());
            Queue(commit, publish);
            Flush();
        }

        internal bool ValidateVersion(LoadoutStateVersion expected, out LoadoutFailure failure)
        {
            if (transactionInvalidated || expected.RoleRevision != Revision
                || !string.Equals(expected.Epoch, Epoch, StringComparison.Ordinal))
                return Fail(LoadoutResultCode.RevisionConflict, "角色库存版本已改变。", out failure);
            failure = default;
            return true;
        }

        internal bool TryCommit(PreparedLoadoutOperation prepared, LoadoutOperationKey key,
            LoadoutStateVersion before, LootSource source, out LoadoutCommitRecord commit,
            out LoadoutFailure failure, LoadoutCommitCoordinator other = null,
            LoadoutOperationKey otherKey = default, LoadoutStateVersion otherBefore = default,
            Action completeModel = null, string restoredEpoch = null)
        {
            commit = null;
            if (!executing || other != null && !other.executing)
                return Fail(LoadoutResultCode.NotReady, "库存提交未持有事务。", out failure);
            if (restoring != (restoredEpoch != null))
                return Fail(LoadoutResultCode.InvalidRequest, "恢复与普通库存提交的事务类型不匹配。", out failure);
            if (!ValidateVersion(before, out failure)
                || other != null && !other.ValidateVersion(otherBefore, out failure)) return false;
            if (source != null)
            {
                if (!string.Equals(source.Id, before.SourceId, StringComparison.Ordinal) || source.Revision != before.SourceRevision)
                    return Fail(LoadoutResultCode.RevisionConflict, "战利品库存版本已改变。", out failure);
                int writes = 0;
                foreach (ILoadoutEndpoint endpoint in prepared.Candidates.Keys)
                    if (source.Owns(endpoint)) writes++;
                if (source.Revision > int.MaxValue - writes)
                    return Fail(LoadoutResultCode.RevisionConflict, "战利品库存版本已达到上限。", out failure);
            }
            if (!prepared.TryCommit(out failure)) return false;
            completeModel?.Invoke();
            if (restoredEpoch != null) ResetVersion(restoredEpoch);
            else if (role != null) AdvanceVersion();
            if (other != null && other.role != null) other.AdvanceVersion();
            commit = prepared.CreateCommitRecord(key, before, Capture(source));
            prepared.CommittedRecord = commit;
            if (other != null)
                prepared.OtherCommittedRecord = prepared.CreateCommitRecord(otherKey, otherBefore, other.Capture());
            return true;
        }

        internal void Queue(LoadoutCommitRecord commit, Action publish)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            publications.Enqueue((commit, publish));
        }

        internal void Flush()
        {
            if (publishing) return;
            publishing = true;
            try
            {
                while (publications.Count != 0)
                {
                    var next = publications.Dequeue();
                    if (Committed != null)
                        foreach (Action<LoadoutCommitRecord> listener in Committed.GetInvocationList())
                            Notify(() => listener(next.commit));
                    if (next.publish != null) Notify(next.publish);
                    if (role != null) Notify(role.PublishRuntimeChanged);
                }
            }
            finally { publishing = false; }
        }

        private void Notify(Action action)
        {
            try { action(); }
            catch (Exception exception) { Debug.LogException(exception, role); }
        }

        private static bool Fail(LoadoutResultCode code, string reason, out LoadoutFailure failure)
        { failure = new LoadoutFailure(code, reason); return false; }

        private sealed class Transaction : IDisposable
        {
            private LoadoutCommitCoordinator owner;
            private readonly LoadoutCommitCoordinator other;
            internal Transaction(LoadoutCommitCoordinator owner, LoadoutCommitCoordinator other)
            { this.owner = owner; this.other = other; }
            public void Dispose()
            {
                if (owner == null) return;
                owner.executing = false;
                owner.restoring = false;
                owner.transactionInvalidated = false;
                if (other != null)
                {
                    other.executing = false;
                    other.transactionInvalidated = false;
                }
                owner = null;
            }
        }
    }
}
