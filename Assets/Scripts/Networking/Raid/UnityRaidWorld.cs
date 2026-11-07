using System;
using System.Collections.Generic;
using Mirror;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
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
                internal GameplaySceneBindings Bindings;
                internal long Generation;
            }
            private readonly AuthoritativeNetworkManager owner;
            private readonly ServerRaidConnections connections;
            private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
            private readonly HashSet<string> initializing = new HashSet<string>();
            private readonly HashSet<string> publishedBags = new HashSet<string>();
            internal UnityRaidWorld(AuthoritativeNetworkManager owner, ServerRaidConnections connections)
            { this.owner = owner; this.connections = connections; }
            private GameObject Actor(string key)
            {
                ServerRaidConnections.Entry connection = connections.Find(key);
                if (connection?.Connection?.identity != null) return connection.Connection.identity.gameObject;
                return entries.TryGetValue(key, out Entry entry) ? entry.UnpublishedPlayer : null;
            }
            public bool CanPrepare(string key) => owner.ValidateServerGameplayWorld(out _) && owner.raidLootBagPrefab != null;
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
                if (connection == null || !initializing.Add(key)) return false;
                GameObject player = null; bool succeeded = false;
                var entry = new Entry { Generation = owner.serverWorldGeneration, Bindings = owner.gameplaySceneBindings };
                entries[key] = entry;
                try
                {
                    NetworkConnectionToClient conn = connection.Connection;
                    if (!connections.IsCurrent(key) || conn.identity != null || !owner.ValidateServerGameplayWorld(out _)
                        || !(owner.gameplaySceneBindings.TrustedSpawnConfiguration is IServerPlayerSpawnInitializer initializer))
                        throw new InvalidOperationException();
                    Transform spawn = owner.GetStartPosition();
                    if (spawn == null || !owner.IsCurrentPlayerEntry(conn, connection.Identity, entry.Bindings, entry.Generation))
                        throw new InvalidOperationException();
                    player = Instantiate(owner.playerPrefab, spawn.position, spawn.rotation);
                    var context = new TrustedPlayerSpawnContext(connection.Identity, entry.Bindings, spawn, owner.ServerItemIds, entry.Generation, reservation);
                    if (!initializer.TryInitializeBeforeSpawn(player, context, out _) || !owner.ValidateServerGameplayWorld(out _)
                        || !connections.IsCurrent(key) || player == null || entry.Bindings == null || player.scene != entry.Bindings.gameObject.scene
                        || !player.TryGetComponent(out NetworkIdentity networkIdentity) || networkIdentity.netId != 0
                        || !player.TryGetComponent(out OnlinePlayerIdentity profile) || !profile.MatchesServerIdentity(connection.Identity, entry.Generation)
                        || !owner.IsCurrentPlayerEntry(conn, connection.Identity, entry.Bindings, entry.Generation))
                        throw new InvalidOperationException();
                    // This object remains unpublished until the application receives the durable Active result.
                    entry.UnpublishedPlayer = player; succeeded = true; return true;
                }
                catch (Exception) { owner.ReportGameplayFailure("Trusted player initialization or entry failed."); return false; }
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
                entry.UnpublishedPlayer = null;
            }
            public FrozenRaidInventory FreezePlayer(string key)
            {
                GameObject actor = Actor(key);
                if (actor == null) return new FrozenRaidInventory {
                    Backpack = new PersistentContainerData { Kind = PersonalContainerKind.Backpack, Rows = 10, Columns = 10 },
                    Equipment = new PersistentContainerData { Kind = PersonalContainerKind.Equipment, Rows = 1, Columns = 1 } };
                Vector3 position = actor.transform.position;
                NetworkPlayerController controller = actor.GetComponent<NetworkPlayerController>();
                if (controller != null)
                {
                    controller.Player.Consumables?.Cancel();
                    controller.SetLocalGameplayInputAllowed(false); controller.Player.ClearIntent();
                    controller.Player.Equipment?.ConfigureAuthority(false, true); controller.Player.Attributes.ConfigureAuthority(false);
                    controller.enabled = false;
                }
                BackpackNetworkInventory networkInventory = actor.GetComponent<BackpackNetworkInventory>();
                if (networkInventory != null)
                { networkInventory.ServerAccessValidator = _ => false; networkInventory.ServerOperationScheduler = (_, reject) => { reject?.Invoke("战局正在结算。"); return true; }; }
                return new FrozenRaidInventory { Position = new RaidPosition(position.x, position.y, position.z),
                    Backpack = RaidInventorySnapshots.Capture(actor.GetComponent<BackpackInventory>().Container, PersonalContainerKind.Backpack),
                    Equipment = RaidInventorySnapshots.Capture(actor.GetComponent<EquipmentInventory>().Container, PersonalContainerKind.Equipment) };
            }
            public void ClearSource(string key)
            {
                if (!NetworkServer.active || singleton != owner) throw new InvalidOperationException("The raid world is stopped.");
                GameObject actor = Actor(key);
                if (actor == null) return;
                RaidInventorySnapshots.Clear(actor.GetComponent<BackpackInventory>().Container);
                RaidInventorySnapshots.Clear(actor.GetComponent<EquipmentInventory>().Container);
            }
            public void PublishDropBag(string key, PersistentDropBag data, RaidPosition position)
            {
                if (publishedBags.Contains(data.BagId)) return;
                if (!NetworkServer.active || singleton != owner || owner.raidLootBagPrefab == null) throw new InvalidOperationException("Loot bag world is unavailable.");
                RaidLootBag bag = Instantiate(owner.raidLootBagPrefab, new Vector3(position.X, position.Y, position.Z), Quaternion.identity);
                try { bag.Prepare(data, owner.ResolveLobbyCatalog()); NetworkServer.Spawn(bag.gameObject); publishedBags.Add(data.BagId); }
                catch { Destroy(bag.gameObject); throw; }
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
                if (entry.UnpublishedPlayer != null) Destroy(entry.UnpublishedPlayer);
                entries.Remove(key);
            }
            internal void Clear()
            {
                foreach (string key in new List<string>(entries.Keys)) ReleasePlayer(key);
                publishedBags.Clear(); initializing.Clear();
            }
        }
    }
}
