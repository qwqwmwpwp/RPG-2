using RPG4.Backpack.Network;
using System.Collections.Generic;
using RPG4.UI;
using RPG4.PlayerMovement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror;
using RPG4.Networking.Contracts;

namespace RPG4.Networking
{
    /// <summary>Legacy offline lifecycle plus an explicit opt-in scene-owned online lifecycle.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/Authoritative Network Manager")]
    public sealed class AuthoritativeNetworkManager : BackpackNetworkManager
    {
        [SerializeField] private GameObject offlinePlayer;
        [SerializeField] private PlayerCameraController localCamera;
        [SerializeField] private Collider[] additionalCombatTargets;
        [Tooltip("Opt in only for the new online flow. Main/legacy Host retain their authored offline lifecycle.")]
        [SerializeField] private bool usesGameplaySceneBindings;
        private GameplaySceneBindings gameplaySceneBindings;
        private NetworkPlayerController boundLocalController;
        private bool clientLifecycleStarted, serverLifecycleStarted, clientSceneChangeRequested;
        private bool connectionEndReported;
        private bool clientSessionPrepared;
        private long clientSessionId;
        private bool clientOwnsSceneCleanup;
        private long clientSceneCleanupSessionId;
        private string clientOfflineSceneAfterCleanup;
        private NetworkConnectionToServer sessionConnection;
        private string pendingClientSceneName;
        private SceneOperation pendingClientSceneOperation;
        private bool pendingClientSceneCustomHandling;
        private int gameplaySpawnIndex;
        private long serverWorldGeneration, gameplayRevision;
        private bool refreshingGameplayBindings, lastWorldReady, lastGameplayReady;
        private GameplaySceneBindings lastObservedBindings;
        private NetworkPlayerController lastObservedPlayer;
        private int lastObservedSceneHandle;
        private string lastGameplayFailure = string.Empty;
        private long lastObservedWorldGeneration, lastObservedClientSessionId;
        private bool serverSceneSpawnComplete;
        private readonly HashSet<NetworkConnectionToClient> pendingPlayerEntries = new HashSet<NetworkConnectionToClient>();

        public bool UsesGameplaySceneBindings => usesGameplaySceneBindings;
        /// <summary>Failed before Mirror installed its disconnect pipeline. Release this manager after stop;
        /// IsClientSessionStopped reports completed cleanup, not permission to reuse this instance.</summary>
        public bool RequiresManagerReplacement { get; private set; }
        public long ClientSessionId => clientSessionId;
        public ClientSessionIdentity CurrentClientSession => new ClientSessionIdentity(this, clientSessionId,
            sessionConnection ?? NetworkClient.connection);
        public GameplaySceneBindings CurrentGameplaySceneBindings => IsCurrentGameplaySceneBindingValid
            ? gameplaySceneBindings : null;
        public bool IsServerGameplayWorldReady => ValidateServerGameplayWorld(out _);
        /// <summary>Changes whenever the server world binding is invalidated/replaced, including reloads.</summary>
        public long ServerWorldGeneration => serverWorldGeneration;
        public GameplayLifecycleEvent CurrentGameplayLifecycle => CaptureGameplayLifecycle(
            IsServerGameplayWorldReady, IsLocalGameplayReady, lastGameplayFailure);
        public bool IsLocalGameplayReady => IsCurrentGameplaySceneBindingValid && clientLifecycleStarted
            && !IsClientSceneChangePending && IsCurrentLocalController(boundLocalController)
            && boundLocalController.HasInitialAuthoritativeState
            && boundLocalController.TryGetComponent(out OnlinePlayerIdentity profile) && profile.HasInitialProfile
            && boundLocalController.TryGetComponent(out BackpackNetworkInventory inventory) && inventory.IsReady
            && boundLocalController.TryGetComponent(out BackpackNetworkEquipment equipment) && equipment.IsReady
            && gameplaySceneBindings.IsBoundTo(boundLocalController.Player);
        public bool IsClientSceneChangePending => clientSceneChangeRequested || HasMirrorSceneWork
            // Local Mirror schedules this private method with Invoke after OnStopClient/Shutdown.
            || IsInvoking("ClientChangeOfflineScene");
        /// <summary>Shutdown AND scene work completed. OnStopClient/ClientConnectionEnded alone do not imply this.</summary>
        public bool IsClientSessionStopped => !clientLifecycleStarted && IsClientTransportStopped
            && !IsClientSceneChangePending;

        public event System.Action<ClientSceneLifecycleEvent> ClientSceneChanging;
        public event System.Action<ClientSceneLifecycleEvent> ClientSceneLoaded;
        /// <summary>Disconnect notification, emitted before Mirror shuts down its client. Poll IsClientSessionStopped afterward.</summary>
        public event System.Action<ClientSessionIdentity> ClientConnectionEnded;
        public event System.Action<ClientFailureEvent> ClientFailure;
        public event System.Action<GameplayLifecycleEvent> GameplaySceneBindingsChanged;
        public event System.Action<GameplayLifecycleEvent> ServerGameplayWorldReadyChanged;
        public event System.Action<GameplayLifecycleEvent> LocalGameplayReadyChanged;
        public event System.Action<GameplayLifecycleEvent> GameplayFailure;

