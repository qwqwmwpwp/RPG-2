using System;
using System.Collections.Generic;
using Mirror;
using RPG4.Networking.Contracts;
using RPG4.Raid.Application;
using RPG4.Raid.Domain;

namespace RPG4.Networking
{
    public sealed partial class AuthoritativeNetworkManager
    {
        private sealed class ServerRaidConnections : IRaidClientChannel
        {
            internal sealed class Entry
            {
                internal string Key;
                internal NetworkConnectionToClient Connection;
                internal AuthenticatedIdentity Identity;
                internal bool Disconnected;
            }
            private readonly AuthoritativeNetworkManager owner;
            private readonly Dictionary<NetworkConnectionToClient, Entry> connections = new Dictionary<NetworkConnectionToClient, Entry>();
            private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
            internal ServerRaidConnections(AuthoritativeNetworkManager owner) { this.owner = owner; }
            internal Entry FindOrCreate(NetworkConnectionToClient connection)
            {
                if (connection == null || !connection.isAuthenticated || !(connection.authenticationData is AuthenticatedIdentity identity)
                    || !(owner.authenticator is AccountAuthenticator account) || !account.IsAuthenticatedSessionCurrent(connection)) return null;
                if (connections.TryGetValue(connection, out Entry existing))
                    return ReferenceEquals(existing.Identity, identity) && !existing.Disconnected ? existing : null;
                // Authentication creates a fresh session id on reconnect. Reclaim the
                // disconnected character entry so its raid/application key survives while
                // the player is still alive and the raid has not settled.
                foreach (Entry disconnected in entries.Values)
                    if (disconnected.Disconnected && owner.raidApplication != null && owner.raidApplication.CanReconnect(disconnected.Key)
                        && disconnected.Identity.AccountId == identity.AccountId
                        && disconnected.Identity.CharacterId == identity.CharacterId)
                    {
                        if (disconnected.Connection != null) connections.Remove(disconnected.Connection);
                        disconnected.Connection = connection;
                        disconnected.Identity = identity;
                        disconnected.Disconnected = false;
                        connections[connection] = disconnected;
                        return disconnected;
                    }
                var entry = new Entry { Key = Guid.NewGuid().ToString("N"), Connection = connection, Identity = identity };
                connections.Add(connection, entry); entries.Add(entry.Key, entry);
                return entry;
            }
            internal Entry Find(NetworkConnectionToClient connection)
                => connection != null && connections.TryGetValue(connection, out Entry entry) ? entry : null;
            internal Entry Find(string key) => key != null && entries.TryGetValue(key, out Entry entry) ? entry : null;
            public bool IsCurrent(string key)
            {
                Entry entry = Find(key);
                return entry != null && !entry.Disconnected && singleton == owner && NetworkServer.active
                    && entry.Connection != null && connections.TryGetValue(entry.Connection, out Entry current) && ReferenceEquals(current, entry)
                    && entry.Connection.isAuthenticated && ReferenceEquals(entry.Connection.authenticationData, entry.Identity)
                    && owner.authenticator is AccountAuthenticator account && account.IsAuthenticatedSessionCurrent(entry.Connection);
            }
            public void SendPhase(string key, RaidPhase phase, string raidId, string worldRunId, string text)
            {
                if (!IsCurrent(key)) return;
                Find(key).Connection.Send(new RaidSessionMessage { Phase = (byte)phase, RaidId = raidId, WorldRunId = worldRunId, Text = text });
            }
            public void Forget(string key)
            {
                Entry entry = Find(key);
                if (entry == null) return;
                entries.Remove(key);
                if (connections.TryGetValue(entry.Connection, out Entry current) && ReferenceEquals(current, entry)) connections.Remove(entry.Connection);
            }
            internal void Clear() { entries.Clear(); connections.Clear(); }
            internal static LobbyCommand ToCommand(LobbyRequestMessage request) => new LobbyCommand {
                Kind = (LobbyCommandKind)request.Kind, OperationId = request.OperationId, RaidId = request.RaidId };
        }
    }
}
