using System;
using Mirror;
using RPG4.Networking.Contracts;
using RPG4.Raid.Application.Client;
using RPG4.Raid.Domain;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG4.Networking
{
    public sealed partial class AuthoritativeNetworkManager : IClientRaidClock, IClientRaidTransport, IClientRaidScenes
    {
        double IClientRaidClock.Now => Time.realtimeSinceStartupAsDouble;
        bool IClientRaidTransport.Connected => NetworkClient.isConnected;
        bool IClientRaidTransport.LoadingScene => NetworkClient.isLoadingScene;
        bool IClientRaidScenes.IsLobbyActive => SceneManager.GetActiveScene().path == LobbyScene;

        void IClientRaidTransport.Send(LobbyCommand command) => NetworkClient.Send(new LobbyRequestMessage {
            Kind = (byte)command.Kind, OperationId = command.OperationId, RaidId = command.RaidId,
            Revision = command.Revision, RequestId = command.RequestId, Source = command.Source,
            Destination = command.Destination, OperationKind = command.OperationKind,
            SourceItemId = command.SourceItemId, TargetItemId = command.TargetItemId,
            Count = command.Count, UpperLeftIndex = command.UpperLeftIndex,
            RootItemId = command.RootItemId, ParentItemId = command.ParentItemId, CandidateItemId = command.CandidateItemId,
            SlotId = command.SlotId, AssemblySessionId = command.AssemblySessionId, StructureRevision = command.StructureRevision,
            CandidateOffset = command.CandidateOffset, CandidateLimit = command.CandidateLimit });
        void IClientRaidTransport.Disconnect() => NetworkClient.Disconnect();

        ClientSceneLoadResult IClientRaidScenes.Load(ClientRaidScene scene)
        {
            if (!NetworkClient.isConnected || NetworkServer.active || IsClientSceneChangePending) return ClientSceneLoadResult.Deferred;
            string path = scene == ClientRaidScene.Lobby ? LobbyScene
                : string.IsNullOrEmpty(deferredGameplayScene) ? EntryProtocol.GameplayScene : deferredGameplayScene;
            OnClientChangeScene(path, SceneOperation.Normal, true);
            NetworkClient.isLoadingScene = true;
            networkSceneName = path;
            try
            {
                loadingSceneAsync = SceneManager.LoadSceneAsync(path, LoadSceneMode.Single);
                if (loadingSceneAsync == null) throw new InvalidOperationException();
                return ClientSceneLoadResult.Started;
            }
            catch (Exception)
            {
                NetworkClient.isLoadingScene = false; clientSceneChangeRequested = false;
                return ClientSceneLoadResult.Failed;
            }
        }

        void IClientRaidScenes.EnterGameplay()
        {
            RefreshGameplaySceneBindings();
            if (!NetworkClient.ready) NetworkClient.Ready();
            if (NetworkClient.localPlayer == null) NetworkClient.AddPlayer();
        }

        void IClientRaidScenes.ClearWorld() => NetworkClient.DestroyAllClientObjects();

        private void CompleteControlledClientScene()
        {
            clientSceneChangeRequested = false;
            string path = SceneManager.GetActiveScene().path;
            ClientRaidScene scene;
            if (path == LobbyScene) scene = ClientRaidScene.Lobby;
            else if (IsGameplaySceneName(path)) scene = ClientRaidScene.Gameplay;
            else { NetworkClient.Disconnect(); return; }
            if (!ClientRaid.SceneCompleted(scene)) return;
            PublishClientEvent(ClientSceneLoaded, new ClientSceneLifecycleEvent(CurrentClientSession, path, SceneOperation.Normal, true));
            ClientRaid.PublishSceneCompletion();
        }
    }
}
