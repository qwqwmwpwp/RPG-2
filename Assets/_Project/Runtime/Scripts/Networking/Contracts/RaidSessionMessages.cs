using Mirror;

namespace RPG4.Networking.Contracts
{
    public enum LobbyRequestKind : byte { Load, InventoryOperation, StartRaid, AbandonRaid, CancelEntry }
    public enum RaidClientPhase : byte { Lobby, Loading, Active, Settling, Returned, Rejected }

    public struct LobbyRequestMessage : NetworkMessage
    {
        public byte Kind, Source, Destination, OperationKind;
        public string OperationId, RaidId;
        public long Revision;
        public int RequestId, Count, UpperLeftIndex;
        public ulong SourceItemId, TargetItemId;
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
    }

    public struct RaidSessionMessage : NetworkMessage
    {
        public byte Phase;
        public string RaidId, WorldRunId, Text;
    }
}
