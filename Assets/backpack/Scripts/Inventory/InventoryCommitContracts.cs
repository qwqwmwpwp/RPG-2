using System;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>准备外部表示，不发布网络条目或业务通知。</summary>
    public interface IInventoryCommitParticipant
    {
        bool TryPrepare(ItemId requiredItem, out IInventoryPreparedCommit prepared, out string error);
    }

    /// <summary>每次准备独立的句柄；Publish 只在权威事务不可回滚之后调用。</summary>
    public interface IInventoryPreparedCommit
    {
        void Publish();
        void Discard();
    }

    public static class InventoryCommitParticipants
    {
        public static IInventoryCommitParticipant Combine(IInventoryCommitParticipant first, IInventoryCommitParticipant second)
            => first == null ? second : second == null || ReferenceEquals(first, second) ? first : new Chain(first, second);

        private sealed class Chain : IInventoryCommitParticipant
        {
            private readonly IInventoryCommitParticipant first, second;
            internal Chain(IInventoryCommitParticipant first, IInventoryCommitParticipant second)
            { this.first = first; this.second = second; }

            public bool TryPrepare(ItemId requiredItem, out IInventoryPreparedCommit prepared, out string error)
            {
                prepared = null;
                IInventoryPreparedCommit left = null, right = null;
                try
                {
                    if (!first.TryPrepare(requiredItem, out left, out error)
                        || !second.TryPrepare(requiredItem, out right, out error))
                    { Discard(left); Discard(right); return false; }
                    prepared = new Group(left, right);
                    return true;
                }
                catch { Discard(left); Discard(right); throw; }
            }
        }

        private sealed class Group : IInventoryPreparedCommit
        {
            private readonly IInventoryPreparedCommit[] handles;
            internal Group(params IInventoryPreparedCommit[] handles) { this.handles = handles; }
            public void Publish()
            {
                // One participant's publication failure must not suppress the remaining participants.
                Exception failure = null;
                foreach (IInventoryPreparedCommit handle in handles)
                    try { handle?.Publish(); } catch (Exception exception) { failure = failure ?? exception; }
                if (failure != null) throw failure;
            }
            public void Discard() { foreach (IInventoryPreparedCommit handle in handles) InventoryCommitParticipants.Discard(handle); }
        }

        internal static void Discard(IInventoryPreparedCommit handle)
        {
            try { handle?.Discard(); }
            catch (Exception exception) { InventoryTransactions.LogPostCommitFailure("取消库存投影准备失败", exception); }
        }
    }
}