        private static bool HasMirrorSceneWork => loadingSceneAsync != null
            || NetworkClient.isLoadingScene || NetworkServer.isLoadingScene;
        private static bool IsClientTransportStopped => !NetworkClient.active && !NetworkClient.isConnecting
            && !NetworkClient.isConnected && NetworkClient.connection == null && NetworkClient.localPlayer == null;

        /// <summary>Read-only fallback for a persistent controller after its per-session manager has been destroyed.</summary>
        public static bool ObserveClientSessionStopped(int managerInstanceId, long sessionId)
        {
            if (singleton is AuthoritativeNetworkManager manager && manager != null)
            {
                // Never allow an old cleanup loop to declare a newer manager/session stopped.
                if (manager.GetInstanceID() != managerInstanceId || manager.clientSessionId != sessionId) return false;
                return manager.IsClientSessionStopped;
            }
            // A different kind of manager also owns the process-wide Mirror state.
            if (singleton != null) return false;
            return IsClientTransportStopped && !HasMirrorSceneWork;
        }

        public static bool ObserveClientSceneChangePending() => HasMirrorSceneWork
            || (singleton is AuthoritativeNetworkManager manager && manager != null && manager.IsClientSceneChangePending);

        /// <summary>Call before subscribing/starting to capture a stable session identity before Connect can emit callbacks.
        /// Connection is initially null and is supplied in subsequent events. Repeated preparation is idempotent.</summary>
        public ClientSessionIdentity PrepareClientSession()
        {
            RestoreClientOfflineSceneAfterCleanup();
            if (RequiresManagerReplacement)
                throw new System.InvalidOperationException("This manager failed during setup and must be released and recreated.");
            if (singleton != this || NetworkServer.active || !IsClientSessionStopped || mode != NetworkManagerMode.Offline)
                throw new System.InvalidOperationException("A client session can only be prepared on the idle current manager.");
            if (clientSessionPrepared) return CurrentClientSession;
            AllocateClientSession();
            return CurrentClientSession;
        }

        public void StartClientSession()
        {
            long startingSessionId = PrepareClientSession().SessionId;
            try { base.StartClient(); }
            catch (System.Exception exception) { HandleClientStartException(exception, startingSessionId); throw; }
            finally { FinishClientStartRequest(startingSessionId); }
        }

        public void StartClientSession(System.Uri uri)
        {
            if (uri == null) throw new System.ArgumentNullException(nameof(uri));
            long startingSessionId = PrepareClientSession().SessionId;
            try { base.StartClient(uri); }
            catch (System.Exception exception) { HandleClientStartException(exception, startingSessionId); throw; }
            finally { FinishClientStartRequest(startingSessionId); }
        }

        // Mirror's startup methods are non-virtual. Calls through this concrete type use the safe entry point;
        // base-typed/Backpack automatic starts are reconciled in the earliest callback and OnStartClient below.
        public new void StartClient() => StartClientSession();
        public new void StartClient(System.Uri uri) => StartClientSession(uri);

        private void AllocateClientSession()
        {
            clientSessionId++;
            clientSessionPrepared = true;
            sessionConnection = null;
            connectionEndReported = false;
            pendingClientSceneName = null;
            pendingClientSceneOperation = SceneOperation.Normal;
            pendingClientSceneCustomHandling = false;
            PublishGameplayLifecycle();
        }

        private void EnsureClientSessionForCallback()
        {
            // Late transport failures after shutdown belong to the ended session, never a newly invented one.
            if (!clientLifecycleStarted && !clientSessionPrepared && NetworkClient.active) AllocateClientSession();
            if (!connectionEndReported && sessionConnection == null) sessionConnection = NetworkClient.connection;
        }

        private void FinishClientStartRequest(long startingSessionId)
        {
            if (singleton != this || clientSessionId != startingSessionId) return;
            // Connect may fail synchronously before Mirror invokes OnStartClient, or StartClient may return early.
            if (!NetworkClient.active || NetworkClient.connection == null || connectionEndReported)
                clientLifecycleStarted = false;
            try { ReconcileSynchronousClientStop(startingSessionId); }
            catch (System.Exception) { Debug.LogError("Client startup cleanup failed.", this); }
            clientSessionPrepared = false;
        }

        private void ReconcileSynchronousClientStop(long startingSessionId)
        {
            if (singleton != this || clientSessionId != startingSessionId || NetworkClient.active) return;
            // Local Mirror creates connection AFTER Transport.ClientConnect returns. A synchronous disconnect
            // already completed its full shutdown, but Connect then creates a stale connection shell.
            if (connectionEndReported && !NetworkClient.active && NetworkClient.connection != null)
                NetworkClient.Shutdown();
            else if (!connectionEndReported && mode == NetworkManagerMode.ClientOnly && NetworkClient.connection != null)
            {
                // An early failure subscriber requested StopClient while Connect had no connection yet.
                // Mirror is already Disconnecting, so repeating StopClient is a no-op. The newly created shell
                // can now forward the requested disconnect to the transport.
                NetworkConnectionToServer stoppingConnection = NetworkClient.connection;
                stoppingConnection.Disconnect();
                if (singleton == this && clientSessionId == startingSessionId && !NetworkClient.active
                    && !connectionEndReported && mode == NetworkManagerMode.ClientOnly
                    && ReferenceEquals(NetworkClient.connection, stoppingConnection))
                    NetworkClient.OnDisconnectedEvent?.Invoke();
            }
        }

