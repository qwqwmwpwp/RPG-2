using System.Threading;
using System.Threading.Tasks;
using RPG4.Raid.Domain;

namespace RPG4.Raid.Application
{
    /// <summary>All identity arguments MUST come from a current authenticated connection, never client account fields.
    /// Snapshots are captured and inventory rules executed on Unity's main thread BEFORE calling this service.
    /// The service accesses DTOs/database only. A successful task means the durable transaction committed.
    /// Recovery/settlement retain gates independently of the transient login lease.</summary>
    public interface IServerInventoryRaidService
    {
        string WorldRunId { get; }
        Task<PersistentItemIdRange> InitializeAsync(CancellationToken token);
        // initialStash is a fresh, server-created, 30-row x 10-column grant, applied once for this account.
        Task<PersistentInventoryState> LoadAsync(RaidIdentity identity, PersistentContainerData initialStash, CancellationToken token);
        // After executing authoritative inventory rules; operationId is stable across retries.
        Task<PersistentInventoryState> CommitInventoryAsync(RaidIdentity identity, string operationId, long expectedRevision,
            PersistentContainerData stash, PersistentContainerData backpack, PersistentContainerData equipment, CancellationToken token);
        Task<RaidReservation> PrepareRaidAsync(RaidIdentity identity, string operationId, long expectedRevision, CancellationToken token);
        // Call only after restoring the player successfully, BEFORE AddPlayerForConnection; no cancellation after Active.
        Task<RaidReservation> ActivateRaidAsync(RaidIdentity identity, string raidId, CancellationToken token);
        Task<PersistentInventoryState> CancelPreparedRaidAsync(RaidIdentity identity, string raidId, CancellationToken token);
        // For failure/disconnect the supplied snapshot is the frozen CURRENT world inventory, not its entry copy.
        Task<RaidSettlementResult> SettleRaidAsync(RaidIdentity identity, string raidId, string operationId,
            RaidOutcome outcome, string bagId, PersistentContainerData backpack, PersistentContainerData equipment, CancellationToken token);
        Task<PersistentDropBag[]> GetPendingDropBagsAsync(CancellationToken token);
        Task MarkDropBagPublishedAsync(string raidId, string bagId, CancellationToken token);
    }

}
