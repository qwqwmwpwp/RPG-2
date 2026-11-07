using Mirror;

namespace RPG4.Networking.Contracts
{
    public enum LoginResultCode : ushort
    {
        Success = 0, InvalidCredentials = 1, AccountDisabled = 2, AccountInUse = 3,
        VersionMismatch = 4, ContentMismatch = 5, ServerNotReady = 6, ServerFull = 7,
        RateLimited = 8, AuthTimeout = 9, DatabaseUnavailable = 10,
        ProfileUnavailable = 11, InvalidRequest = 12, InternalError = 13
    }

    // Wire order is explicit in LoginMessageSerialization, shared by both builds.
    public struct LoginRequestMessage : NetworkMessage
    {
        public uint protocolVersion;
        public string contentHash;
        public string serverId;
        public string requestId;
        public string username;
        public string password;
    }

    public struct LoginResponseMessage : NetworkMessage
    {
        public string requestId;
        public ushort code;
        public ulong accountId;
        public ulong characterId;
        public string displayName;
        public uint protocolVersion;
        public string contentHash;
        public string mapId;
        public string sessionId;
        public int retryAfterSeconds;
    }
}
