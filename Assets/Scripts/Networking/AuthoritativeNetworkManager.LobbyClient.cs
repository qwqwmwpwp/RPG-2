using System;
using System.IO;
using Mirror;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
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

        private ItemManifestCatalog ResolveLobbyCatalog() => lobbyCatalog != null ? lobbyCatalog
            : playerPrefab != null ? playerPrefab.GetComponent<RPG4.Backpack.Runtime.BackpackInventory>()?.Catalog : null;

        private void EnsureRaidClient()
        {
            if (clientRaid != null) return;
            clientRaidInventory = new ClientRaidInventoryProjection(ResolveLobbyCatalog);
            clientRaid = new ClientRaidFlow(this, this, this, clientRaidInventory);
            clientRaidInventory.Bind(clientRaid);
            clientRaid.InventoryChanged += () => LobbyInventoryChanged?.Invoke();
            clientRaid.Changed += () => ClientRaidChanged?.Invoke();
        }

        private void StartRaidClientMessages()
        {
            EnsureRaidClient();
            NetworkClient.ReplaceHandler<SceneMessage>(OnControlledSceneMessage, false);
            NetworkClient.RegisterHandler<LobbySnapshotMessage>(OnLobbySnapshot);
            NetworkClient.RegisterHandler<RaidSessionMessage>(OnRaidSessionMessage);
        }

        private void StopRaidClientMessages()
        {
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
        private void TickLobbyClient() => clientRaid?.Tick();

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
                        Success = message.Success, ResultItemId = message.ResultItemId, MovedCount = message.MovedCount, Error = message.Error });
            }
            catch (Exception) { NetworkClient.Disconnect(); }
        }

        private void OnRaidSessionMessage(RaidSessionMessage message) =>
            ClientRaid.ReceiveSession((RaidPhase)message.Phase, message.RaidId, message.WorldRunId, message.Text);
    }
}