        private void HandleClientStartException(System.Exception exception, long startingSessionId)
        {
            if (singleton != this || clientSessionId != startingSessionId) return;
            PublishClientEvent(ClientFailure, new ClientFailureEvent(CurrentClientSession, null,
                "Client startup failed.", exception));
            if (singleton != this || clientSessionId != startingSessionId) return;
            try
            {
                base.StopClient();
                if (singleton != this || clientSessionId != startingSessionId) return;
                if (!clientLifecycleStarted && NetworkClient.connection == null && mode == NetworkManagerMode.ClientOnly)
                {
                    // Connect threw before constructing its connection. StopClient's connection?.Disconnect()
                    // cannot progress here. Use the already registered Mirror disconnect pipeline so it owns
                    // authenticator shutdown, OnStopClient, NetworkClient.Shutdown and offline scene scheduling.
                    System.Action disconnect = NetworkClient.OnDisconnectedEvent;
                    if (disconnect != null) disconnect();
                    else
                    {
                        // Setup failed before Mirror registered client messages; no transport connection exists.
                        // Mirror owns mode's private setter. This per-attempt manager cannot be reused.
                        RequiresManagerReplacement = true;
                        try { if (authenticator != null) authenticator.OnStopClient(); }
                        finally
                        {
                            try { OnStopClient(); }
                            finally { NetworkClient.Shutdown(); }
                        }
                    }
                }
            }
            catch (System.Exception)
            {
                Debug.LogError("Client startup cleanup failed.", this);
                // The original startup exception is still rethrown to the caller.
            }
        }

        private void PublishClientEvent<T>(System.Action<T> handlers, T value)
        {
            if (handlers == null) return;
            foreach (System.Delegate handler in handlers.GetInvocationList())
            {
                try { ((System.Action<T>)handler)(value); }
                catch (System.Exception)
                {
                    // Subscriber exception text can contain credentials. Never let it interrupt Mirror cleanup.
                    Debug.LogError("A client lifecycle subscriber failed.", this);
                }
            }
        }

        /// <summary>Configure the new flow before network startup; it never restores an offline replacement character.</summary>
        public void ConfigureGameplaySceneBindings(bool enabled)
        {
            if (NetworkServer.active || !IsClientSessionStopped || mode != NetworkManagerMode.Offline)
                throw new System.InvalidOperationException("Gameplay binding mode can only change while networking and scene work are stopped.");
            ClearGameplaySceneBindings();
            usesGameplaySceneBindings = enabled;
            ConfigureAuthenticatorWorldReadiness();
        }

        private void ConfigureAuthenticatorWorldReadiness()
        {
            if (usesGameplaySceneBindings && authenticator is AccountAuthenticator accountAuthenticator)
                accountAuthenticator.ConfigureWorldReadiness(() => IsServerGameplayWorldReady, () => ServerWorldGeneration);
        }

        public override void Awake()
        {
            base.Awake();
            if (singleton != this) return;
            GameplaySceneBindings.RegistrationChanged += RefreshGameplaySceneBindings;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            SceneManager.sceneUnloaded += OnGameplaySceneUnloaded;
            ConfigureAuthenticatorWorldReadiness();
        }

