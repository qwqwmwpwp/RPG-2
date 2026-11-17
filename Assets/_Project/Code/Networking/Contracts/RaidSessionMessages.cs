using Mirror;
namespace RPG4.Networking.Contracts
{
    public enum LobbyRequestKind : byte { Load = 0, StartRaid = 2, AbandonRaid = 3, CancelEntry = 4 }
    public enum RaidClientPhase : byte { Lobby, Loading, Active, Settling, Returned, Rejected }
    public struct LobbyRequestMessage : NetworkMessage
    {
        public byte Kind;
        public string OperationId, RaidId;
    }
    public struct RaidSessionMessage : NetworkMessage
    {
        public byte Phase;
        public string RaidId, WorldRunId, Text;
    }
}
