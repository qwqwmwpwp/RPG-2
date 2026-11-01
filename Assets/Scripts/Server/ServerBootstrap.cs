using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mirror;
using Mirror.Transports.Encryption;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using RPG4.Networking.Transports;
using kcp2k;
using UnityEngine;

namespace RPG4.Server
{
    public enum ServerStartupPhase { NotStarted, Validating, CheckingDatabase, LoadingWorld, Ready, Degraded, Draining, Stopped, Failed }

    /// <summary>Only the dedicated server entry scene owns this component. No startup migrations or key generation.</summary>
    [DisallowMultipleComponent]
    public sealed class ServerBootstrap : MonoBehaviour
    {
        [SerializeField] private AuthoritativeNetworkManager networkManagerPrefab;
        [SerializeField] private EntryContentManifest contentManifest;
        [SerializeField] private string defaultConfigPath = "服务器/配置/server.json";
        private static ServerBootstrap instance;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private AuthoritativeNetworkManager manager;
        private AccountAuthenticator authenticator;
        private MySqlAuthenticationProvider provider;
        private ServerConfiguration settings;
        private ServerAuthenticationCleanup cleanup;
        private Task startTask, initializationTask, stopTask, healthTask;
        private bool quitAllowed, stopping, destroyed;
        private int exitCode;
        public ServerStartupPhase Phase { get; private set; }
        public string FailureCode { get; private set; }
        public ServerStartupPhase FailurePhase { get; private set; }
        public Task StartupCompletion => startTask ?? Task.CompletedTask;
        public event Action<ServerStartupPhase> PhaseChanged;

        private void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            DontDestroyOnLoad(gameObject);
            Application.wantsToQuit += WantsToQuit;
        }
        private void Start()
        {
            if (instance != this) return;
            startTask = RunStartupAsync();
        }

        private async Task RunStartupAsync()
        {
            try { await StartServerAsync(); }
            catch (OperationCanceledException) { if (!stopping) await FailAsync("server_startup_timeout"); }
            catch (ServerOperationException exception) { await FailAsync(exception.Code); }
            catch (Exception) { await FailAsync("server_startup_failed"); }
        }

