using System;
using Mirror;
using UnityEngine.SceneManagement;

namespace RPG4.Networking
{
    /// <summary>Immutable observation of the existing manager lifecycle, not a second session state machine.
    /// Subscribe before reading CurrentGameplayLifecycle. Compare manager/session, world generation and revision
    /// before acting on deferred callbacks; object references alone can belong to an ended session.</summary>
    public readonly struct GameplayLifecycleEvent
    {
        public ClientSessionIdentity Session { get; }
        public long ServerWorldGeneration { get; }
        public long Revision { get; }
        public int SceneHandle { get; }
        public string ScenePath { get; }
        public GameplaySceneBindings Bindings { get; }
        public NetworkPlayerController LocalPlayer { get; }
        public bool WorldReady { get; }
        public bool GameplayReady { get; }
        public string FailureReason { get; }

        internal GameplayLifecycleEvent(ClientSessionIdentity session, long worldGeneration, long revision,
            Scene scene, GameplaySceneBindings bindings, NetworkPlayerController localPlayer,
            bool worldReady, bool gameplayReady, string failureReason)
        {
            Session = session; ServerWorldGeneration = worldGeneration; Revision = revision;
            SceneHandle = scene.IsValid() ? scene.handle : 0;
            ScenePath = scene.IsValid() ? scene.path : string.Empty;
            Bindings = bindings; LocalPlayer = localPlayer; WorldReady = worldReady;
            GameplayReady = gameplayReady; FailureReason = failureReason ?? string.Empty;
        }
    }

    /// <summary>Capture manager/session before startup and reject other sessions. Connection is null before Connect;
    /// adopt the first live connection from that same session, then reject events from other connections.</summary>
    public readonly struct ClientSessionIdentity
    {
        public AuthoritativeNetworkManager Manager { get; }
        public int ManagerInstanceId { get; }
        public long SessionId { get; }
        public NetworkConnectionToServer Connection { get; }

        internal ClientSessionIdentity(AuthoritativeNetworkManager manager, long sessionId,
            NetworkConnectionToServer connection)
        {
            Manager = manager;
            ManagerInstanceId = manager.GetInstanceID();
            SessionId = sessionId;
            Connection = connection;
        }
    }

    public readonly struct ClientSceneLifecycleEvent
    {
        public ClientSessionIdentity Session { get; }
        public string SceneName { get; }
        public SceneOperation Operation { get; }
        public bool CustomHandling { get; }

        internal ClientSceneLifecycleEvent(ClientSessionIdentity session, string sceneName,
            SceneOperation operation, bool customHandling)
        {
            Session = session;
            SceneName = sceneName;
            Operation = operation;
            CustomHandling = customHandling;
        }
    }

    public readonly struct ClientFailureEvent
    {
        public ClientSessionIdentity Session { get; }
        public TransportError? TransportError { get; }
        public string Reason { get; }
        public Exception Exception { get; }

        internal ClientFailureEvent(ClientSessionIdentity session, TransportError? transportError,
            string reason, Exception exception)
        {
            Session = session;
            TransportError = transportError;
            Reason = reason;
            Exception = exception;
        }
    }
}
