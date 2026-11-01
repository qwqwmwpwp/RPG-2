using System;
using System.Text;

namespace RPG4.Networking.Contracts
{
    public static class EntryProtocol
    {
        public const uint Version = 1;
        public const string Name = "rpg-entry-v1";
        public const string MapId = "online_game";
        public const string GameplayScene = "Assets/Scenes/OnlineGame.unity";
        public const int MaxMessageBytes = 2048; // Includes Mirror's two-byte message ID.
        public const int MessageIdBytes = 2;
        public const int HashLength = 64, RequestIdLength = 32, SessionIdLength = 32;
        public const int MaxServerIdBytes = 64, MaxUsernameBytes = 32;
        public const int MaxPasswordBytes = 256, MaxDisplayNameBytes = 64, MaxMapIdBytes = 32;
        public const double ServerAuthenticationTimeoutSeconds = 10;
        public const double ClientAuthenticationTimeoutSeconds = 15;
        internal static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }

        public static bool IsAscii(string value, int maxBytes, bool allowEmpty = false)
        {
            if (value == null || value.Length > maxBytes || (!allowEmpty && value.Length == 0)) return false;
            foreach (char c in value) if (c > 127) return false;
            return true;
        }

        public static bool IsUsername(string value)
        {
            if (value == null || value.Length < 3 || value.Length > MaxUsernameBytes) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z')
                    && !(c >= '0' && c <= '9') && c != '_') return false;
            return true;
        }

        public static bool IsUtf8(string value, int minBytes, int maxBytes)
        {
            // UTF-16 length is a cheap bound before the encoder examines untrusted input.
            if (value == null || value.Length > maxBytes) return false;
            try
            {
                int count = StrictUtf8.GetByteCount(value);
                return count >= minBytes && count <= maxBytes;
            }
            catch (EncoderFallbackException) { return false; }
        }

        public static bool IsKnownCode(LoginResultCode code) => (ushort)code <= (ushort)LoginResultCode.InternalError;

        public static bool IsValidRequest(LoginRequestMessage value) =>
            IsLowerHex(value.contentHash, HashLength) && IsAscii(value.serverId, MaxServerIdBytes)
            && IsLowerHex(value.requestId, RequestIdLength) && IsUsername(value.username)
            && IsUtf8(value.password, 1, MaxPasswordBytes);

        public static bool IsValidResponse(LoginResponseMessage value)
        {
            if (!IsLowerHex(value.requestId, RequestIdLength) || !IsKnownCode((LoginResultCode)value.code)
                || !IsLowerHex(value.contentHash, HashLength) || value.retryAfterSeconds < 0
                || !IsUtf8(value.displayName, 0, MaxDisplayNameBytes)
                || !IsAscii(value.mapId, MaxMapIdBytes, true)) return false;
            if (value.code == (ushort)LoginResultCode.Success)
                return value.protocolVersion == Version && value.retryAfterSeconds == 0
                    && value.accountId != 0 && value.characterId != 0
                    && value.mapId == MapId && IsLowerHex(value.sessionId, SessionIdLength);
            return value.accountId == 0 && value.characterId == 0 && value.displayName == string.Empty
                && value.mapId == string.Empty && value.sessionId == string.Empty;
        }

        public static LoginResponseMessage Failure(string requestId, LoginResultCode code,
            string serverContentHash, int retryAfterSeconds = 0)
        {
            if (code == LoginResultCode.Success || !IsKnownCode(code) || retryAfterSeconds < 0)
                throw new ArgumentException("Invalid authentication failure result.");
            return new LoginResponseMessage
            {
                requestId = requestId, code = (ushort)code, displayName = string.Empty,
                protocolVersion = Version, contentHash = serverContentHash, mapId = string.Empty,
                sessionId = string.Empty, retryAfterSeconds = retryAfterSeconds
            };
        }
    }
}
