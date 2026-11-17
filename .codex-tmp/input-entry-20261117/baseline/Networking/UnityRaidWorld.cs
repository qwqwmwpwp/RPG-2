using System;
using System.Collections.Generic;
using Mirror;
using RPG4.LoadoutTemplate;
using RPG4.Networking.Contracts;
using RPG4.Raid.Application;
using RPG4.Raid.Domain;
using UnityEngine;

namespace RPG4.Networking
{
    public sealed partial class AuthoritativeNetworkManager
    {
        private sealed class UnityRaidWorld : IRaidWorld
        {
            private sealed class Entry
            {
                internal GameObject UnpublishedPlayer;
                internal GameObject ActivePlayer;
                internal GameplaySceneBindings Bindings;
                internal long Generation;
                internal bool LootExpired;
            }
            private readonly AuthoritativeNetworkManager owner;
            private readonly ServerRaidConnections connections;
            private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
            private readonly HashSet<string> initializing = new HashSet<string>();
            private readonly HashSet<string> publishedBags = new HashSet<string>();
            private readonly Dictionary<string, RaidLootBag> roleBags = new Dictionary<string, RaidLootBag>();
            private readonly ServerMatchRuntime match;
            internal UnityRaidWorld(AuthoritativeNetworkManager owner, ServerRaidConnections connections)
            {
                this.owner = owner; this.connections = connections;
                match = owner.matchRuntime;
                if (match != null) match.Changed += OnMatchChanged;
            }
            private void OnMatchChanged(MatchRunEvent value)
            {
                if (value.Kind != MatchRunEventKind.Completed && value.Kind != MatchRunEventKind.Cancelled) return;
                // A new round may start without stopping Mirror. Expire only this round's role loot.
                foreach (Entry entry in entries.Values) entry.LootExpired = true;
                ClearRoleBags();
            }
            private void ClearRoleBags()
            {
                foreach (RaidLootBag bag in new List<RaidLootBag>(roleBags.Values))
                {
                    if (bag == null) continue;
                    if (NetworkServer.active && bag.TryGetComponent(out NetworkIdentity identity) && identity.netId != 0)
                        NetworkServer.Destroy(bag.gameObject);
                    else Destroy(bag.gameObject);
                }
                roleBags.Clear();
            }
            private GameObject Actor(string key)
            {
                ServerRaidConnections.Entry connection = connections.Find(key);
                if (connection?.Connection?.identity != null) return connection.Connection.identity.gameObject;
                if (!entries.TryGetValue(key, out Entry entry)) return null;
                return entry.UnpublishedPlayer ?? entry.ActivePlayer;
            }
            internal string FindActorSession(GameObject actor)
            {
                if (actor == null) return null;
                foreach (var pair in entries)
                    if (pair.Value.ActivePlayer == actor || pair.Value.UnpublishedPlayer == actor) return pair.Key;
                return null;
            }
            public bool CanPrepare(string key) => owner.ValidateServerGameplayWorld(out _);
            public bool HasActor(string key) => Actor(key) != null;
            public bool CanExtract(string key)
            {
                GameObject actor = Actor(key);
                return NetworkServer.active && actor != null && actor.TryGetComponent(out NetworkPlayerController controller)
                    && controller.isActiveAndEnabled && controller.Player != null && controller.Player.IsAlive && controller.connectionToClient != null;
            }
            public bool TryInitializePlayer(string key, RaidReservation reservation)
            {
                ServerRaidConnections.Entry connection = connections.Find(key);
                if (connection == null)
                { owner.ReportGameplayFailure("Trusted player entry connection is missing."); return false; }
                if (!initializing.Add(key))
                { owner.ReportGameplayFailure("Trusted player initialization is already in progress."); return false; }
                GameObject player = null; bool succeeded = false;
                var entry = new Entry { Generation = owner.serverWorldGeneration, Bindings = owner.gameplaySceneBindings };
                entries[key] = entry;
                try
                {
                    NetworkConnectionToClient conn = connection.Connection;
                    if (!connections.IsCurrent(key))
                        throw new InvalidOperationException("The authenticated player connection is no longer current.");
                    if (conn.identity != null)
                        throw new InvalidOperationException("The player connection already owns a network player.");
                    if (!owner.ValidateServerGameplayWorld(out string reason))
                        throw new InvalidOperationException("The server gameplay world is not ready: " + reason);
                    if (!(owner.gameplaySceneBindings.TrustedSpawnConfiguration is IServerPlayerSpawnInitializer initializer))
                        throw new InvalidOperationException("The trusted pre-spawn initializer is missing.");
                    Transform spawn = owner.GetStartPosition();
                    if (spawn == null)
                        throw new InvalidOperationException("No valid player spawn point is available.");
                    if (!owner.IsCurrentPlayerEntry(conn, connection.Identity, entry.Bindings, entry.Generation))
                        throw new InvalidOperationException("The player entry became stale before initialization.");
                    player = Instantiate(owner.playerPrefab, spawn.position, spawn.rotation);
                    var context = new TrustedPlayerSpawnContext(connection.Identity, entry.Bindings, spawn, owner.ServerItemIds, entry.Generation, reservation);
                    if (!initializer.TryInitializeBeforeSpawn(player, context, out reason))
                        throw new InvalidOperationException("Trusted player initialization failed: " + (string.IsNullOrWhiteSpace(reason) ? "No failure reason was provided." : reason));
                    if (!owner.ValidateServerGameplayWorld(out reason))
                        throw new InvalidOperationException("The server gameplay world became unavailable after initialization: " + reason);
                    if (!connections.IsCurrent(key))
                        throw new InvalidOperationException("The authenticated player connection changed during initialization.");
                    if (player == null || entry.Bindings == null || player.scene != entry.Bindings.gameObject.scene)
                        throw new InvalidOperationException("The initialized player or its gameplay scene binding is invalid.");
                    if (!player.TryGetComponent(out NetworkIdentity networkIdentity) || networkIdentity.netId != 0)
                        throw new InvalidOperationException("The initialized player is missing an unpublished network identity.");
                    if (!player.TryGetComponent(out OnlinePlayerIdentity profile) || !profile.MatchesServerIdentity(connection.Identity, entry.Generation))
                        throw new InvalidOperationException("The initialized player profile does not match the authenticated entry.");
                    if (!owner.IsCurrentPlayerEntry(conn, connection.Identity, entry.Bindings, entry.Generation))
                        throw new InvalidOperationException("The player entry became stale after initialization.");
                    // This object remains unpublished until the application receives the durable Active result.
                    entry.UnpublishedPlayer = player; succeeded = true; return true;
                }
                catch (Exception exception)
                { owner.ReportGameplayFailure("Trusted player initialization or entry failed. " + exception.GetType().Name + ": " + exception.Message); return false; }
                finally
                {
                    initializing.Remove(key);
                    if (!succeeded && player != null)
                    {
                        if (player.TryGetComponent(out NetworkIdentity identity) && identity.netId != 0) NetworkServer.Destroy(player);
                        else Destroy(player);
                    }
                }
            }
            public void PublishPlayer(string key)
            {
                ServerRaidConnections.Entry connection = connections.Find(key);
                if (connection == null || !entries.TryGetValue(key, out Entry entry) || entry.UnpublishedPlayer == null || !connections.IsCurrent(key)
                    || !owner.IsCurrentPlayerEntry(connection.Connection, connection.Identity, entry.Bindings, entry.Generation))
                    throw new InvalidOperationException("Stale raid publication.");
                NetworkServer.SetClientReady(connection.Connection);
                // SetClientReady may invoke callbacks: repeat the exact connection/world guard immediately before publication.
                if (!connections.IsCurrent(key) || !owner.IsCurrentPlayerEntry(connection.Connection, connection.Identity, entry.Bindings, entry.Generation)
                    || !NetworkServer.AddPlayerForConnection(connection.Connection, entry.UnpublishedPlayer))
                    throw new InvalidOperationException("Player publication failed.");
                entry.ActivePlayer = entry.UnpublishedPlayer;
                entry.UnpublishedPlayer = null;
            }
            public bool TryRebindPlayer(string key)
            {
                ServerRaidConnections.Entry connection = connections.Find(key);
                if (connection == null || connection.Connection == null || connection.Connection.identity != null
                    || !entries.TryGetValue(key, out Entry entry) || entry.ActivePlayer == null
                    || !connections.IsCurrent(key)) return false;
                if (!owner.IsCurrentPlayerEntry(connection.Connection, connection.Identity, entry.Bindings, entry.Generation)
                    || !entry.ActivePlayer.TryGetComponent(out OnlinePlayerIdentity profile)
                    || !profile.TryRebindServerIdentity(connection.Identity, entry.Generation)) return false;
                NetworkServer.SetClientReady(connection.Connection);
                return NetworkServer.AddPlayerForConnection(connection.Connection, entry.ActivePlayer);
            }
            public FrozenRaidInventory FreezePlayer(string key)
            {
                GameObject actor = Actor(key);
                if (actor == null) throw new InvalidOperationException("The raid actor is missing.");
                Vector3 position = actor.transform.position;
                NetworkPlayerController controller = actor.GetComponent<NetworkPlayerController>();
                if (controller != null)
                {
                    controller.Player.Consumables?.Cancel();
                    controller.SetLocalGameplayInputAllowed(false); controller.Player.ClearIntent();
                    controller.Player.Equipment?.ConfigureAuthority(false, true); controller.Player.Attributes.ConfigureAuthority(false);
                    controller.enabled = false;
                }
                NetworkRoleLoadout loadout = actor.GetComponent<NetworkRoleLoadout>();
                if (loadout == null) throw new InvalidOperationException("The raid loadout is missing.");
                loadout.SuspendServerOperations();
                return new FrozenRaidInventory { Position = new RaidPosition(position.x, position.y, position.z),
                    RoleLoadoutState = loadout.CaptureServerState() };
            }
            public void ClearSource(string key)
            {
                if (!NetworkServer.active || singleton != owner) throw new InvalidOperationException("The raid world is stopped.");
                Actor(key)?.GetComponent<NetworkRoleLoadout>()?.SuspendServerOperations();
            }
            public void PublishRoleLoot(string key, string bagId, RaidPosition position)
            {
                if (string.IsNullOrEmpty(bagId)) throw new InvalidOperationException("The role loot id is missing.");
                if (publishedBags.Contains(bagId)) return;
                // A failed publication must not materialize an old round's frozen loot in a later round.
                if (entries.TryGetValue(key, out Entry entry) && entry.LootExpired) return;
                if (!NetworkServer.active || singleton != owner || owner.raidLootBagPrefab == null)
                    throw new InvalidOperationException("The role loot world is unavailable.");
                GameObject actor = Actor(key);
                NetworkRoleLoadout loadout = actor != null ? actor.GetComponent<NetworkRoleLoadout>() : null;
                if (loadout == null) throw new InvalidOperationException("The frozen role loot owner is missing.");
                if (!loadout.TryPrepareCorpseLoot(out LootSource source, out string error))
                    throw new InvalidOperationException(error);
                RaidLootBag bag = Instantiate(owner.raidLootBagPrefab,
                    new Vector3(position.X, position.Y, position.Z), Quaternion.identity);
                bool transferred = false;
                try
                {
                    bag.PrepareRoleLoot(bagId, source);
                    NetworkServer.Spawn(bag.gameObject);
                    NetworkIdentity identity = bag.GetComponent<NetworkIdentity>();
                    if (identity.netId == 0 || !NetworkServer.spawned.TryGetValue(identity.netId, out NetworkIdentity spawned)
                        || spawned != identity) throw new InvalidOperationException("The role loot publication failed.");
                    // The spawned crate rejects access until this atomic ownership transfer activates its source.
                    if (!loadout.CommitCorpseLoot(source, out error)) throw new InvalidOperationException(error);
                    transferred = true;
                    roleBags[bagId] = bag;
                    publishedBags.Add(bagId);
                }
                catch (Exception exception)
                {
                    // Observer failures after commit cannot roll back ownership or destroy the now-live crate.
                    if (transferred || source.IsActive)
                    {
                        roleBags[bagId] = bag;
                        publishedBags.Add(bagId);
                        Debug.LogException(exception, bag);
                        return;
                    }
                    if (bag != null)
                    {
                        if (bag.TryGetComponent(out NetworkIdentity identity) && identity.netId != 0)
                            NetworkServer.Destroy(bag.gameObject);
                        else Destroy(bag.gameObject);
                    }
                    throw;
                }
            }
            public void ReturnToLobby(string key)
            {
                NetworkConnectionToClient connection = connections.Find(key)?.Connection;
                if (connection == null) return;
                NetworkServer.SetClientNotReady(connection);
                if (connection.identity != null) NetworkServer.RemovePlayerForConnection(connection, RemovePlayerOptions.Destroy);
            }
            public void ReleasePlayer(string key)
            {
                if (!entries.TryGetValue(key, out Entry entry)) return;
                if (entry.UnpublishedPlayer != null) { entry.UnpublishedPlayer.GetComponent<NetworkRoleLoadout>()?.SuspendServerOperations(); Destroy(entry.UnpublishedPlayer); }
                if (entry.ActivePlayer != null)
                {
                    entry.ActivePlayer.GetComponent<NetworkRoleLoadout>()?.SuspendServerOperations();
                    if (entry.ActivePlayer.TryGetComponent(out NetworkIdentity identity) && identity.netId != 0)
                        NetworkServer.Destroy(entry.ActivePlayer);
                    else Destroy(entry.ActivePlayer);
                }
                entries.Remove(key);
            }
            internal void Clear()
            {
                if (match != null) match.Changed -= OnMatchChanged;
                ClearRoleBags();
                foreach (string key in new List<string>(entries.Keys)) ReleasePlayer(key);
                publishedBags.Clear(); initializing.Clear();
            }
        }
    }
}
