using System;
using System.IO;
using System.Text;
using Mirror;

namespace RPG4.Networking.Contracts
{
    /// <summary>Whole-message codecs. Mirror supplies one unbatched message to each reader.
    /// Never include reader.ToString(), credentials, or an inner exception in a parse failure.</summary>
    public static class LoginMessageSerialization
    {
        /// <summary>Explicit installation also protects runtime registration from generated serializer ordering.</summary>
        public static void Register()
        {
            Reader<LoginRequestMessage>.read = ReadLoginRequestMessage;
            Writer<LoginRequestMessage>.write = WriteLoginRequestMessage;
            Reader<LoginResponseMessage>.read = ReadLoginResponseMessage;
            Writer<LoginResponseMessage>.write = WriteLoginResponseMessage;
        }
        private static InvalidDataException Invalid() => new InvalidDataException("Invalid entry authentication message.");
        private static void Require(NetworkReader reader, int bytes)
        {
            if (bytes < 0 || reader.Remaining < bytes) throw Invalid();
        }
        private static void Begin(NetworkReader reader)
        {
            if (reader.Remaining > EntryProtocol.MaxMessageBytes - EntryProtocol.MessageIdBytes) throw Invalid();
        }
        private static string ReadBoundedString(NetworkReader reader, int maxBytes)
        {
            Require(reader, 2);
            int encodedSize = reader.ReadUShort();
            if (encodedSize == 0) throw Invalid(); // This protocol never uses null strings.
            int size = encodedSize - 1;
            if (size > maxBytes) throw Invalid();
            Require(reader, size);
            ArraySegment<byte> bytes = reader.ReadBytesSegment(size);
            try { return EntryProtocol.StrictUtf8.GetString(bytes.Array, bytes.Offset, bytes.Count); }
            catch (DecoderFallbackException) { throw Invalid(); }
        }

        public static LoginRequestMessage ReadLoginRequestMessage(this NetworkReader reader)
        {
            Begin(reader);
            Require(reader, 4);
            var value = new LoginRequestMessage
            {
                protocolVersion = reader.ReadUInt(),
                contentHash = ReadBoundedString(reader, EntryProtocol.HashLength),
                serverId = ReadBoundedString(reader, EntryProtocol.MaxServerIdBytes),
                requestId = ReadBoundedString(reader, EntryProtocol.RequestIdLength),
                username = ReadBoundedString(reader, EntryProtocol.MaxUsernameBytes),
                password = ReadBoundedString(reader, EntryProtocol.MaxPasswordBytes)
            };
            if (reader.Remaining != 0 || !EntryProtocol.IsValidRequest(value)) throw Invalid();
            return value;
        }

        public static LoginResponseMessage ReadLoginResponseMessage(this NetworkReader reader)
        {
            Begin(reader);
            var value = new LoginResponseMessage { requestId = ReadBoundedString(reader, EntryProtocol.RequestIdLength) };
            Require(reader, 18);
            value.code = reader.ReadUShort();
            value.accountId = reader.ReadULong();
            value.characterId = reader.ReadULong();
            value.displayName = ReadBoundedString(reader, EntryProtocol.MaxDisplayNameBytes);
            Require(reader, 4);
            value.protocolVersion = reader.ReadUInt();
            value.contentHash = ReadBoundedString(reader, EntryProtocol.HashLength);
            value.mapId = ReadBoundedString(reader, EntryProtocol.MaxMapIdBytes);
            value.sessionId = ReadBoundedString(reader, EntryProtocol.SessionIdLength);
            Require(reader, 4);
            value.retryAfterSeconds = reader.ReadInt();
            if (reader.Remaining != 0 || !EntryProtocol.IsValidResponse(value)) throw Invalid();
            return value;
        }

        public static void WriteLoginRequestMessage(this NetworkWriter writer, LoginRequestMessage value)
        {
            if (!EntryProtocol.IsValidRequest(value)) throw Invalid();
            int size = 4 + StringSize(value.contentHash) + StringSize(value.serverId)
                + StringSize(value.requestId) + StringSize(value.username) + StringSize(value.password);
            CheckSize(size);
            writer.WriteUInt(value.protocolVersion);
            writer.WriteString(value.contentHash); writer.WriteString(value.serverId);
            writer.WriteString(value.requestId); writer.WriteString(value.username); writer.WriteString(value.password);
        }

        public static void WriteLoginResponseMessage(this NetworkWriter writer, LoginResponseMessage value)
        {
            if (!EntryProtocol.IsValidResponse(value)) throw Invalid();
            int size = 26 + StringSize(value.requestId) + StringSize(value.displayName)
                + StringSize(value.contentHash) + StringSize(value.mapId) + StringSize(value.sessionId);
            CheckSize(size);
            writer.WriteString(value.requestId); writer.WriteUShort(value.code);
            writer.WriteULong(value.accountId); writer.WriteULong(value.characterId); writer.WriteString(value.displayName);
            writer.WriteUInt(value.protocolVersion); writer.WriteString(value.contentHash);
            writer.WriteString(value.mapId); writer.WriteString(value.sessionId); writer.WriteInt(value.retryAfterSeconds);
        }
        private static int StringSize(string value) => 2 + EntryProtocol.StrictUtf8.GetByteCount(value);
        private static void CheckSize(int bytes)
        {
            if (bytes + EntryProtocol.MessageIdBytes > EntryProtocol.MaxMessageBytes) throw Invalid();
        }
    }
}
