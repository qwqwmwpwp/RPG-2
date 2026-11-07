using System;
using System.Collections.Generic;
using Mirror;
using RPG4.Networking.Contracts;
using RPG4.Backpack.Network;
using RPG4.Raid.Application;
using RPG4.Raid.Domain;
using UnityEngine;

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
            public void SendSnapshot(string key, PersistentInventoryState state, LobbyCommand request, LobbyInventoryResult result)
            {
                Entry entry = Find(key);
                NetworkConnectionToClient connection = entry?.Connection;
                if (connection == null || entry.Disconnected || !NetworkServer.connections.TryGetValue(connection.connectionId, out NetworkConnectionToClient current)
                    || !ReferenceEquals(current, connection)) return;
                byte[] bytes = RaidInventoryWireCodec.Encode(state); string transfer = Guid.NewGuid().ToString("N");
                int count = Math.Max(1, (bytes.Length + RaidInventoryWireCodec.ChunkBytes - 1) / RaidInventoryWireCodec.ChunkBytes);
                for (int i = 0; i < count; i++)
                {
                    int length = Math.Min(RaidInventoryWireCodec.ChunkBytes, bytes.Length - i * RaidInventoryWireCodec.ChunkBytes);
                    var chunk = new byte[length]; Buffer.BlockCopy(bytes, i * RaidInventoryWireCodec.ChunkBytes, chunk, 0, length);
                    connection.Send(new LobbySnapshotMessage { TransferId = transfer, OperationId = request.OperationId, Index = i, Count = count,
                        RequestId = request.RequestId, Source = request.Source, Success = result.Success, Error = result.Error,
                        ResultItemId = result.ResultItemId, MovedCount = result.MovedCount, Payload = chunk,
                        AssemblyKind = result.AssemblyKind, AssemblyPayloadPending = result.AssemblyPayload != null });
                }
                byte[] assembly = result.AssemblyPayload;
                if (assembly != null && assembly.Length > 0 && assembly.Length <= AssemblyWireCodec.MaxBytes) {
                    int assemblyCount = (assembly.Length + AssemblyWireCodec.ChunkBytes - 1) / AssemblyWireCodec.ChunkBytes;
                    for (int i = 0; i < assemblyCount; i++) {
                        int length = Math.Min(AssemblyWireCodec.ChunkBytes, assembly.Length - i * AssemblyWireCodec.ChunkBytes);
                        var chunk = new byte[length]; Buffer.BlockCopy(assembly, i * AssemblyWireCodec.ChunkBytes, chunk, 0, length);
                        connection.Send(new LobbyAssemblyResponseMessage { OperationId = request.OperationId, Source = request.Source,
                            Kind = result.AssemblyKind, SessionId = request.AssemblySessionId, RequestId = request.RequestId,
                            RootItemId = request.RootItemId, ParentItemId = request.ParentItemId, SlotId = request.SlotId,
                            CandidateOffset = request.CandidateOffset, Index = i, Count = assemblyCount, Payload = chunk });
                    }
                }
            }
            public void SendPhase(string key, RaidPhase phase, string raidId, string worldRunId, string text)
            {
                if (!IsCurrent(key)) return;
                if (phase == RaidPhase.Loading) owner.NotifyRaidPlayerLoading(key);
                else if (phase == RaidPhase.Active) owner.NotifyRaidPlayerActive(key);
                Find(key).Connection.Send(new RaidSessionMessage { Phase = (byte)phase, RaidId = raidId, WorldRunId = worldRunId, Text = text });
            }
            public void Forget(string key)
            {
                Entry entry = Find(key);
                if (entry == null) return;
                entries.Remove(key);
                if (connections.TryGetValue(entry.Connection, out Entry current) && ReferenceEquals(current, entry)) connections.Remove(entry.Connection);
            }
            public void CompletionFailed() => Debug.LogError("A persistent raid completion failed.", owner);
            internal void Clear() { entries.Clear(); connections.Clear(); }
            internal static LobbyCommand ToCommand(LobbyRequestMessage request) => new LobbyCommand {
                Kind = (LobbyCommandKind)request.Kind, Source = request.Source, Destination = request.Destination,
                OperationKind = request.OperationKind, OperationId = request.OperationId, RaidId = request.RaidId, Revision = request.Revision,
                RequestId = request.RequestId, Count = request.Count, UpperLeftIndex = request.UpperLeftIndex,
                SourceItemId = request.SourceItemId, TargetItemId = request.TargetItemId,
                RootItemId = request.RootItemId, ParentItemId = request.ParentItemId, CandidateItemId = request.CandidateItemId,
                SlotId = request.SlotId, AssemblySessionId = request.AssemblySessionId, StructureRevision = request.StructureRevision,
                CandidateOffset = request.CandidateOffset, CandidateLimit = request.CandidateLimit };
        }
    }
}