        public override void OnDestroy()
        {
            GameplaySceneBindings.RegistrationChanged -= RefreshGameplaySceneBindings;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            SceneManager.sceneUnloaded -= OnGameplaySceneUnloaded;
            ClearGameplaySceneBindings();
            base.OnDestroy();
        }
        [System.Serializable]
        private sealed class OfflineSceneObject
        {
            public GameObject Target;
            public bool Active = true;
        }
        [Tooltip("Mirror disables scene identities before Start. Preserve their authored offline activation explicitly.")]
        [SerializeField] private OfflineSceneObject[] offlineSceneObjects = System.Array.Empty<OfflineSceneObject>();
        private readonly Dictionary<GameObject, bool> offlineObjectStates = new Dictionary<GameObject, bool>();
        private bool sceneObjectStatesInitialized, offlineSceneObjectsRestored;
        private bool sceneSuspended, offlineWasActive;
        private PlayerViewController offlineView;
        private readonly List<CharacterHealthView> localHealthViews = new List<CharacterHealthView>();
        public override void Start()
        {
            // The base may start headless/server/client mode or the configured Backpack start mode.
            // Restore scene identities only after those decisions, so client startup never runs their offline business.
            base.Start();
            TryRestoreOffline();
        }
        public override void OnStartServer()
        {
            serverWorldGeneration++;
            serverLifecycleStarted = true;
            serverSceneSpawnComplete = false;
            if (!usesGameplaySceneBindings) SuspendOffline();
            NetworkPlayerSimulation.Reset();
            if (!usesGameplaySceneBindings) NetworkPlayerSimulation.SetAdditionalCombatTargets(additionalCombatTargets);
            base.OnStartServer();
            if (usesGameplaySceneBindings) RefreshGameplaySceneBindings();
            PublishGameplayLifecycle();
        }
        public override void OnStartClient()
        {
            EnsureClientSessionForCallback();
            clientLifecycleStarted = NetworkClient.active && NetworkClient.connection != null && !connectionEndReported;
            clientSessionPrepared = false;
            // Mirror invokes OnStartClient after Connect. A synchronous disconnect has already run OnStopClient.
            if (!clientLifecycleStarted) { ReconcileSynchronousClientStop(clientSessionId); return; }
            if (!usesGameplaySceneBindings) SuspendOffline();
            if (!Mirror.NetworkServer.active)
            {
                NetworkPlayerSimulation.Reset();
                // The objects may have been enabled for offline play before this connection began.
                // Hide them before registration; only a subsequent server spawn may enable them on a pure client.
                if (!usesGameplaySceneBindings)
                {
                    HideSceneObjectsForClient();
                    Mirror.NetworkClient.PrepareToSpawnSceneObjects();
                }
            }
            base.OnStartClient();
            if (usesGameplaySceneBindings) RefreshGameplaySceneBindings();
        }
        public void BindLocalPlayer(Player player)
        {
            if (player == null) return;
            if (usesGameplaySceneBindings)
            {
                NetworkPlayerController controller = player.GetComponent<NetworkPlayerController>();
                if (!IsCurrentLocalController(controller)) return;
                SetBoundLocalController(controller);
                RefreshGameplaySceneBindings();
                return;
            }
            if (localCamera != null) localCamera.BindView(player.LocalView);
            foreach (CharacterHealthView view in localHealthViews) if (view != null) view.BindAttributes(player.Attributes);
            player.RefreshLocalPresentationBindings();
        }
        private void SuspendOffline()
        {
            if (sceneSuspended) return;
            InitializeSceneObjectStates();
            if (offlineSceneObjectsRestored)
            {
                // Preserve changes made during offline play, rather than Mirror's forced startup false state.
                foreach (OfflineSceneObject entry in offlineSceneObjects)
                    if (IsLoadedSceneObject(entry?.Target)) offlineObjectStates[entry.Target] = entry.Target.activeSelf;
            }
            offlineSceneObjectsRestored = false;
            sceneSuspended = true;
            if (offlinePlayer == null) return;
            localHealthViews.Clear();
            var attributes = offlinePlayer.GetComponent<RPG4.CharacterAttributes.CharacterAttributes>();
            foreach (CharacterHealthView view in Object.FindObjectsOfType<CharacterHealthView>(true))
                if (view.SourceAttributes == attributes) localHealthViews.Add(view);
            offlineWasActive = offlinePlayer.activeSelf;
            offlineView = offlinePlayer.GetComponent<Player>()?.LocalView;
            offlinePlayer.SetActive(false);
        }
        private void RestoreOffline()
        {
            if (!sceneSuspended) return;
            sceneSuspended = false;
            if (offlinePlayer != null)
            {
                var player = offlinePlayer.GetComponent<Player>();
                foreach (CharacterHealthView view in localHealthViews) if (view != null) view.BindAttributes(player.Attributes);
                offlinePlayer.SetActive(offlineWasActive);
                player.RefreshLocalPresentationBindings();
            }
            localHealthViews.Clear();
            if (localCamera != null && offlineView != null) localCamera.BindView(offlineView);
            offlineView = null;
        }
        public override void OnStopServer()
        {
            serverLifecycleStarted = false;
            serverSceneSpawnComplete = false;
            serverWorldGeneration++;
            if (usesGameplaySceneBindings && !clientLifecycleStarted) ClearGameplaySceneBindings();
            base.OnStopServer();
            NetworkPlayerSimulation.Reset();
            PublishGameplayLifecycle();
        }
        public override void OnStopClient()
        {
            // Also covers setup failures whose fallback invokes OnStopClient without a disconnect callback.
            PreserveClientSceneCleanupOwner();
            clientLifecycleStarted = false;
            if (usesGameplaySceneBindings) ClearGameplaySceneBindings();
            // Mirror calls this BEFORE NetworkClient.Shutdown and may schedule the offline scene later.
            // Do not publish a stopped/completed signal from this callback.
            base.OnStopClient();
            if (!Mirror.NetworkServer.active) NetworkPlayerSimulation.Reset();
            PublishGameplayLifecycle();
        }
        public override void OnStopHost() { base.OnStopHost(); }
        public override void LateUpdate()
        {
            // NetworkManager advances asynchronous scene transitions here.
            base.LateUpdate();
            // Mirror's no-scene-change StartServer branch calls SpawnObjects without OnServerSceneChanged.
            // At LateUpdate the synchronous branch has returned, or the asynchronous scene work has completed.
            if (serverLifecycleStarted && NetworkServer.active && !HasMirrorSceneWork
                && IsOnlineScene(SceneManager.GetActiveScene())) serverSceneSpawnComplete = true;
            if (clientSceneChangeRequested && !NetworkClient.active && !HasMirrorSceneWork
                && !IsInvoking("ClientChangeOfflineScene")) clientSceneChangeRequested = false;
            RestoreClientOfflineSceneAfterCleanup();
            if (usesGameplaySceneBindings) RefreshGameplaySceneBindings();
            TryRestoreOffline();
        }
        private void TryRestoreOffline()
        {
            if (usesGameplaySceneBindings) return;
            if (singleton != this || Mirror.NetworkServer.active || Mirror.NetworkClient.active
                || loadingSceneAsync != null || Mirror.NetworkServer.isLoadingScene || Mirror.NetworkClient.isLoadingScene)
                return;
            // A disconnect may schedule the configured offline scene with a delay. Do not re-enable the online scene.
            if (!string.IsNullOrWhiteSpace(offlineScene) && !Mirror.Utils.IsSceneActive(offlineScene)) return;
            if (!offlineSceneObjectsRestored)
            {
                InitializeSceneObjectStates();
                foreach (var entry in offlineObjectStates)
                    if (IsLoadedSceneObject(entry.Key)) entry.Key.SetActive(entry.Value);
                offlineSceneObjectsRestored = true;
            }
            // Mirror stop callbacks precede destruction/unregistration of the old local player.
            if (sceneSuspended) RestoreOffline();
        }
        private void InitializeSceneObjectStates()
        {
            if (sceneObjectStatesInitialized) return;
            sceneObjectStatesInitialized = true;
            foreach (OfflineSceneObject entry in offlineSceneObjects)
                if (IsLoadedSceneObject(entry?.Target)) offlineObjectStates[entry.Target] = entry.Active;
        }
        private void HideSceneObjectsForClient()
        {
            foreach (OfflineSceneObject entry in offlineSceneObjects)
                if (IsLoadedSceneObject(entry?.Target)) entry.Target.SetActive(false);
        }
        private static bool IsLoadedSceneObject(GameObject target) => target != null
            && target.scene.IsValid() && target.scene.isLoaded;

