using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    internal delegate bool InventoryTransactionBody(InventoryTransactions transaction, out string error);

    /// <summary>一次同步库存操作：保护写入、最小撤销记录、准备外部表示及提交后发布。</summary>
    internal sealed class InventoryTransactions
    {
        private readonly InventoryContainer[] containers;
        private readonly Dictionary<RuntimeItem, ItemState> items = new Dictionary<RuntimeItem, ItemState>();
        private readonly List<PlacementState> placements = new List<PlacementState>();
        private readonly List<Action> undo = new List<Action>();
        private readonly List<IInventoryPreparedCommit> prepared = new List<IInventoryPreparedCommit>();
        private readonly Dictionary<InventoryContainer, ItemId[]> pending = new Dictionary<InventoryContainer, ItemId[]>();
        private bool committed, released;
        [ThreadStatic] private static PublicationScope publicationScope;

        private InventoryTransactions(InventoryContainer source, InventoryContainer destination)
        { containers = destination == null || ReferenceEquals(source, destination) ? new[] { source } : new[] { source, destination }; }

        internal static bool Execute(InventoryContainer source, InventoryContainer destination, InventoryTransactionBody body, out string error)
        {
            var transaction = new InventoryTransactions(source, destination);
            foreach (InventoryContainer container in transaction.containers)
            {
                if (!container.TryAcquireTransaction(transaction))
                { transaction.Release(); error = "库存正在提交另一项操作，暂不可修改。"; return false; }
                transaction.pending.Add(container, container.CapturePendingViewChanges());
            }
            try { return body(transaction, out error); }
            catch (Exception exception)
            {
                if (transaction.committed)
                { LogPostCommitFailure("库存已提交，后续处理失败", exception); error = null; return true; }
                error = "库存事务未能提交：" + exception.Message;
                return false;
            }
            finally
            {
                if (!transaction.committed)
                    try { transaction.Rollback(); } finally { transaction.Release(); }
            }
        }

        internal void Track(RuntimeItem item)
        {
            if (item == null || items.ContainsKey(item)) return;
            items.Add(item, new ItemState(item.StackCount, item.RootOwner));
            foreach (InventoryContainer container in containers)
                if (container.TryGetPlacement(item, out InventoryPlacement placement))
                    placements.Add(new PlacementState(container, item, placement));
        }

        internal void OnRollback(Action action) => undo.Add(action);

        internal bool Prepare(IInventoryCommitParticipant participant, ItemId requiredItem, out string error)
        {
            error = null;
            if (participant == null) return true;
            IInventoryPreparedCommit handle = null;
            try
            {
                bool accepted = participant.TryPrepare(requiredItem, out handle, out error);
                if (handle != null) prepared.Add(handle);
                return accepted;
            }
            catch { if (handle != null) prepared.Add(handle); throw; }
        }

        internal bool Commit(Action notify, out string error, Action finalize = null)
        {
            // No fallible participant or user code is called beyond this point as part of rollback.
            finalize?.Invoke();
            foreach (InventoryContainer container in containers) container.AdvanceTransactionRevision();
            committed = true;
            Action publish = () =>
            {
                try
                {
                    foreach (IInventoryPreparedCommit handle in prepared)
                        RunPostCommit(() => handle.Publish(), "库存已提交，投影发布失败");
                }
                finally { Release(); }
                RunPostCommit(notify, "库存已提交，业务通知失败");
            };
            if (publicationScope != null) publicationScope.Add(publish); else publish();
            error = null;
            return true;
        }

        private void Rollback()
        {
            try
            {
                foreach (InventoryContainer container in containers)
                    foreach (RuntimeItem item in items.Keys) container.RemoveTransactionPlacement(item);
                for (int index = undo.Count - 1; index >= 0; index--)
                    try { undo[index](); } catch (Exception exception) { LogPostCommitFailure("库存业务撤销失败", exception); }
                foreach (KeyValuePair<RuntimeItem, ItemState> pair in items)
                { pair.Key.SetStackCount(pair.Value.Count); pair.Key.RootOwner = pair.Value.Owner; }
                foreach (PlacementState placement in placements)
                    placement.Container.RestoreTransactionPlacement(placement.Item, placement.Placement);
                foreach (KeyValuePair<InventoryContainer, ItemId[]> pair in pending) pair.Key.RestorePendingViewChanges(pair.Value);
            }
            finally { foreach (IInventoryPreparedCommit handle in prepared) InventoryCommitParticipants.Discard(handle); }
        }

        private void Release()
        {
            if (released) return;
            foreach (InventoryContainer container in containers) container.ReleaseTransaction(this);
            released = true;
        }

        internal static PublicationScope DeferPublication() => new PublicationScope();
        internal static void RunPostCommit(Action action, string message)
        { try { action?.Invoke(); } catch (Exception exception) { LogPostCommitFailure(message, exception); } }
        internal static void LogPostCommitFailure(string message, Exception exception)
        { BackpackLog.Warn(LogModule.Inventory, message + "：" + exception.Message); }

        internal sealed class PublicationScope : IDisposable
        {
            private readonly PublicationScope previous;
            private readonly List<Action> publications = new List<Action>();
            internal PublicationScope() { previous = publicationScope; publicationScope = this; }
            internal void Add(Action action) => publications.Add(action);
            public void Dispose()
            {
                publicationScope = previous;
                foreach (Action action in publications)
                    if (previous != null) previous.Add(action); else RunPostCommit(action, "库存提交后发布失败");
            }
        }

        private readonly struct ItemState
        {
            internal readonly int Count;
            internal readonly object Owner;
            internal ItemState(int count, object owner) { Count = count; Owner = owner; }
        }
        private readonly struct PlacementState
        {
            internal readonly InventoryContainer Container;
            internal readonly RuntimeItem Item;
            internal readonly InventoryPlacement Placement;
            internal PlacementState(InventoryContainer container, RuntimeItem item, InventoryPlacement placement)
            { Container = container; Item = item; Placement = placement; }
        }
    }

    public sealed partial class InventoryContainer
    {
        private InventoryTransactions activeTransaction;
        private readonly HashSet<RuntimeItem> committedItemNotifications = new HashSet<RuntimeItem>();
        internal IInventoryCommitParticipant CommitParticipant { get; set; }
        internal bool TryAcquireTransaction(InventoryTransactions transaction)
        { if (activeTransaction != null) return false; activeTransaction = transaction; return true; }
        internal void ReleaseTransaction(InventoryTransactions transaction)
        { if (ReferenceEquals(activeTransaction, transaction)) activeTransaction = null; }
        internal ItemId[] CapturePendingViewChanges()
        { var result = new ItemId[pendingViewChanges.Count]; pendingViewChanges.CopyTo(result); return result; }
        internal void RestorePendingViewChanges(ItemId[] previous)
        { pendingViewChanges.Clear(); pendingViewChanges.UnionWith(previous); itemIndicesDirty = true; }
        internal void RemoveTransactionPlacement(RuntimeItem item)
        { if (placements.TryGetValue(item, out InventoryPlacement placement)) { ClearCells(placement); RemovePlacement(item); } }
        internal void RestoreTransactionPlacement(RuntimeItem item, InventoryPlacement placement)
            => CommitPlacement(item, placement.UpperLeftIndex, placement.Dimensions);
        internal void AdvanceTransactionRevision() => AdvanceRevision();
        private void PublishCommittedItemState(RuntimeItem item)
        {
            committedItemNotifications.Add(item);
            try { item.PublishStateChanged(); } finally { committedItemNotifications.Remove(item); }
        }
    }
}
