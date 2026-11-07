using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RPG4.Networking.Contracts;

namespace RPG4.Server
{
    public sealed class MySqlAuthenticationProvider : IServerAuthenticationProvider
    {
        private readonly ServerConfiguration config;
        private readonly uint supportedSpawnVersion;
        private readonly MySqlAccountRepository store;
        private readonly PasswordHasher passwords;
        private readonly AuthenticationRateLimiter limiter;
        private readonly AccountSessionRegistry sessions;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly object databaseGate = new object();
        private long databaseFailureRevision;
        private bool draining;
        private PasswordRecord dummyPassword;
        private int processReady, worldReady, databaseReady, accepting, pending;
        public int PendingAuthenticationCount => Volatile.Read(ref pending);
        public int RunningOrQueuedKdfCount => passwords.PendingCount;
        public int ActiveSessionCount => sessions.Count;
        public ServerAuthenticationReadiness Readiness => new ServerAuthenticationReadiness(
            Volatile.Read(ref processReady) != 0, Volatile.Read(ref worldReady) != 0,
            Volatile.Read(ref databaseReady) != 0, Volatile.Read(ref accepting) != 0, sessions.HasCapacity);

        public MySqlAuthenticationProvider(LoadedServerConfiguration configuration, uint supportedSpawnVersion)
        {
            config = configuration.Settings; this.supportedSpawnVersion = supportedSpawnVersion;
            if (supportedSpawnVersion == 0) throw new ArgumentOutOfRangeException(nameof(supportedSpawnVersion));
            store = new MySqlAccountRepository(configuration);
            passwords = new PasswordHasher(config.kdfConcurrency, config.kdfQueueCapacity, config.passwordIterations);
            limiter = new AuthenticationRateLimiter(config); sessions = new AccountSessionRegistry(config.maxPlayers);
        }
        public async Task InitializeAsync(CancellationToken cancellation)
        {
            await store.ValidateSchemaAsync(cancellation).ConfigureAwait(false);
            dummyPassword = await passwords.HashAsync(Guid.NewGuid().ToString("N"), 1, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            lock (databaseGate)
            {
                if (draining || shutdown.IsCancellationRequested) throw new OperationCanceledException();
                Volatile.Write(ref databaseReady, 1); Volatile.Write(ref processReady, 1); Volatile.Write(ref accepting, 1);
            }
        }
        /// <summary>Main-thread world observer passes a pure boolean; workers never inspect scene objects.</summary>
        public void SetWorldReady(bool ready) => Volatile.Write(ref worldReady, ready ? 1 : 0);
        public async Task ProbeHealthAsync(CancellationToken cancellation)
        {
            bool requireFullSchema;
            long revision;
            lock (databaseGate) { requireFullSchema = databaseReady == 0; revision = databaseFailureRevision; }
            try
            {
                if (requireFullSchema) await store.ValidateSchemaAsync(cancellation).ConfigureAwait(false);
                else await store.ProbeAsync(cancellation).ConfigureAwait(false);
                lock (databaseGate)
                    if (!draining && !cancellation.IsCancellationRequested && revision == databaseFailureRevision)
                        Volatile.Write(ref databaseReady, 1);
            }
            catch (Exception) { MarkDatabaseUnavailable(); }
        }
        private void MarkDatabaseUnavailable()
        { lock (databaseGate) { Volatile.Write(ref databaseReady, 0); unchecked { databaseFailureRevision++; } } }
        public void BeginDraining()
        {
            lock (databaseGate) { draining = true; Volatile.Write(ref accepting, 0); Volatile.Write(ref worldReady, 0); }
            try { shutdown.Cancel(); } catch (Exception) { /* Only fixed state is exposed; cancellation is best effort. */ }
        }
        public void FinishStop()
        {
            BeginDraining(); Volatile.Write(ref processReady, 0); sessions.Close();
        }

        public async Task<ServerAuthenticationResult> AuthenticateAsync(ServerAuthenticationRequest request, CancellationToken cancellationToken)
        {
            if (request == null || request.ServerId != config.serverId) return Reject(LoginResultCode.InvalidRequest);
            LoginResultCode ready = Readiness.AdmissionFailure;
            if (ready != LoginResultCode.Success) return Reject(ready);
            string ip = NormalizeAddress(request.RemoteAddress);
            if (!limiter.TryEnter(ip, request.NormalizedUsername, out int retry)) return ServerAuthenticationResult.Rejected(LoginResultCode.RateLimited, retry);
            if (Interlocked.Increment(ref pending) > config.maxPendingAuthentications)
            { Interlocked.Decrement(ref pending); return ServerAuthenticationResult.Rejected(LoginResultCode.RateLimited, 1); }
            string normalizedUsername = request.NormalizedUsername, password = request.Password, requestId = request.RequestId;
            long deadlineTimestamp = request.DeadlineTimestamp;
            request = null; // Do not retain a second password-bearing DTO across DB/profile work.
            IAuthenticatedSessionLease lease = null;
            bool transferred = false;
            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token))
                using (var budget = DeadlineBudget.Create(deadlineTimestamp, linked.Token))
                {
                    CancellationToken token = budget.Token;
                    DeadlineBudget.ThrowIfExpired(deadlineTimestamp, token);
                    AccountRecord account;
                    try { account = await store.FindAccountAsync(normalizedUsername, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { MarkDatabaseUnavailable(); return Reject(LoginResultCode.DatabaseUnavailable); }
                    DeadlineBudget.ThrowIfExpired(deadlineTimestamp, token);
                    bool validRecord = account != null && PasswordHasher.IsSupported(account.Password)
                        && account.Password.Iterations <= config.maxStoredPasswordIterations;
                    PasswordVerification verification = await passwords.VerifyAsync(password,
                        validRecord ? account.Password : dummyPassword, token).ConfigureAwait(false);
                    DeadlineBudget.ThrowIfExpired(deadlineTimestamp, token);
                    if (!validRecord || verification == PasswordVerification.Invalid) return Reject(LoginResultCode.InvalidCredentials);
                    long verifiedVersion = account.Password.CredentialVersion;
                    AdmissionProfile profile = await store.ReadAdmissionProfileAsync(account.AccountId, token).ConfigureAwait(false);
                    if (!profile.AccountExists || profile.CredentialVersion != verifiedVersion) return Reject(LoginResultCode.InvalidCredentials);
                    if (profile.Disabled) return Reject(LoginResultCode.AccountDisabled);
                    if (verification == PasswordVerification.NeedsRehash)
                    {
                        PasswordRecord replacement = await passwords.HashAsync(password, verifiedVersion + 1, token,
                            account.Password.Iterations).ConfigureAwait(false);
                        try
                        {
                            if (!await store.TryUpgradeCredentialsAsync(account.AccountId, verifiedVersion, replacement, token).ConfigureAwait(false))
                                return Reject(LoginResultCode.InvalidCredentials);
                            verifiedVersion = replacement.CredentialVersion;
                        }
                        finally { Array.Clear(replacement.Hash, 0, replacement.Hash.Length); }
                        profile = await store.ReadAdmissionProfileAsync(account.AccountId, token).ConfigureAwait(false);
                        if (!profile.AccountExists || profile.CredentialVersion != verifiedVersion) return Reject(LoginResultCode.InvalidCredentials);
                        if (profile.Disabled) return Reject(LoginResultCode.AccountDisabled);
                    }
                    password = null;
                    DeadlineBudget.ThrowIfExpired(deadlineTimestamp, token);
                    if (account.AccountId == 0 || profile.CharacterId == 0 || profile.SpawnConfigVersion != supportedSpawnVersion
                        || !EntryProtocol.IsUtf8(profile.DisplayName, 1, EntryProtocol.MaxDisplayNameBytes))
                        return Reject(LoginResultCode.ProfileUnavailable);
                    ready = Readiness.CommitFailure;
                    if (ready != LoginResultCode.Success) return Reject(ready);
                    var identity = new AuthenticatedIdentity(account.AccountId, profile.CharacterId, profile.DisplayName,
                        profile.SpawnConfigVersion, Guid.NewGuid().ToString("N"));
                    LoginResultCode acquired = sessions.TryAcquire(identity, config.serverId, requestId, out lease);
                    if (acquired != LoginResultCode.Success) return Reject(acquired);
                    DeadlineBudget.ThrowIfExpired(deadlineTimestamp, token);
                    ready = Readiness.CommitFailure; // Reserved capacity is intentionally NOT rechecked here.
                    if (ready != LoginResultCode.Success) return Reject(ready);
                    transferred = true;
                    return ServerAuthenticationResult.Accepted(lease);
                }
            }
            catch (OperationCanceledException) { return Reject(LoginResultCode.AuthTimeout); }
            catch (KdfQueueFullException) { return ServerAuthenticationResult.Rejected(LoginResultCode.RateLimited, 1); }
            catch (MySqlConnector.MySqlException) { MarkDatabaseUnavailable(); return Reject(LoginResultCode.DatabaseUnavailable); }
            catch (Exception) { return Reject(LoginResultCode.InternalError); }
            finally
            {
                password = null;
                try { if (!transferred && lease != null) await lease.ReleaseAsync().ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref pending); }
            }
        }

        public async Task WaitForStoppedWorkAsync()
        {
            while (PendingAuthenticationCount != 0 || RunningOrQueuedKdfCount != 0)
                await Task.Delay(25).ConfigureAwait(false);
        }
        private static ServerAuthenticationResult Reject(LoginResultCode code) => ServerAuthenticationResult.Rejected(code);
        private static string NormalizeAddress(string address)
        {
            if (address == null || address.Length > 128) return "unknown";
            if (IPAddress.TryParse(address, out IPAddress ip)) return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
            if (Uri.TryCreate("tcp://" + address, UriKind.Absolute, out Uri uri) && IPAddress.TryParse(uri.Host.Trim('[', ']'), out ip))
                return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
            return "unknown";
        }
    }
}