        private bool IsOnlineScene(Scene scene) => scene.IsValid() && scene.isLoaded
            && !string.IsNullOrWhiteSpace(onlineScene)
            && (scene.path == onlineScene || scene.name == onlineScene);
        private bool IsCurrentGameplaySceneBindingValid => usesGameplaySceneBindings && gameplaySceneBindings != null
            && gameplaySceneBindings.IsSceneValid && IsOnlineScene(gameplaySceneBindings.gameObject.scene)
            && gameplaySceneBindings.gameObject.scene == SceneManager.GetActiveScene();

        public bool ValidateServerGameplayWorld(out string reason)
        {
            if (!usesGameplaySceneBindings || !serverLifecycleStarted || !serverSceneSpawnComplete
                || !NetworkServer.active || HasMirrorSceneWork
                || !IsCurrentGameplaySceneBindingValid)
            { reason = "The current online server scene and its bindings are not ready."; return false; }
            if (playerPrefab == null || playerPrefab.GetComponent<NetworkIdentity>() == null
                || playerPrefab.GetComponent<NetworkPlayerController>() == null
                || playerPrefab.GetComponent<BackpackNetworkInventory>() == null
                || playerPrefab.GetComponent<BackpackNetworkEquipment>() == null
                || !playerPrefab.TryGetComponent(out OnlinePlayerIdentity profile) || !profile.enabled)
            { reason = "The configured player prefab is missing required network components."; return false; }
            if (ServerItemIds == null || !(gameplaySceneBindings.TrustedSpawnConfiguration is IServerPlayerSpawnInitializer))
            { reason = "A trusted pre-spawn initializer and server item IDs are required."; return false; }
            return gameplaySceneBindings.ValidateServerWorld(out reason);
        }

        public override Transform GetStartPosition()
        {
            if (!usesGameplaySceneBindings) return base.GetStartPosition();
            if (!ValidateServerGameplayWorld(out _)) return null;
            IReadOnlyList<NetworkStartPosition> points = gameplaySceneBindings.SpawnPoints;
            return points[(gameplaySpawnIndex++ & int.MaxValue) % points.Count].transform;
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            if (!usesGameplaySceneBindings) { base.OnServerAddPlayer(conn); return; }
            if (conn == null) return;
            // Trusted inventory/equipment callbacks can synchronously re-enter the manager while the
            // original object is still unpublished. One connection owns at most one pending spawn.
            if (!pendingPlayerEntries.Add(conn)) { DisconnectFailedPlayerEntry(conn); return; }
            GameObject player = null;
            bool published = false;
            try
            {
                if (!conn.isAuthenticated || conn.identity != null
                    || !(conn.authenticationData is AuthenticatedIdentity identity)
                    || !(authenticator is AccountAuthenticator accountAuthenticator)
                    || !accountAuthenticator.IsAuthenticatedSessionCurrent(conn)
                    || !ValidateServerGameplayWorld(out _)
                    || !(gameplaySceneBindings.TrustedSpawnConfiguration is IServerPlayerSpawnInitializer initializer))
                    throw new System.InvalidOperationException();
                long generation = serverWorldGeneration;
                GameplaySceneBindings bindings = gameplaySceneBindings;
                var itemIds = ServerItemIds;
                Transform spawn = GetStartPosition();
                if (spawn == null || !IsCurrentPlayerEntry(conn, identity, bindings, generation))
                    throw new System.InvalidOperationException();
                player = Instantiate(playerPrefab, spawn.position, spawn.rotation);
                var context = new TrustedPlayerSpawnContext(identity, bindings, spawn, itemIds, generation);
                if (!initializer.TryInitializeBeforeSpawn(player, context, out _)
                    || !ValidateServerGameplayWorld(out _) || !accountAuthenticator.IsAuthenticatedSessionCurrent(conn)
                    || player == null || bindings == null || player.scene != bindings.gameObject.scene
                    || !player.TryGetComponent(out NetworkIdentity networkIdentity) || networkIdentity.netId != 0
                    || !player.TryGetComponent(out OnlinePlayerIdentity profile) || !profile.MatchesServerIdentity(identity, generation)
                    || !IsCurrentPlayerEntry(conn, identity, bindings, generation))
                    throw new System.InvalidOperationException();
                // No user callbacks follow the final connection/world/reference check before publication.
                published = NetworkServer.AddPlayerForConnection(conn, player);
                if (!published) throw new System.InvalidOperationException();
            }
            catch (System.Exception)
            {
                ReportGameplayFailure("Trusted player initialization or entry failed.");
                DisconnectFailedPlayerEntry(conn);
            }
            finally
            {
                pendingPlayerEntries.Remove(conn);
                if (!published && player != null)
                {
                    if (player.TryGetComponent(out NetworkIdentity failedIdentity) && failedIdentity.netId != 0)
                        NetworkServer.Destroy(player);
                    else Destroy(player);
                }
            }
        }

