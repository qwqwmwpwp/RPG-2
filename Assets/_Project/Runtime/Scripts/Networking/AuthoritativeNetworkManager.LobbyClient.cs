using System;
using System.IO;
using Mirror;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
using System.Collections.Generic;
using RPG4.Networking.Contracts;
using RPG4.Raid.Application.Client;
using RPG4.Raid.Domain;
using UnityEngine;

namespace RPG4.Networking
{
    public sealed partial class AuthoritativeNetworkManager
    {
        public const string LobbyScene = "Assets/Scenes/Lobby.unity";
        [Header("Persistent lobby / raids")]
        [SerializeField] private ItemManifestCatalog lobbyCatalog;
        [SerializeField] private RaidLootBag raidLootBagPrefab;
        [SerializeField, Min(30)] private float raidDurationSeconds = 1200f;

        public event Action LobbyInventoryChanged;
        public event Action ClientRaidChanged;
        public ClientRaidFlow ClientRaid { get { EnsureRaidClient(); return clientRaid; } }
        public IClientLobbyInventoryViews ClientRaidInventory { get { EnsureRaidClient(); return clientRaidInventory; } }
        public RaidClientPhase ClientRaidPhase => (RaidClientPhase)ClientRaid.Phase;
        public string LastRaidResultText => ClientRaid.LastResultText;
        public string ClientRaidId => ClientRaid.RaidId;
        public bool ClientLobbyVisible => ClientRaid.LobbyVisible;
        public bool IsLobbyInventoryReady => ClientRaid.IsInventoryReady;
        public bool CanStartRaid => ClientRaid.CanStartRaid;
        public IBackpackView LobbyStash => ClientRaidInventory.Stash;
        public IBackpackView LobbyBackpack => ClientRaidInventory.Backpack;
        public IBackpackView LobbyEquipment => ClientRaidInventory.Equipment;

        private ClientRaidFlow clientRaid;
        private ClientRaidInventoryProjection clientRaidInventory;
        private string deferredGameplayScene, incomingTransfer;
        private byte[][] incomingChunks;
        private int incomingChunkCount, incomingBytes;
        private readonly Dictionary<(byte source, int session), (string operation, AssemblyWireBuffer buffer)> incomingAssemblies
            = new Dictionary<(byte, int), (string, AssemblyWireBuffer)>();

        private ItemManifestCatalog ResolveLobbyCatalog() => lobbyCatalog != null ? lobbyCatalog
            : playerPrefab != null ? playerPrefab.GetComponent<RPG4.Backpack.Runtime.BackpackInventory>()?.Catalog : null;

        private void EnsureRaidClient()
        {
            if (clientRaid != null) return;
            clientRaidInventory = new ClientRaidInventoryProjection(ResolveLobbyCatalog);
            clientRaid = new ClientRaidFlow(this, this, this, clientRaidInventory);
            clientRaidInventory.Bind(clientRaid);
            clientRaidInventory.AssemblyRequestChanged += (source, session) => incomingAssemblies.Remove((source, session));
            clientRaid.InventoryChanged += () => LobbyInventoryChanged?.Invoke();
            clientRaid.Changed += () => ClientRaidChanged?.Invoke();
        }

        private void StartRaidClientMessages()
        {
            EnsureRaidClient();
            NetworkClient.ReplaceHandler<SceneMessage>(OnControlledSceneMessage, false);
            NetworkClient.RegisterHandler<LobbySnapshotMessage>(OnLobbySnapshot);
            NetworkClient.RegisterHandler<LobbyAssemblyResponseMessage>(OnLobbyAssemblyResponse);
            NetworkClient.RegisterHandler<RaidSessionMessage>(OnRaidSessionMessage);
        }

        private void StopRaidClientMessages()
        {
            NetworkClient.UnregisterHandler<LobbyAssemblyResponseMessage>(); incomingAssemblies.Clear();
            NetworkClient.UnregisterHandler<LobbySnapshotMessage>(); NetworkClient.UnregisterHandler<RaidSessionMessage>();
            clientRaid?.Reset();
            incomingChunks = null; incomingTransfer = deferredGameplayScene = null;
        }

        public override void OnClientConnect()
        {
            if (!usesGameplaySceneBindings) { base.OnClientConnect(); return; }
            EnsureClientSessionForCallback();
            ClientRaid.Connected();
        }

