using Mirror;

namespace RPG4.Networking.Contracts
{
    public enum LobbyRequestKind : byte { Load, InventoryOperation, StartRaid, AbandonRaid, CancelEntry, AssemblyQuery, AssemblyOperation }
    public enum RaidClientPhase : byte { Lobby, Loading, Active, Settling, Returned, Rejected }

    public struct LobbyRequestMessage : NetworkMessage
    {
        public byte Kind, Source, Destination, OperationKind;
        public string OperationId, RaidId;
        public long Revision;
        public int RequestId, Count, UpperLeftIndex;
        public ulong SourceItemId, TargetItemId;
        public ulong RootItemId, ParentItemId, CandidateItemId;
        public string SlotId;
        public int AssemblySessionId, StructureRevision, CandidateOffset, CandidateLimit;
    }

    public struct LobbySnapshotMessage : NetworkMessage
    {
        public string TransferId, OperationId;
        public int Index, Count, RequestId;
        public byte Source;
        public bool Success;
        public ulong ResultItemId;
        public int MovedCount;
        public string Error;
        public byte[] Payload;
        public byte AssemblyKind;
        public bool AssemblyPayloadPending;

    }

    public struct LobbyAssemblyResponseMessage : NetworkMessage
    {
        public string OperationId, SlotId;
        public byte Source, Kind;
        public int SessionId, RequestId, CandidateOffset, Index, Count;
        public ulong RootItemId, ParentItemId;
        public byte[] Payload;
    }

    public struct RaidSessionMessage : NetworkMessage
    {
        public byte Phase;
        public string RaidId, WorldRunId, Text;
    }
}
