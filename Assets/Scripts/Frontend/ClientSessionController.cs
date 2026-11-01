using System;
using Mirror;
using Mirror.Transports.Encryption;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using UnityEngine;
using UnityEngine.SceneManagement;
using HierarchicalMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.Frontend
{
    public enum ClientSessionState { Bootstrapping, MenuIdle, Connecting, Authenticating, LoadingGameplay, AwaitingPlayer, Playing, Disconnecting }
    public readonly struct ClientSessionStatus
    {
        public ClientSessionState State { get; }
        public string Message { get; }
        public string Failure { get; }
        public bool IsBusy => State != ClientSessionState.MenuIdle && State != ClientSessionState.Playing;
        internal ClientSessionStatus(ClientSessionState state, string message, string failure)
        { State = state; Message = message; Failure = failure; }
    }
    /// <summary>Facts only: credentials never enter this context, a status snapshot, preferences or logs.</summary>
    internal sealed class ClientSessionContext
    {
        internal long AttemptId, BusinessGeneration, ManagerSessionId, Revision = -1;
        internal string RequestId, AuthenticatedSessionId, FirstFailure;
        internal int ManagerInstanceId;
        internal NetworkConnectionToServer Connection;
        internal bool LoginRequested, Secure, Accepted, SceneLoaded, DisconnectRequested, Ended, Quit, HasPlayed;
        internal bool StopRequested, DestroyRequested, CleanupComplete;
        internal double StartedAt, SecureAt, AcceptedAt, SceneChangingAt, SceneLoadedAt, Deadline;
    }
    [DisallowMultipleComponent, DefaultExecutionOrder(-950)]
    public sealed class ClientSessionController : MonoBehaviour
    {
        public const string MenuScene = "Assets/Scenes/MainMenu.unity";
        [SerializeField] private ClientServerCatalog serverCatalog;
        [SerializeField] private AuthoritativeNetworkManager networkManagerPrefab;
        [SerializeField] private SessionStatusView sessionStatus;
        [SerializeField, Min(1)] private float connectionTimeout = 15, authenticationTimeout = 15, sceneTimeout = 60, playerTimeout = 20;
        public static ClientSessionController Instance { get; private set; }
        public event Action<ClientSessionStatus> StatusChanged;
        public event Action CredentialsDispatched;
        public ClientSessionState State { get; private set; } = ClientSessionState.Bootstrapping;
        public ClientSessionStatus Status => new ClientSessionStatus(State, PhaseText(State), context.FirstFailure);
        public ClientServerCatalog ServerCatalog => serverCatalog;
        public bool CanSubmit => State == ClientSessionState.MenuIdle && !context.LoginRequested && !context.DisconnectRequested;
        internal readonly ClientSessionNode[] Nodes = new ClientSessionNode[8];
        private HierarchicalMachine machine;
        private ClientSessionContext context = new ClientSessionContext();
        private long generation;
        private AuthoritativeNetworkManager manager;
        private AccountAuthenticator authenticator;
        private NetworkPlayerController localPlayer;
        private AsyncOperation menuLoad;
        private bool menuLoadFailed;
        private bool ticking;
        private Action<ClientAuthenticationContext> secureHandler;
        private Action<ClientAuthenticationResult> authenticationHandler;
        private Action<ClientSceneLifecycleEvent> changingHandler, loadedHandler;
        private Action<ClientSessionIdentity> endedHandler;
        private Action<ClientFailureEvent> failureHandler;
        private Action<GameplayLifecycleEvent> gameplayHandler, gameplayFailureHandler;
        private static double Now => Time.realtimeSinceStartupAsDouble;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            machine = new HierarchicalMachine();
            machine.Initialize(new ClientSessionRoot(machine, this));
            if (sessionStatus != null) sessionStatus.Bind(this);
        }
        private void Update()
        {
            if (Instance != this || machine == null) return;
            if (State != ClientSessionState.Disconnecting && context.LoginRequested)
            {
                if (manager == null) RequestDisconnect("连接已结束，请重新登录。");
                else if (context.Accepted) ObserveGameplay(manager.CurrentGameplayLifecycle);
                // A synchronous callback can complete a phase before the HSM consumes the fact next tick.
                // Do not expire the already-completed phase while waiting for its sibling transition.
                bool phaseCompleted = (State == ClientSessionState.Connecting && context.Secure)
                    || (State == ClientSessionState.Authenticating && context.Accepted)
                    || (State == ClientSessionState.LoadingGameplay && context.SceneLoaded)
                    || (State == ClientSessionState.AwaitingPlayer && manager != null && manager.IsLocalGameplayReady);
                if (!context.DisconnectRequested && !phaseCompleted && context.Deadline > 0 && Now >= context.Deadline)
                    RequestDisconnect(ClientFailureText.Timeout(State));
            }
            ticking = true;
            try { machine.Tick(Time.unscaledDeltaTime); }
            finally { ticking = false; }
        }
        public bool ValidateConfiguration(int index, string address, int port, out string reason)
        {
            if (serverCatalog == null) { reason = "尚未配置受信任服务器目录。"; return false; }
            if (!serverCatalog.Validate(index, address, port, out reason)
                || !ClientServerCatalog.ValidateManager(networkManagerPrefab, out reason)) return false;
            if (sessionStatus == null) { reason = "连接状态界面配置不完整。"; return false; }
            if (!Application.CanStreamedLevelBeLoaded(MenuScene) || !Application.CanStreamedLevelBeLoaded(EntryProtocol.GameplayScene))
            { reason = "客户端构建场景尚未配置，暂时无法登录。"; return false; }
            reason = null; return true;
        }
        public bool TryLogin(int index, string address, int port, string username, string password, out string reason)
        {
            if (!CanSubmit) { reason = "当前连接正在处理中。"; return false; }
            if (!ValidateConfiguration(index, address, port, out reason)) return false;
            if (!EntryProtocol.IsUsername(username) || !EntryProtocol.IsUtf8(password, 1, EntryProtocol.MaxPasswordBytes))
            { reason = "用户名须为 3–32 位字母、数字或下划线；密码须为 1–256 字节。"; return false; }
            if (NetworkManager.singleton != null || NetworkClient.active || NetworkClient.isConnecting || NetworkServer.active
                || AuthoritativeNetworkManager.ObserveClientSceneChangePending())
            { reason = "上一网络会话尚未释放，请稍后再试。"; return false; }
            context = new ClientSessionContext { AttemptId = ++generation, BusinessGeneration = generation,
                RequestId = Guid.NewGuid().ToString("N"), StartedAt = Now, LoginRequested = true };
            long attempt = context.AttemptId;
            try
            {
                var server = serverCatalog.Get(index);
                manager = Instantiate(networkManagerPrefab);
                context.ManagerInstanceId = manager.GetInstanceID();
                manager.networkAddress = address;
                manager.offlineScene = MenuScene;
                manager.onlineScene = EntryProtocol.GameplayScene;
                ((PortTransport)((EncryptionTransport)manager.transport).Inner).Port = (ushort)port;
                manager.ConfigureGameplaySceneBindings(true);
                context.ManagerSessionId = manager.PrepareClientSession().SessionId;
                authenticator = (AccountAuthenticator)manager.authenticator;
                Subscribe(context.AttemptId);
                if (!authenticator.PrepareClientAttempt(context.AttemptId, context.RequestId, server.ServerId,
                    serverCatalog.ContentHash, username, password, server.PublicKeyFingerprint, out _))
                { reason = "无法准备安全登录，请检查配置。"; RequestDisconnect(reason); return false; }
                if (!IsBusinessCurrent(attempt)) { reason = context.FirstFailure; return false; }
                manager.StartClientSession();
                if (manager != null) ObserveGameplay(manager.CurrentGameplayLifecycle);
                reason = context.FirstFailure;
                return !context.DisconnectRequested;
            }
            catch (Exception)
            { reason = "连接初始化失败，请检查客户端配置。[C01]"; RequestDisconnect(reason); return false; }
            finally { password = null; }
        }
        private bool ObserveIdentity(ClientSessionIdentity identity)
        {
            if (!ReferenceEquals(identity.Manager, manager) || identity.ManagerInstanceId != context.ManagerInstanceId
                || identity.SessionId != context.ManagerSessionId) return false;
            return AdoptConnection(identity.Connection);
        }
        private bool AdoptConnection(NetworkConnectionToServer candidate)
        {
            if (candidate == null) return context.Connection == null;
            if (context.Connection == null)
            {
                if (!ReferenceEquals(candidate, NetworkClient.connection)) return false;
                context.Connection = candidate;
            }
            return ReferenceEquals(candidate, context.Connection);
        }
        private bool IsBusinessCurrent(long attempt) => Instance == this
            && context.BusinessGeneration == generation && context.AttemptId == attempt
            && !context.DisconnectRequested && manager != null && manager.GetInstanceID() == context.ManagerInstanceId
            && manager.ClientSessionId == context.ManagerSessionId;
        private bool MatchesAuthentication(ClientAuthenticationContext value, long attempt) => value != null
            && IsBusinessCurrent(attempt) && value.AttemptId == attempt && value.RequestId == context.RequestId
            && AdoptConnection(value.Connection);
        private void Subscribe(long attempt)
        {
            // Store facts before transitions: Connect/Authenticate/scene callbacks can all occur in one frame.
            secureHandler = value =>
            {
                if (!MatchesAuthentication(value, attempt)) return;
                if (!context.Secure) { context.Secure = true; context.SecureAt = Now; }
                PublishCredentialsDispatched(); // Authenticator submits immediately after this callback returns.
            };
            authenticationHandler = value =>
            {
                if (value == null || !MatchesAuthentication(value.Context, attempt)) return;
                PublishCredentialsDispatched();
                // Public view events may synchronously cancel, quit or destroy this controller.
                if (!MatchesAuthentication(value.Context, attempt)) return;
                if (!value.Succeeded) { RequestDisconnect(ClientFailureText.Authentication(value)); return; }
                if (!context.Accepted)
                { context.Accepted = true; context.AcceptedAt = Now; context.AuthenticatedSessionId = value.Response.Value.sessionId; }
            };
            changingHandler = value =>
            {
                if (!ObserveIdentity(value.Session)) return;
                CloseLocalInput();
                if (!IsBusinessCurrent(attempt)) return;
                if (value.Operation != SceneOperation.Normal || !IsGameplayScene(value.SceneName))
                { RequestDisconnect("服务器指定的场景不受支持，请更新客户端。"); return; }
                context.SceneLoaded = false;
                context.SceneChangingAt = Now;
            };
            loadedHandler = value =>
            {
                if (!ObserveIdentity(value.Session) || !IsBusinessCurrent(attempt)) return;
                if (!IsGameplayScene(value.SceneName) && SceneManager.GetActiveScene().path != EntryProtocol.GameplayScene)
                { RequestDisconnect("服务器场景无法进入，请更新客户端。"); return; }
                if (!context.SceneLoaded) { context.SceneLoaded = true; context.SceneLoadedAt = Now; }
            };
            endedHandler = value =>
            {
                if (!ObserveIdentity(value)) return;
                context.Ended = true; CloseLocalInput();
                if (IsBusinessCurrent(attempt)) RequestDisconnect("与服务器的连接已结束。");
            };
            failureHandler = value =>
            { if (ObserveIdentity(value.Session) && IsBusinessCurrent(attempt)) RequestDisconnect("无法连接服务器，请检查地址与网络。[C02]"); };
            gameplayHandler = value => { if (ObserveIdentity(value.Session) && IsBusinessCurrent(attempt)) ObserveGameplay(value); };
            gameplayFailureHandler = value =>
            {
                if (!ObserveIdentity(value.Session) || !IsBusinessCurrent(attempt) || !context.Accepted) return;
                var current = manager.CurrentGameplayLifecycle;
                if (value.Revision < context.Revision || value.Revision != current.Revision
                    || value.ServerWorldGeneration != current.ServerWorldGeneration
                    || value.SceneHandle != current.SceneHandle || !ReferenceEquals(value.LocalPlayer, current.LocalPlayer)) return;
                RequestDisconnect("场景或角色配置未就绪，请联系管理员。[G01]");
            };
            authenticator.SecureConnectionReady += secureHandler;
            authenticator.AuthenticationCompleted += authenticationHandler;
            manager.ClientSceneChanging += changingHandler; manager.ClientSceneLoaded += loadedHandler;
            manager.ClientConnectionEnded += endedHandler; manager.ClientFailure += failureHandler;
            manager.LocalGameplayReadyChanged += gameplayHandler; manager.GameplayFailure += gameplayFailureHandler;
        }
        private static bool IsGameplayScene(string scene) => scene == EntryProtocol.GameplayScene || scene == "OnlineGame";
        private void ObserveGameplay(GameplayLifecycleEvent value)
        {
            if (!context.Accepted || !IsBusinessCurrent(context.AttemptId) || !ObserveIdentity(value.Session)
                || value.Revision < context.Revision) return;
            var current = manager.CurrentGameplayLifecycle;
            if (value.Revision != current.Revision || value.ServerWorldGeneration != current.ServerWorldGeneration
                || value.SceneHandle != current.SceneHandle || !ReferenceEquals(value.LocalPlayer, current.LocalPlayer)) return;
            context.Revision = value.Revision;
            if (localPlayer != value.LocalPlayer)
            {
                CloseLocalInput();
                localPlayer = value.LocalPlayer;
                // A replacement can become ready between HSM ticks, leaving Playing active throughout.
                // Apply that state's permission to the new owner only after all current manager checks pass.
                if (State == ClientSessionState.Playing && localPlayer != null && manager.IsLocalGameplayReady)
                    localPlayer.SetLocalGameplayInputAllowed(true);
            }
            if (!manager.IsLocalGameplayReady) CloseLocalInput();
            if (!manager.IsClientSceneChangePending && value.ScenePath == EntryProtocol.GameplayScene && !context.SceneLoaded)
            { context.SceneLoaded = true; context.SceneLoadedAt = Now; }
        }
        public void Cancel()
        {
            // Connect/Authenticate may run before the hierarchical machine has left MenuIdle this frame.
            if (context.LoginRequested || (State != ClientSessionState.MenuIdle && State != ClientSessionState.Bootstrapping))
                RequestDisconnect(null);
        }
        public void RequestQuit()
        {
            context.Quit = true;
            if (CanSubmit) { Application.Quit(); return; }
            RequestDisconnect(null);
        }
        private void RequestDisconnect(string failure)
        {
            if (!string.IsNullOrEmpty(failure) && string.IsNullOrEmpty(context.FirstFailure)) context.FirstFailure = failure;
            CloseLocalInput();
            if (context.DisconnectRequested) return;
            context.DisconnectRequested = true; ++generation; // Retain identity and cleanup observations.
            PublishCredentialsDispatched();
            // In particular, cancel inside SecureConnectionReady must stop the authenticator before its
            // automatic submission after that event returns. The HSM still owns the subsequent state change.
            if (Instance == this) BeginStop();
        }
        internal ClientSessionState NextState(ClientSessionState state)
        {
            if (state != ClientSessionState.Disconnecting && context.DisconnectRequested) return ClientSessionState.Disconnecting;
            switch (state)
            {
                case ClientSessionState.Bootstrapping: return IsMenuLoaded() ? ClientSessionState.MenuIdle : state;
                case ClientSessionState.MenuIdle: return context.LoginRequested ? ClientSessionState.Connecting : state;
                case ClientSessionState.Connecting: return context.Secure ? ClientSessionState.Authenticating : state;
                case ClientSessionState.Authenticating: return context.Accepted ? ClientSessionState.LoadingGameplay : state;
                case ClientSessionState.LoadingGameplay: return context.SceneLoaded ? ClientSessionState.AwaitingPlayer : state;
                case ClientSessionState.AwaitingPlayer: return !context.SceneLoaded ? ClientSessionState.LoadingGameplay
                    : manager != null && manager.IsLocalGameplayReady ? ClientSessionState.Playing : state;
                case ClientSessionState.Playing: return !context.SceneLoaded ? ClientSessionState.LoadingGameplay
                    : manager == null || !manager.IsLocalGameplayReady ? ClientSessionState.AwaitingPlayer : state;
                case ClientSessionState.Disconnecting: return context.CleanupComplete ? ClientSessionState.MenuIdle : state;
                default: return state;
            }
        }
        internal void EnterState(ClientSessionState state)
        {
            bool readinessLost = State == ClientSessionState.Playing && state == ClientSessionState.AwaitingPlayer;
            State = state;
            switch (state)
            {
                case ClientSessionState.Connecting: context.Deadline = context.StartedAt + connectionTimeout; break;
                case ClientSessionState.Authenticating: context.Deadline = context.SecureAt + authenticationTimeout; break;
                case ClientSessionState.LoadingGameplay:
                    context.Deadline = (context.HasPlayed ? context.SceneChangingAt : context.AcceptedAt) + sceneTimeout; break;
                case ClientSessionState.AwaitingPlayer: context.Deadline = (readinessLost ? Now : context.SceneLoadedAt > 0 ? context.SceneLoadedAt : Now) + playerTimeout; break;
                default: context.Deadline = 0; break;
            }
            if (state == ClientSessionState.Playing && manager != null && manager.IsLocalGameplayReady && localPlayer != null)
            { context.HasPlayed = true; localPlayer.SetLocalGameplayInputAllowed(true); }
            if (state == ClientSessionState.Disconnecting) BeginStop();
            if (state == ClientSessionState.MenuIdle)
            { context.LoginRequested = context.DisconnectRequested = context.CleanupComplete = false; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            PublishStatus();
        }
        internal void UpdateState(ClientSessionState state)
        {
            if (Instance != this) return;
            if (state == ClientSessionState.Bootstrapping) EnsureMenu();
            if (state == ClientSessionState.Disconnecting) FinishDisconnect();
        }
        private void BeginStop()
        {
            if (context.StopRequested) return;
            CloseLocalInput(); context.StopRequested = true;
            // Only authentication subscriptions are business callbacks; manager observations stay until truly stopped.
            if (!ReferenceEquals(authenticator, null))
            { authenticator.SecureConnectionReady -= secureHandler; authenticator.AuthenticationCompleted -= authenticationHandler; }
            try { if (authenticator != null && !context.Accepted) authenticator.CancelClientAttempt(context.AttemptId, context.Connection); }
            catch (Exception) { /* manager cleanup is still required */ }
            try { if (manager != null) manager.StopClient(); }
            catch (Exception) { if (string.IsNullOrEmpty(context.FirstFailure)) context.FirstFailure = "连接清理尚未完成。[C03]"; }
        }
        private void FinishDisconnect()
        {
            bool stopped = context.ManagerInstanceId == 0
                ? !NetworkClient.active && !NetworkClient.isConnecting && NetworkClient.connection == null
                    && NetworkClient.localPlayer == null && !AuthoritativeNetworkManager.ObserveClientSceneChangePending()
                : AuthoritativeNetworkManager.ObserveClientSessionStopped(context.ManagerInstanceId, context.ManagerSessionId);
            if (!stopped) return;
            Unsubscribe();
            if (manager != null && !context.DestroyRequested)
            { context.DestroyRequested = true; Destroy(manager.gameObject); return; }
            if (manager != null || AuthoritativeNetworkManager.ObserveClientSceneChangePending()) return;
            localPlayer = null; authenticator = null; context.Connection = null;
            context.RequestId = context.AuthenticatedSessionId = null;
            if (context.Quit) { Application.Quit(); return; }
            EnsureMenu();
            if (IsMenuLoaded()) context.CleanupComplete = true;
        }
        private bool IsMenuLoaded() => SceneManager.GetActiveScene().path == MenuScene && (menuLoad == null || menuLoad.isDone)
            && !AuthoritativeNetworkManager.ObserveClientSceneChangePending();
        private void EnsureMenu()
        {
            if (IsMenuLoaded() || (menuLoad != null && !menuLoad.isDone) || menuLoadFailed
                || AuthoritativeNetworkManager.ObserveClientSceneChangePending()) return;
            try
            {
                if (!Application.CanStreamedLevelBeLoaded(MenuScene)) throw new InvalidOperationException();
                menuLoad = SceneManager.LoadSceneAsync(MenuScene, LoadSceneMode.Single);
                if (menuLoad == null) throw new InvalidOperationException();
            }
            catch (Exception)
            {
                menuLoadFailed = true;
                if (string.IsNullOrEmpty(context.FirstFailure))
                    context.FirstFailure = "主菜单尚未加入客户端构建场景，请联系管理员。[B01]";
                PublishStatus();
            }
        }
        private void CloseLocalInput()
        {
            if (localPlayer != null) localPlayer.SetLocalGameplayInputAllowed(false);
            NetworkIdentity identity = NetworkClient.localPlayer;
            if (identity != null && identity.TryGetComponent(out NetworkPlayerController current) && current != localPlayer)
                current.SetLocalGameplayInputAllowed(false);
        }
        private void Unsubscribe()
        {
            if (!ReferenceEquals(authenticator, null))
            { authenticator.SecureConnectionReady -= secureHandler; authenticator.AuthenticationCompleted -= authenticationHandler; }
            if (!ReferenceEquals(manager, null))
            {
                manager.ClientSceneChanging -= changingHandler; manager.ClientSceneLoaded -= loadedHandler;
                manager.ClientConnectionEnded -= endedHandler; manager.ClientFailure -= failureHandler;
                manager.LocalGameplayReadyChanged -= gameplayHandler; manager.GameplayFailure -= gameplayFailureHandler;
            }
        }
        private void PublishStatus()
        {
            if (sessionStatus != null) sessionStatus.Show(Status);
            var handlers = StatusChanged;
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
                try { ((Action<ClientSessionStatus>)handler)(Status); }
                catch (Exception) { Debug.LogError("A frontend status subscriber failed.", this); }
        }
        private void PublishCredentialsDispatched()
        {
            var handlers = CredentialsDispatched;
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
                try { ((Action)handler)(); }
                catch (Exception) { Debug.LogError("A frontend view subscriber failed.", this); }
        }
        public static string PhaseText(ClientSessionState state)
        {
            switch (state)
            {
                case ClientSessionState.Bootstrapping: return "正在准备主菜单…";
                case ClientSessionState.Connecting: return "正在连接服务器…";
                case ClientSessionState.Authenticating: return "正在验证账号…";
                case ClientSessionState.LoadingGameplay: return "正在进入场景…";
                case ClientSessionState.AwaitingPlayer: return "正在同步角色与界面…";
                case ClientSessionState.Playing: return "已进入游戏";
                case ClientSessionState.Disconnecting: return "正在断开连接并返回菜单…";
                default: return "请选择服务器并登录";
            }
        }
        private void OnDestroy()
        {
            if (Instance != this) return;
            ++generation; CloseLocalInput(); Unsubscribe();
            // A public status subscriber can destroy us during State.OnEnter; do not invalidate the HSM's
            // active chain while its Tick is still unwinding. This graph owns no independent resources.
            if (!ticking) machine?.Stop();
            if (manager != null && NetworkClient.active) manager.StopClient();
            Instance = null;
        }
    }
}