        private void OnControlledSceneMessage(SceneMessage message)
        {
            if (message.sceneOperation != SceneOperation.Normal || !IsGameplaySceneName(message.sceneName))
            { NetworkClient.Disconnect(); return; }
            // Mirror's authentication scene offer is not raid admission.
            deferredGameplayScene = message.sceneName;
        }

        private static bool IsGameplaySceneName(string path) => path == EntryProtocol.GameplayScene || path == "OnlineGame";
        public bool TryStartRaid(out string reason) => ClientRaid.TryStartRaid(out reason);
        public bool TryAbandonRaid(out string reason) => ClientRaid.TryAbandonRaid(out reason);
        private void TickLobbyClient()
        {
            clientRaid?.Tick();
            foreach (var key in new List<(byte source, int session)>(incomingAssemblies.Keys))
                if (Time.unscaledTimeAsDouble > incomingAssemblies[key].buffer.Deadline) incomingAssemblies.Remove(key);
        }

        private void OnLobbySnapshot(LobbySnapshotMessage message)
        {
            try
            {
                if (message.TransferId == null || message.TransferId.Length != 32 || message.Count < 1
                    || message.Count > (RaidInventoryWireCodec.MaxBytes + RaidInventoryWireCodec.ChunkBytes - 1) / RaidInventoryWireCodec.ChunkBytes
                    || message.Index < 0 || message.Index >= message.Count || message.Payload == null || message.Payload.Length > RaidInventoryWireCodec.ChunkBytes)
                    throw new InvalidDataException();
                if (incomingTransfer != message.TransferId)
                { incomingTransfer = message.TransferId; incomingChunks = new byte[message.Count][]; incomingChunkCount = incomingBytes = 0; }
                if (incomingChunks.Length != message.Count) throw new InvalidDataException();
                if (incomingChunks[message.Index] == null)
                { incomingChunks[message.Index] = message.Payload; incomingChunkCount++; incomingBytes += message.Payload.Length; }
                if (incomingBytes > RaidInventoryWireCodec.MaxBytes) throw new InvalidDataException();
                if (incomingChunkCount != message.Count) return;
                var bytes = new byte[incomingBytes]; int offset = 0;
                foreach (byte[] chunk in incomingChunks) { Buffer.BlockCopy(chunk, 0, bytes, offset, chunk.Length); offset += chunk.Length; }
                incomingChunks = null; incomingTransfer = null;
                ClientRaid.ReceiveInventory(RaidInventoryWireCodec.Decode(bytes), message.OperationId,
                    (PersonalContainerKind)message.Source, new LobbyInventoryResult { RequestId = message.RequestId,
                        Success = message.Success, ResultItemId = message.ResultItemId, MovedCount = message.MovedCount, Error = message.Error, AssemblyKind = message.AssemblyKind, AssemblyPayload = message.AssemblyPayloadPending ? Array.Empty<byte>() : null });
            }
            catch (Exception) { NetworkClient.Disconnect(); }
        }

        private void OnLobbyAssemblyResponse(LobbyAssemblyResponseMessage message)
        {
            var key = (message.Source, message.SessionId);
            if (message.OperationId == null || message.OperationId.Length != 32 || message.SlotId != null && message.SlotId.Length > 64
                || !clientRaidInventory.AcceptsAssembly(message.Source, message.Kind, message.SessionId, message.RequestId,
                    message.RootItemId, message.ParentItemId, message.SlotId, message.CandidateOffset)) { incomingAssemblies.Remove(key); return; }
            try {
                if (!incomingAssemblies.TryGetValue(key, out var receive) || receive.operation != message.OperationId) {
                    if (!incomingAssemblies.ContainsKey(key) && incomingAssemblies.Count >= 32) return;
                    receive = (message.OperationId, new AssemblyWireBuffer(message.Count, Time.unscaledTimeAsDouble)); incomingAssemblies[key] = receive;
                }
                if (!receive.buffer.Add(message.Index, message.Count, message.Payload, Time.unscaledTimeAsDouble, out byte[] payload)) return;
                incomingAssemblies.Remove(key); clientRaidInventory.PublishAssembly(message.Source, message.Kind, payload);
            }
            catch (Exception) { incomingAssemblies.Remove(key); }
        }

        private void OnRaidSessionMessage(RaidSessionMessage message) =>
            ClientRaid.ReceiveSession((RaidPhase)message.Phase, message.RaidId, message.WorldRunId, message.Text);
    }
}
