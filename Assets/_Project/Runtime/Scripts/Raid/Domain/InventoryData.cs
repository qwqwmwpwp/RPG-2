using System;



namespace RPG4.Raid.Domain
{
    public enum PersonalContainerKind : byte { Stash = 0, Backpack = 1, Equipment = 2 }
    public enum PersistentRaidStatus : byte { Prepared = 0, Active = 1, Extracted = 2, Failed = 3, Cancelled = 4 }
    public enum RaidOutcome : byte { Extracted = 0, Death = 1, Abandoned = 2, Timeout = 3, Disconnected = 4, ServerRecovery = 5 }

    /// <summary>Trusted server snapshot. Payload is RuntimeItemSnapshotCodec's full tree; position is separate.</summary>
    [Serializable]
    public sealed class PersistentItemData
    {
        public ulong ItemId;
        public string ItemType;
        public int StackCount;
        public int UpperLeftIndex;
        public byte[] Payload;
    }

    [Serializable]
    public sealed class PersistentContainerData
    {
        public PersonalContainerKind Kind;
        public int Rows;
        public int Columns;
        public PersistentItemData[] Items = Array.Empty<PersistentItemData>();
    }

    [Serializable]
    public sealed class PersistentInventoryState
    {
        public ulong AccountId;
        public ulong CharacterId;
        public long Revision;
        public string ActiveRaidId;
        public bool ReadOnly;
        public PersistentContainerData Stash;
        public PersistentContainerData Backpack;
        public PersistentContainerData Equipment;
    }

    [Serializable]
    public sealed class RaidReservation
    {
        public string RaidId;
        public string WorldRunId;
        public ulong AccountId;
        public ulong CharacterId;
        public long Revision;
        public PersistentRaidStatus Status;
        public PersistentContainerData Backpack;
        public PersistentContainerData Equipment;
    }

    [Serializable]
    public sealed class PersistentDropBag
    {
        public string BagId;
        public string RaidId;
        public string WorldRunId;
        public bool Published;
        public PersistentContainerData Backpack;
        public PersistentContainerData Equipment;
    }

    public sealed class RaidSettlementResult
    {
        public PersistentInventoryState Inventory;
        public PersistentDropBag DropBag;
        public PersistentRaidStatus Status;
    }

    public readonly struct PersistentItemIdRange
    {
        public readonly ulong First, Last;
        public PersistentItemIdRange(ulong first, ulong last) { First = first; Last = last; }
    }

    public sealed class InventoryRaidException : Exception
    {
        public string Code { get; }
        public InventoryRaidException(string code) : base(code) { Code = code; }
    }
}