        private bool IsCurrentPlayerEntry(NetworkConnectionToClient conn, AuthenticatedIdentity identity,
            GameplaySceneBindings bindings, long generation) => singleton == this && NetworkServer.active
            && serverLifecycleStarted && serverSceneSpawnComplete && !HasMirrorSceneWork
            && serverWorldGeneration == generation && ReferenceEquals(gameplaySceneBindings, bindings)
            && IsCurrentGameplaySceneBindingValid && conn.isAuthenticated && conn.identity == null
            && ReferenceEquals(conn.authenticationData, identity)
            && NetworkServer.connections.TryGetValue(conn.connectionId, out NetworkConnectionToClient current)
            && ReferenceEquals(current, conn);

        private void DisconnectFailedPlayerEntry(NetworkConnectionToClient conn)
        {
            if (conn == null) return;
            // Release authentication ownership now. The later transport disconnect callback is idempotent.
            try
            {
                if (authenticator is AccountAuthenticator accountAuthenticator)
                    accountAuthenticator.NotifyServerDisconnected(conn);
            }
            finally { conn.Disconnect(); }
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            if (authenticator is AccountAuthenticator accountAuthenticator)
                accountAuthenticator.NotifyServerDisconnected(conn);
            base.OnServerDisconnect(conn);
        }

        public override void OnServerChangeScene(string newSceneName)
        {
            serverSceneSpawnComplete = false;
            if (usesGameplaySceneBindings) serverWorldGeneration++;
            if (usesGameplaySceneBindings) ClearGameplaySceneBindings();
            base.OnServerChangeScene(newSceneName);
        }

        public override void OnServerSceneChanged(string sceneName)
        {
            base.OnServerSceneChanged(sceneName);
            serverSceneSpawnComplete = true;
            if (usesGameplaySceneBindings) RefreshGameplaySceneBindings();
        }

        public override void OnClientChangeScene(string newSceneName, SceneOperation sceneOperation, bool customHandling)
        {
            // Offline scene callbacks after shutdown still belong to the ended session.
            if (NetworkClient.active) EnsureClientSessionForCallback();
            clientSceneChangeRequested = true;
            pendingClientSceneName = newSceneName;
            pendingClientSceneOperation = sceneOperation;
            pendingClientSceneCustomHandling = customHandling;
            if (usesGameplaySceneBindings) ClearGameplaySceneBindings();
            base.OnClientChangeScene(newSceneName, sceneOperation, customHandling);
            PublishClientEvent(ClientSceneChanging, new ClientSceneLifecycleEvent(CurrentClientSession, newSceneName, sceneOperation, customHandling));
        }

        public override void OnClientSceneChanged()
        {
            // Keep Mirror's normal Ready/AddPlayer sequence. Frontends must not call it a second time.
            base.OnClientSceneChanged();
            clientSceneChangeRequested = false;
            if (usesGameplaySceneBindings) RefreshGameplaySceneBindings();
            PublishClientEvent(ClientSceneLoaded, new ClientSceneLifecycleEvent(CurrentClientSession,
                pendingClientSceneName ?? SceneManager.GetActiveScene().path,
                pendingClientSceneOperation, pendingClientSceneCustomHandling));
        }

        public override void OnClientDisconnect()
        {
            EnsureClientSessionForCallback();
            // Mirror moves the manager out of DDOL AFTER this callback when offlineScene is set.
            // A scene unload would then destroy the only LateUpdate owner of FinishLoadScene, leaving
            // a completed loadingSceneAsync/isLoadingScene permanently pending. The new frontend owns
            // returning to its menu after IsClientSessionStopped; Mirror still finishes all in-flight work.
            PreserveClientSceneCleanupOwner();
            // Disconnection ends gameplay before bindings are cleared. Otherwise their expected removal
            // publishes a configuration failure before ClientConnectionEnded can report the real cause.
            clientLifecycleStarted = false;
            if (usesGameplaySceneBindings) ClearGameplaySceneBindings();
            base.OnClientDisconnect();
            if (connectionEndReported) return;
            connectionEndReported = true;
            PublishClientEvent(ClientConnectionEnded, CurrentClientSession);
        }

