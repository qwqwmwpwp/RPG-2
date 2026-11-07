using RPG4.Raid.Domain;

namespace RPG4.Raid.Application
{
    public interface ILobbyInventoryExecutor
    {
        PersistentContainerData CreateInitialStash();
        LobbyInventoryCandidate Execute(PersistentInventoryState inventory, LobbyCommand command);
    }

    /// <summary>All methods run synchronously on the application's Tick/caller thread.</summary>
    public interface IRaidWorld
    {
        bool CanPrepare(string sessionKey);
        bool HasActor(string sessionKey);
        bool CanExtract(string sessionKey);
        bool TryInitializePlayer(string sessionKey, RaidReservation reservation);
        void PublishPlayer(string sessionKey);
        FrozenRaidInventory FreezePlayer(string sessionKey);
        void ClearSource(string sessionKey);
        void PublishDropBag(string sessionKey, PersistentDropBag bag, RaidPosition position);
        void ReturnToLobby(string sessionKey);
        void ReleasePlayer(string sessionKey);
    }

    public interface IRaidClientChannel
    {
        bool IsCurrent(string sessionKey);
        void SendSnapshot(string sessionKey, PersistentInventoryState inventory, LobbyCommand command, LobbyInventoryResult result);
        void SendPhase(string sessionKey, RaidPhase phase, string raidId, string worldRunId, string text);
        void Forget(string sessionKey);
        void CompletionFailed();
    }
}
