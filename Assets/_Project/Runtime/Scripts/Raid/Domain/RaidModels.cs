using System;
using System.Collections.Generic;

namespace RPG4.Raid.Domain
{
    /// <summary>Trusted business identity mapped by the authenticated transport adapter.</summary>
    public sealed class RaidIdentity
    {
        public ulong AccountId { get; }
        public ulong CharacterId { get; }
        public string SessionId { get; }
        public RaidIdentity(ulong accountId, ulong characterId, string sessionId)
        {
            if (accountId == 0 || characterId == 0 || string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Invalid raid identity.");
            AccountId = accountId; CharacterId = characterId; SessionId = sessionId;
        }
    }

    public enum RaidStage { Prepared, Initializing, Activating, Active, Settling }
    public enum LobbyCommandKind : byte { Load, InventoryOperation, StartRaid, AbandonRaid, CancelEntry }
    public enum LobbyInventoryOperationKind : byte { MergeStack, SwapStackCounts, SplitStack, TransferItem, MoveItem, SwapItems }
    public enum RaidPhase : byte { Lobby, Loading, Active, Settling, Returned, Rejected }

    public sealed class LobbyCommand
    {
        public LobbyCommandKind Kind;
        public byte Source, Destination, OperationKind;
        public string OperationId, RaidId;
        public long Revision;
        public int RequestId, Count, UpperLeftIndex;
        public ulong SourceItemId, TargetItemId;
        public bool SamePayload(LobbyCommand other) => other != null && Kind == other.Kind
            && Source == other.Source && Destination == other.Destination && OperationKind == other.OperationKind
            && Revision == other.Revision && SourceItemId == other.SourceItemId && TargetItemId == other.TargetItemId
            && Count == other.Count && UpperLeftIndex == other.UpperLeftIndex && RequestId == other.RequestId;
    }

    public sealed class LobbyInventoryResult
    {
        public int RequestId;
        public bool Success;
        public ulong ResultItemId;
        public int MovedCount;
        public string Error;
        public static LobbyInventoryResult Accepted(int id) => new LobbyInventoryResult { RequestId = id, Success = true };
        public static LobbyInventoryResult Rejected(int id, string error) => new LobbyInventoryResult { RequestId = id, Error = error };
    }

    public sealed class LobbyInventoryCandidate
    {
        public LobbyInventoryResult Result;
        public PersistentContainerData Stash, Backpack, Equipment;
    }

    public readonly struct RaidPosition
    {
        public readonly float X, Y, Z;
        public RaidPosition(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public sealed class FrozenRaidInventory
    {
        public PersistentContainerData Backpack, Equipment;
        public RaidPosition Position;
    }

    public sealed class RaidSettlementIntent
    {
        public string OperationId { get; }
        public string BagId { get; }
        public RaidOutcome Outcome { get; }
        public FrozenRaidInventory Frozen { get; private set; }
        public RaidSettlementIntent(RaidOutcome outcome)
        {
            Outcome = outcome; OperationId = Guid.NewGuid().ToString("N");
            BagId = outcome == RaidOutcome.Extracted ? null : Guid.NewGuid().ToString("N");
        }
        public void Capture(FrozenRaidInventory inventory)
        {
            if (Frozen != null || inventory == null || inventory.Backpack == null || inventory.Equipment == null)
                throw new InvalidOperationException("A settlement captures its inventory exactly once.");
            Frozen = inventory;
        }
    }

    public sealed class RaidRun
    {
        public RaidReservation Reservation { get; private set; }
        public RaidStage Stage { get; private set; }
        public bool ReadyRequested;
        public double Deadline, RetryAt;
        public RaidSettlementIntent Settlement { get; private set; }
        public bool ActivationRequested => Stage == RaidStage.Activating || Stage == RaidStage.Active || Stage == RaidStage.Settling;
        public bool IsSpawning => Stage == RaidStage.Initializing || Stage == RaidStage.Activating;
        public RaidRun(RaidReservation reservation, double deadline)
        { Reservation = reservation ?? throw new ArgumentNullException(nameof(reservation)); Stage = RaidStage.Prepared; Deadline = deadline; }
        public bool BeginInitialization()
        {
            if (Stage != RaidStage.Prepared || Reservation.Status != PersistentRaidStatus.Prepared) return false;
            Stage = RaidStage.Initializing; return true;
        }
        public void InitializationFailed() { if (Stage == RaidStage.Initializing) Stage = RaidStage.Prepared; }
        public void BeginActivation()
        {
            if (Stage != RaidStage.Initializing) throw new InvalidOperationException("The player is not initialized.");
            Stage = RaidStage.Activating;
        }
        public void ActivationCommitted(RaidReservation reservation)
        {
            if (Stage != RaidStage.Activating || reservation.Status != PersistentRaidStatus.Active)
                throw new InvalidOperationException("Invalid raid activation.");
            Reservation = reservation;
        }
        public void Published(double deadline)
        {
            if (Stage != RaidStage.Activating || Reservation.Status != PersistentRaidStatus.Active)
                throw new InvalidOperationException("Only an active raid can be published.");
            Stage = RaidStage.Active; Deadline = deadline;
        }
        public bool BeginSettlement(RaidOutcome outcome)
        {
            if (Stage == RaidStage.Settling || Reservation.Status != PersistentRaidStatus.Active) return false;
            Stage = RaidStage.Settling; Settlement = new RaidSettlementIntent(outcome); return true;
        }
    }

    public enum PendingLobbyOperationKind { Commit, Prepare }
    public sealed class PendingLobbyOperation
    {
        public PendingLobbyOperationKind Kind;
        public LobbyCommand Command;
        public LobbyInventoryCandidate Candidate;
        public double RetryAt;
    }

    public sealed class LobbyReply
    {
        public PersistentInventoryState Inventory;
        public LobbyCommand Command;
        public LobbyInventoryResult Result;
    }

    public sealed class RaidPlayerSession
    {
        public string Key { get; }
        public RaidIdentity Identity { get; }
        public bool Connected = true;
        public bool Busy;
        public PersistentInventoryState Inventory;
        public RaidRun Raid;
        public PendingLobbyOperation Pending;
        public readonly Dictionary<string, LobbyReply> Replies = new Dictionary<string, LobbyReply>();
        public readonly Queue<string> ReplyOrder = new Queue<string>();
        public RaidPlayerSession(string key, RaidIdentity identity) { Key = key; Identity = identity; }
    }
}