        private void PreserveClientSceneCleanupOwner()
        {
            // Never alter legacy Main or Host's server-owned scene transitions.
            if (!usesGameplaySceneBindings || singleton != this || mode != NetworkManagerMode.ClientOnly
                || NetworkServer.active) return;
            if (!clientOwnsSceneCleanup)
            {
                clientOwnsSceneCleanup = true;
                clientSceneCleanupSessionId = clientSessionId;
                clientOfflineSceneAfterCleanup = offlineScene;
            }
            offlineScene = string.Empty;
            if (Application.isPlaying && gameObject.scene.name != "DontDestroyOnLoad")
            {
                transform.SetParent(null);
                DontDestroyOnLoad(gameObject);
            }
        }

        private void RestoreClientOfflineSceneAfterCleanup()
        {
            if (!clientOwnsSceneCleanup || singleton != this || clientSceneCleanupSessionId != clientSessionId
                || mode != NetworkManagerMode.Offline || !IsClientSessionStopped) return;
            offlineScene = clientOfflineSceneAfterCleanup;
            clientOfflineSceneAfterCleanup = null;
            clientOwnsSceneCleanup = false;
        }

        public override void OnClientError(TransportError error, string reason)
        {
            EnsureClientSessionForCallback();
            base.OnClientError(error, reason);
            PublishClientEvent(ClientFailure, new ClientFailureEvent(CurrentClientSession, error, reason, null));
        }

        public override void OnClientTransportException(System.Exception exception)
        {
            EnsureClientSessionForCallback();
            base.OnClientTransportException(exception);
            PublishClientEvent(ClientFailure, new ClientFailureEvent(CurrentClientSession, null, exception?.Message, exception));
        }

        private void OnActiveSceneChanged(Scene previous, Scene current) => RefreshGameplaySceneBindings();
        private void OnGameplaySceneUnloaded(Scene scene)
        {
            if (gameplaySceneBindings != null && gameplaySceneBindings.gameObject.scene == scene)
                ClearGameplaySceneBindings();
            RefreshGameplaySceneBindings();
        }

        private bool IsCurrentLocalController(NetworkPlayerController controller) => controller != null
            && controller.isActiveAndEnabled
            && controller.Player != null && controller.Player.isActiveAndEnabled
            && clientLifecycleStarted && NetworkClient.isConnected && NetworkClient.connection != null
            && NetworkClient.connection == sessionConnection && controller.isLocalPlayer && controller.isOwned
            && IsOnlineScene(controller.gameObject.scene) && controller.gameObject.scene == SceneManager.GetActiveScene()
            && NetworkClient.localPlayer == controller.netIdentity && NetworkClient.connection.identity == controller.netIdentity;

        private void SetBoundLocalController(NetworkPlayerController controller)
        {
            if (ReferenceEquals(boundLocalController, controller)) return;
            if (!ReferenceEquals(boundLocalController, null))
            {
                boundLocalController.InitialAuthoritativeStateChanged -= OnInitialAuthoritativeState;
                if (boundLocalController != null)
                {
                    if (boundLocalController.TryGetComponent(out BackpackNetworkInventory previousInventory))
                        previousInventory.Changed -= OnLocalInventoryChanged;
                    if (boundLocalController.TryGetComponent(out BackpackNetworkEquipment previousEquipment))
                        previousEquipment.Changed -= OnLocalInventoryChanged;
                    boundLocalController.SetLocalGameplayInputAllowed(false);
                    boundLocalController.ClearLocalInputEdges();
                }
            }
            if (gameplaySceneBindings != null) gameplaySceneBindings.UnbindLocalPlayer();
            boundLocalController = controller;
            if (boundLocalController == null) return;
            boundLocalController.SetLocalGameplayInputAllowed(false);
            boundLocalController.ClearLocalInputEdges();
            boundLocalController.InitialAuthoritativeStateChanged += OnInitialAuthoritativeState;
            if (boundLocalController.TryGetComponent(out BackpackNetworkInventory inventory))
                inventory.Changed += OnLocalInventoryChanged;
            if (boundLocalController.TryGetComponent(out BackpackNetworkEquipment equipment))
                equipment.Changed += OnLocalInventoryChanged;
        }

        private void OnLocalInventoryChanged() => RefreshGameplaySceneBindings();

        private void OnInitialAuthoritativeState(NetworkPlayerController controller)
        {
            if (controller == boundLocalController) RefreshGameplaySceneBindings();
        }

