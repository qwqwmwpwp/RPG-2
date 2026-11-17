namespace RPG4.Raid.Domain
{
    public enum RaidStatus : byte { Prepared = 0, Active = 1, Extracted = 2, Failed = 3, Cancelled = 4 }
    public enum RaidOutcome : byte { Extracted = 0, Death = 1, Abandoned = 2, Timeout = 3, Disconnected = 4, ServerRecovery = 5 }
    public sealed class RaidReservation
    {
        public string RaidId, WorldRunId;
        public ulong AccountId, CharacterId;
        public long Revision;
        public RaidStatus Status;
    }
}
