using System;
using Mirror;
using RPG4.Networking.Contracts;
using RPG4.Raid.Application.Client;
using RPG4.Raid.Domain;
using UnityEngine;
namespace RPG4.Networking
{
    public sealed partial class AuthoritativeNetworkManager
    {
        public const string LobbyScene = "Assets/Scenes/Lobby.unity";
        [SerializeField] private RaidLootBag raidLootBagPrefab;
        [SerializeField, Min(30)] private float raidDurationSeconds = 1200f;
        public event Action ClientRaidChanged;
        public ClientRaidFlow ClientRaid { get { EnsureRaidClient(); return clientRaid; } }
        public RaidClientPhase ClientRaidPhase => (RaidClientPhase)ClientRaid.Phase;
        public string LastRaidResultText => ClientRaid.LastResultText;
        public string ClientRaidId => ClientRaid.RaidId;
        public bool ClientLobbyVisible => ClientRaid.LobbyVisible;
        public bool CanStartRaid => ClientRaid.CanStartRaid;
        private ClientRaidFlow clientRaid;
        private string deferredGameplayScene;
        private void EnsureRaidClient()
        {
            if (clientRaid != null) return;
            clientRaid = new ClientRaidFlow(this, this, this);
            clientRaid.Changed += () => ClientRaidChanged?.Invoke();
        }
        private void StartRaidClientMessages()
        {
            EnsureRaidClient();
            NetworkClient.ReplaceHandler<SceneMessage>(OnControlledSceneMessage, false);
            NetworkClient.RegisterHandler<RaidSessionMessage>(OnRaidSessionMessage);
        }
        private void StopRaidClientMessages()
        {
            NetworkClient.UnregisterHandler<RaidSessionMessage>();
            clientRaid?.Reset(); deferredGameplayScene = null;
        }
        public override void OnClientConnect()
        {
            if (!usesGameplaySceneBindings) { base.OnClientConnect(); return; }
            EnsureClientSessionForCallback(); ClientRaid.Connected();
        }
        private void OnControlledSceneMessage(SceneMessage message)
        {
            if (message.sceneOperation != SceneOperation.Normal || !IsGameplaySceneName(message.sceneName))
            { NetworkClient.Disconnect(); return; }
            deferredGameplayScene = message.sceneName;
        }
        private static bool IsGameplaySceneName(string path) => path == EntryProtocol.GameplayScene || path == "OnlineGame";
        public bool TryStartRaid(out string reason) => ClientRaid.TryStartRaid(out reason);
        public bool TryAbandonRaid(out string reason) => ClientRaid.TryAbandonRaid(out reason);
        private void TickLobbyClient() => clientRaid?.Tick();
        private void OnRaidSessionMessage(RaidSessionMessage message)
        {
            if (message.Phase > (byte)RaidPhase.Rejected) { NetworkClient.Disconnect(); return; }
            ClientRaid.ReceiveSession((RaidPhase)message.Phase, message.RaidId, message.WorldRunId, message.Text);
        }
    }
}