        private async Task StartServerAsync()
        {
            SetPhase(ServerStartupPhase.Validating);
            lifetime.Token.ThrowIfCancellationRequested();
            if (NetworkServer.active || NetworkClient.active || NetworkManager.singleton != null)
                throw new ServerOperationException("network_lifecycle_already_active");
            if (networkManagerPrefab == null || contentManifest == null || !contentManifest.IsValid)
                throw new ServerOperationException("entry_content_or_prefab_unconfigured");
            string hash = contentManifest.ContentHash;
            Debug.Log("Dedicated server entry protocol=" + EntryProtocol.Version + " content=" + hash + " rule=" + contentManifest.Rule + ".");
            string path = ResolveConfigurationPath();
            LoadedServerConfiguration loaded = ServerConfiguration.Load(path, Application.dataPath);
            settings = loaded.Settings;
            ServerIdentityFiles.ValidateExisting(loaded.KeyPath, Application.dataPath);
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(settings.startupTimeoutSeconds));
                provider = new MySqlAuthenticationProvider(loaded, OnlineSpawnConfiguration.Version);
                SetPhase(ServerStartupPhase.CheckingDatabase);
                startup.Token.ThrowIfCancellationRequested();
                initializationTask = provider.InitializeAsync(startup.Token);
                await initializationTask;
                startup.Token.ThrowIfCancellationRequested();
                manager = Instantiate(networkManagerPrefab);
                startup.Token.ThrowIfCancellationRequested();
                if (manager == null || NetworkManager.singleton != manager) throw new ServerOperationException("network_manager_duplicate");
                manager.headlessStartMode = HeadlessStartOptions.DoNothing;
                manager.offlineScene = string.Empty;
                manager.onlineScene = EntryProtocol.GameplayScene;
                manager.maxConnections = settings.maxConnections;
                manager.ConfigureGameplaySceneBindings(true);
                authenticator = manager.authenticator as AccountAuthenticator;
                var encryption = manager.transport as EncryptionTransport;
                if (authenticator == null || encryption == null
                    || !(encryption.Inner is ConnectionAdmissionTransport admission)
                    || !(admission.Inner is KcpTransport))
                    throw new ServerOperationException("server_network_prefab_invalid");
                admission.ConfigureLimits(settings.maxRawConnections, settings.maxRawConnectionsPerIp);
                admission.Port = checked((ushort)settings.port);
                encryption.ServerLoadKeyPairFromFile = true;
                encryption.ServerKeypairPath = loaded.KeyPath;
                authenticator.ConfigureServer(provider, settings.serverId, hash);
                authenticator.ConfigureWorldReadiness(() => manager != null && manager.IsServerGameplayWorldReady,
                    () => manager != null ? manager.ServerWorldGeneration : -1);
                cleanup = authenticator.CaptureServerCleanup();
                SetPhase(ServerStartupPhase.LoadingWorld);
                startup.Token.ThrowIfCancellationRequested();
                manager.StartServer();
                startup.Token.ThrowIfCancellationRequested();
                if (!NetworkServer.active || !encryption.ServerActive()) throw new ServerOperationException("server_listen_failed");
                while (!manager.IsServerGameplayWorldReady)
                {
                    startup.Token.ThrowIfCancellationRequested();
                    if (!NetworkServer.active || !encryption.ServerActive()) throw new ServerOperationException("server_listener_stopped");
                    await Task.Delay(50, startup.Token);
                }
                startup.Token.ThrowIfCancellationRequested();
                if (!(manager.CurrentGameplaySceneBindings?.TrustedSpawnConfiguration is OnlineSpawnConfiguration))
                    throw new ServerOperationException("server_spawn_profile_version_source_invalid");
                provider.SetWorldReady(true);
                SetPhase(ServerStartupPhase.Ready);
                startup.Token.ThrowIfCancellationRequested();
                healthTask = MonitorHealthAsync(provider, settings.healthIntervalSeconds, settings.databaseTimeoutSeconds, lifetime.Token);
            }
        }

        private string ResolveConfigurationPath()
        {
            string path = null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; ++i)
                if (string.Equals(args[i], "-serverConfig", StringComparison.OrdinalIgnoreCase))
                {
                    if (path != null || i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        throw new ServerOperationException("server_config_argument_invalid");
                    path = args[++i];
                }
            if (string.IsNullOrWhiteSpace(path)) path = Environment.GetEnvironmentVariable("RPG4_SERVER_CONFIG");
            if (string.IsNullOrWhiteSpace(path)) path = defaultConfigPath;
            // An explicit executable/project base makes launch-directory changes irrelevant.
            string root = Application.isEditor ? Path.GetDirectoryName(Application.dataPath) : AppContext.BaseDirectory;
            return Path.GetFullPath(Path.Combine(root, path));
        }

        private void Update()
        {
            if (stopping || provider == null || manager == null) return;
            bool world = NetworkServer.active && manager.IsServerGameplayWorldReady;
            world &= manager.CurrentGameplaySceneBindings?.TrustedSpawnConfiguration is OnlineSpawnConfiguration;
            provider.SetWorldReady(world);
            if (Phase == ServerStartupPhase.Ready || Phase == ServerStartupPhase.Degraded)
                SetPhase(provider.Readiness.CommitFailure == LoginResultCode.Success ? ServerStartupPhase.Ready : ServerStartupPhase.Degraded);
        }

        private static async Task MonitorHealthAsync(MySqlAuthenticationProvider current, int interval, int timeout, CancellationToken cancellation)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), cancellation).ConfigureAwait(false);
                    using (var check = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        check.CancelAfter(TimeSpan.FromSeconds(timeout));
                        await current.ProbeHealthAsync(check.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public Task StopAsync()
        {
            if (stopTask != null) return stopTask;
            stopping = true;
            // Publish a promise before synchronous stop callbacks, so reentrant StopAsync sees the same operation.
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
            _ = StopCoreAsync(completion);
            return stopTask;
        }

        private async Task StopCoreAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                SetPhase(ServerStartupPhase.Draining);
                provider?.BeginDraining();
                try { lifetime.Cancel(); } catch (Exception) { }
                if (authenticator != null) cleanup = authenticator.CaptureServerCleanup();
                if (manager != null && NetworkManager.singleton == manager && NetworkServer.active) manager.StopServer();
                Task waiting = Task.WhenAll(cleanup?.WaitForPendingAsync() ?? Task.CompletedTask,
                    provider?.WaitForStoppedWorkAsync() ?? Task.CompletedTask, healthTask ?? Task.CompletedTask,
                    ObserveManagedCleanupAsync(initializationTask ?? Task.CompletedTask));
                Task timeout = Task.Delay(TimeSpan.FromSeconds(settings?.shutdownTimeoutSeconds ?? 15));
                if (await Task.WhenAny(waiting, timeout) != waiting)
                {
                    FailureCode = "server_cleanup_timeout"; exitCode = Math.Max(exitCode, 1);
                    _ = ObserveManagedCleanupAsync(waiting); // Final observers/releases keep running after the bounded wait.
                }
                else await waiting;
                // Scene loads cannot be cancelled. Keep the stopped manager alive to advance Mirror's load operation.
                while (!timeout.IsCompleted && manager != null && NetworkManager.loadingSceneAsync != null)
                    await Task.Delay(25);
                if (NetworkManager.loadingSceneAsync != null)
                { FailureCode = "server_scene_cleanup_timeout"; exitCode = Math.Max(exitCode, 1); }
                if (cleanup != null && cleanup.ReleaseFailureCount > 0)
                { FailureCode = "server_session_release_failed"; exitCode = Math.Max(exitCode, 1); }
                provider?.FinishStop();
                if (manager != null && NetworkManager.loadingSceneAsync == null) Destroy(manager.gameObject);
                SetPhase(exitCode == 0 ? ServerStartupPhase.Stopped : ServerStartupPhase.Failed);
            }
            catch (Exception)
            {
                FailureCode = "server_shutdown_failed"; exitCode = Math.Max(exitCode, 1);
                provider?.FinishStop(); SetPhase(ServerStartupPhase.Failed);
            }
            finally { completion.TrySetResult(true); }
        }
        private async Task FailAsync(string code)
        {
            if (stopping) return;
            FailurePhase = Phase; FailureCode = code; exitCode = 1;
            Debug.LogError("Dedicated server startup failed at " + FailurePhase + ": " + code + ".");
            await StopAsync();
#if UNITY_SERVER && !UNITY_EDITOR
            quitAllowed = true;
            Application.Quit(exitCode);
#endif
        }
        private bool WantsToQuit()
        {
            if (quitAllowed || destroyed) return true;
#if UNITY_EDITOR
            return true;
#else
            _ = StopThenQuitAsync();
            return false;
#endif
        }
        private async Task StopThenQuitAsync()
        {
            await StopAsync();
            quitAllowed = true;
#if !UNITY_EDITOR
            Application.Quit(exitCode);
#endif
        }
        private static async Task ObserveManagedCleanupAsync(Task work)
        { try { await work.ConfigureAwait(false); } catch (Exception) { /* Counted by the managed cleanup owner. */ } }
        private void SetPhase(ServerStartupPhase phase)
        {
            if (Phase == phase) return;
            Phase = phase;
            var listeners = PhaseChanged;
            if (listeners == null) return;
            foreach (Action<ServerStartupPhase> listener in listeners.GetInvocationList())
                try { listener(phase); } catch (Exception) { Debug.LogError("Server lifecycle listener failed."); }
        }
        private void OnDestroy()
        {
            if (instance != this) return;
            destroyed = true; instance = null;
            Application.wantsToQuit -= WantsToQuit;
            provider?.BeginDraining();
            try { lifetime.Cancel(); } catch (Exception) { }
            if (!stopping) _ = StopAsync();
        }
    }
}