        private void RefreshGameplaySceneBindings()
        {
            if (refreshingGameplayBindings || !usesGameplaySceneBindings || singleton != this
                || (!clientLifecycleStarted && !serverLifecycleStarted)) return;
            refreshingGameplayBindings = true;
            try
            {
                if (HasMirrorSceneWork || clientSceneChangeRequested)
                {
                    ClearGameplaySceneBindings(false);
                    return;
                }
                Scene scene = SceneManager.GetActiveScene();
                GameplaySceneBindings next = IsOnlineScene(scene) ? GameplaySceneBindings.FindForScene(scene) : null;
                if (next != gameplaySceneBindings)
                {
                    // Replacing a valid binding is one observation. Do not expose its temporary null state.
                    ClearGameplaySceneBindings(false);
                    gameplaySceneBindings = next;
                    gameplaySpawnIndex = 0;
                    if (serverLifecycleStarted && next != null)
                    {
                        serverWorldGeneration++;
                        NetworkPlayerSimulation.SetAdditionalCombatTargets(next.GetCombatTargets());
                    }
                }
                NetworkPlayerController local = NetworkClient.localPlayer != null
                    ? NetworkClient.localPlayer.GetComponent<NetworkPlayerController>() : null;
                if (!IsCurrentLocalController(local)) local = null;
                SetBoundLocalController(local);
                if (gameplaySceneBindings != null && boundLocalController != null && !IsClientSceneChangePending)
                    gameplaySceneBindings.BindLocalPlayer(boundLocalController.Player);
            }
            finally { refreshingGameplayBindings = false; PublishGameplayLifecycle(); }
        }

        private void ClearGameplaySceneBindings(bool publish = true)
        {
            if (serverLifecycleStarted && !ReferenceEquals(gameplaySceneBindings, null)) serverWorldGeneration++;
            SetBoundLocalController(null);
            if (gameplaySceneBindings != null) gameplaySceneBindings.UnbindLocalPlayer();
            gameplaySceneBindings = null;
            if (usesGameplaySceneBindings && singleton == this) NetworkPlayerSimulation.SetAdditionalCombatTargets(null);
            if (publish) PublishGameplayLifecycle();
        }

        private GameplayLifecycleEvent CaptureGameplayLifecycle(bool worldReady, bool gameplayReady, string reason) =>
            new GameplayLifecycleEvent(CurrentClientSession, serverWorldGeneration, gameplayRevision,
                SceneManager.GetActiveScene(), gameplaySceneBindings, boundLocalController, worldReady, gameplayReady, reason);

        private void PublishGameplayLifecycle()
        {
            if (!usesGameplaySceneBindings || singleton != this) return;
            bool worldReady = ValidateServerGameplayWorld(out string worldReason);
            bool gameplayReady = IsLocalGameplayReady;
            // Loading and the pre-SpawnObjects startup stack are expected, including a Host's client half.
            // Only inspect/report configuration failures after server scene spawning has completed.
            string reason = string.Empty;
            if (!HasMirrorSceneWork && !clientSceneChangeRequested && IsOnlineScene(SceneManager.GetActiveScene())
                && (serverLifecycleStarted ? serverSceneSpawnComplete : clientLifecycleStarted))
            {
                if (gameplaySceneBindings == null)
                    reason = "The online scene requires exactly one enabled GameplaySceneBindings.";
                else if (serverLifecycleStarted && !worldReady)
                    reason = worldReason;
                else if (gameplaySceneBindings != null && clientLifecycleStarted
                    && !gameplaySceneBindings.ValidateClientConfiguration(out string clientReason))
                    reason = clientReason;
                else if (gameplaySceneBindings != null && clientLifecycleStarted
                    && !string.IsNullOrEmpty(gameplaySceneBindings.ClientBindingFailureReason))
                    reason = gameplaySceneBindings.ClientBindingFailureReason;
            }
            if (!gameplayReady && boundLocalController != null && boundLocalController.LocalGameplayInputAllowed)
                boundLocalController.SetLocalGameplayInputAllowed(false);
            int sceneHandle = SceneManager.GetActiveScene().handle;
            bool bindingsChanged = !ReferenceEquals(lastObservedBindings, gameplaySceneBindings)
                || lastObservedSceneHandle != sceneHandle;
            bool playerChanged = !ReferenceEquals(lastObservedPlayer, boundLocalController);
            bool worldChanged = lastWorldReady != worldReady || lastObservedWorldGeneration != serverWorldGeneration;
            bool localChanged = lastGameplayReady != gameplayReady || playerChanged || lastObservedClientSessionId != clientSessionId;
            bool failureChanged = !string.Equals(lastGameplayFailure, reason, System.StringComparison.Ordinal);
            if (!bindingsChanged && !worldChanged && !localChanged && !failureChanged) return;
            lastObservedBindings = gameplaySceneBindings; lastObservedSceneHandle = sceneHandle;
            lastObservedPlayer = boundLocalController; lastWorldReady = worldReady; lastGameplayReady = gameplayReady;
            lastObservedWorldGeneration = serverWorldGeneration; lastObservedClientSessionId = clientSessionId;
            lastGameplayFailure = reason ?? string.Empty; gameplayRevision++;
            GameplayLifecycleEvent state = CaptureGameplayLifecycle(worldReady, gameplayReady, lastGameplayFailure);
            if (bindingsChanged) PublishClientEvent(GameplaySceneBindingsChanged, state);
            if (worldChanged || bindingsChanged) PublishClientEvent(ServerGameplayWorldReadyChanged, state);
            if (localChanged || bindingsChanged) PublishClientEvent(LocalGameplayReadyChanged, state);
            if (failureChanged && !string.IsNullOrEmpty(reason)) PublishClientEvent(GameplayFailure, state);
        }

        private void ReportGameplayFailure(string reason)
        {
            gameplayRevision++;
            PublishClientEvent(GameplayFailure, CaptureGameplayLifecycle(IsServerGameplayWorldReady, IsLocalGameplayReady, reason));
        }
    }
}
