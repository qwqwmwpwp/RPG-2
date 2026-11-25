using System;

namespace RPG4.LoadoutTemplate
{
    public sealed partial class RoleLoadoutController
    {
        private LoadoutCommitCoordinator commitCoordinator;
        internal LoadoutCommitCoordinator CommitCoordinator => commitCoordinator
            ?? (commitCoordinator = new LoadoutCommitCoordinator(this));

        public event Action<LoadoutCommitRecord> Committed
        {
            add { CommitCoordinator.Committed += value; }
            remove { CommitCoordinator.Committed -= value; }
        }
        public bool AffectsEquippedWeapon(LoadoutCommitRecord commit)
        {
            if (commit.SelectedWeaponChanged || !commit.DetailsComplete || commit.Before.Epoch != commit.After.Epoch
                || commit.RuntimeItemsChanged && commit.Changes.Count == 0) return true;
            foreach (var slot in WeaponFallbackOrder)
            {
                string endpoint = GetSlot(slot)?.Snapshot.Id;
                foreach (LoadoutEndpointChange change in commit.Changes)
                    if (change.EndpointId == endpoint) return true;
            }
            return false;
        }

        public bool IsPublishingCommit => CommitCoordinator.IsPublishing;

        internal LoadoutOperationKey CreateOperationKey(LoadoutOperationChannel channel, int requestId = 0,
            string session = null)
        {
            NetworkRoleLoadout network = Network;
            return network != null ? network.CreateOperationKey(channel, requestId)
                : new LoadoutOperationKey(0, CommitCoordinator.Epoch, session ?? CommitCoordinator.Epoch, channel, requestId);
        }

        internal LoadoutStateVersion CaptureStateVersion(LootSource source = null) => CommitCoordinator.Capture(source);
        internal void QueueCommitted(LoadoutCommitRecord commit, Action publishEndpoints) => CommitCoordinator.Queue(commit, publishEndpoints);
        internal void FlushCommitted() => CommitCoordinator.Flush();

        private void PublishRuntimeTransaction(LoadoutCommitRecord commit, Action publishEndpoints)
        {
            QueueCommitted(commit, publishEndpoints);
            NetworkRoleLoadout network = Network;
            if (network != null)
            {
                try { network.CommitRuntimeChanges(); }
                finally { network.PublishRuntimeCommit(); }
            }
            else FlushCommitted();
        }
    }
}
