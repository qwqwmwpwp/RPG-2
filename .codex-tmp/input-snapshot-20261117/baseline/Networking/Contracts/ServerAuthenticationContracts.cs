using System;
using System.Threading;
using System.Threading.Tasks;

namespace RPG4.Networking.Contracts
{
    /// <summary>Only a server provider creates this trusted identity; never populate it from client fields.</summary>
    public sealed class AuthenticatedIdentity
    {
        public ulong AccountId { get; }
        public ulong CharacterId { get; }
        public string DisplayName { get; }
        public uint SpawnConfigVersion { get; }
        public string SessionId { get; }

        public AuthenticatedIdentity(ulong accountId, ulong characterId, string displayName,
            uint spawnConfigVersion, string sessionId)
        {
            if (accountId == 0 || characterId == 0 || spawnConfigVersion == 0
                || !EntryProtocol.IsUtf8(displayName, 0, EntryProtocol.MaxDisplayNameBytes)
                || !EntryProtocol.IsLowerHex(sessionId, EntryProtocol.SessionIdLength))
                throw new ArgumentException("Invalid authenticated identity.");
            AccountId = accountId; CharacterId = characterId; DisplayName = displayName;
            SpawnConfigVersion = spawnConfigVersion; SessionId = sessionId;
        }
    }

    public sealed class ServerAuthenticationRequest
    {
        public string RequestId { get; }
        public string NormalizedUsername { get; }
        public string Password { get; }
        public string RemoteAddress { get; }
        public string ServerId { get; }
        /// <summary>Stopwatch.GetTimestamp deadline, including time already spent waiting for the request.</summary>
        public long DeadlineTimestamp { get; }

        public ServerAuthenticationRequest(string requestId, string username, string password, string remoteAddress, string serverId,
            long deadlineTimestamp)
        {
            if (!EntryProtocol.IsLowerHex(requestId, EntryProtocol.RequestIdLength) || !EntryProtocol.IsUsername(username)
                || !EntryProtocol.IsUtf8(password, 1, EntryProtocol.MaxPasswordBytes)
                || !EntryProtocol.IsAscii(serverId, EntryProtocol.MaxServerIdBytes) || deadlineTimestamp <= 0)
                throw new ArgumentException("Invalid authentication request.");
            RequestId = requestId; NormalizedUsername = username.ToLowerInvariant(); Password = password;
            RemoteAddress = remoteAddress ?? string.Empty; ServerId = serverId;
            DeadlineTimestamp = deadlineTimestamp;
        }
    }

    /// <summary>A provider-created reservation. All members are thread-safe and independent of Unity lifetime.
    /// Identity is immutable and returns the same reference. Never reuse a lease for another successful result.
    /// IsOwnedBy binds the reservation to the exact server/request that acquired it. ReleaseAsync must be
    /// idempotent, compare account AND session, and own bounded retries/recovery even after server stop.</summary>
    public interface IAuthenticatedSessionLease
    {
        AuthenticatedIdentity Identity { get; }
        bool IsValid { get; }
        bool IsOwnedBy(string serverId, string requestId);
        Task ReleaseAsync();
    }

    /// <summary>Reusable lease with one release operation. Callbacks must be thread-safe, nonblocking and
    /// independent of Unity. The release callback owns compare-and-release plus bounded storage recovery.</summary>
    public sealed class AuthenticatedSessionLease : IAuthenticatedSessionLease
    {
        private readonly object gate = new object();
        private readonly string serverId, requestId;
        private readonly Func<bool> isCurrent;
        private readonly Func<Task> release;
        private Task releaseTask;
        public AuthenticatedIdentity Identity { get; }

        public AuthenticatedSessionLease(AuthenticatedIdentity identity, string serverId, string requestId,
            Func<bool> isCurrent, Func<Task> release)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            if (!EntryProtocol.IsAscii(serverId, EntryProtocol.MaxServerIdBytes)
                || !EntryProtocol.IsLowerHex(requestId, EntryProtocol.RequestIdLength))
                throw new ArgumentException("Invalid session ownership.");
            this.serverId = serverId; this.requestId = requestId;
            this.isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
            this.release = release ?? throw new ArgumentNullException(nameof(release));
        }

        public bool IsValid
        {
            get
            {
                lock (gate) { if (releaseTask != null) return false; }
                bool current = isCurrent();
                lock (gate) { return current && releaseTask == null; }
            }
        }

        public bool IsOwnedBy(string ownerServerId, string ownerRequestId) =>
            string.Equals(serverId, ownerServerId, StringComparison.Ordinal)
            && string.Equals(requestId, ownerRequestId, StringComparison.Ordinal);

        public Task ReleaseAsync()
        {
            TaskCompletionSource<bool> completion;
            lock (gate)
            {
                if (releaseTask != null) return releaseTask;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                releaseTask = completion.Task;
            }
            _ = CompleteReleaseAsync(release, completion);
            return completion.Task;
        }

        private static async Task CompleteReleaseAsync(Func<Task> release, TaskCompletionSource<bool> completion)
        {
            try
            {
                Task task = release();
                if (task == null) throw new InvalidOperationException("Session release returned no task.");
                await task.ConfigureAwait(false);
                completion.TrySetResult(true);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }
    }

    /// <summary>Admission capacity is distinct from the ability to commit an existing reservation.</summary>
    public readonly struct ServerAuthenticationReadiness
    {
        public bool ProcessReady { get; }
        public bool WorldReady { get; }
        public bool DatabaseReady { get; }
        /// <summary>Independent administrative gate. False while draining/closed; also prevents committing existing work.</summary>
        public bool AcceptingLogins { get; }
        /// <summary>Unreserved admission capacity only. A valid existing lease must not be rejected by this flag.</summary>
        public bool HasCapacity { get; }
        public ServerAuthenticationReadiness(bool processReady, bool worldReady, bool databaseReady, bool acceptingLogins, bool hasCapacity)
        { ProcessReady = processReady; WorldReady = worldReady; DatabaseReady = databaseReady; AcceptingLogins = acceptingLogins; HasCapacity = hasCapacity; }
        public LoginResultCode CommitFailure => !ProcessReady || !WorldReady ? LoginResultCode.ServerNotReady
            : !DatabaseReady ? LoginResultCode.DatabaseUnavailable
            : !AcceptingLogins ? LoginResultCode.ServerNotReady : LoginResultCode.Success;
        public LoginResultCode AdmissionFailure => CommitFailure != LoginResultCode.Success ? CommitFailure
            : !HasCapacity ? LoginResultCode.ServerFull : LoginResultCode.Success;
    }

    public sealed class ServerAuthenticationResult
    {
        public LoginResultCode Code { get; }
        public IAuthenticatedSessionLease Session { get; }
        public AuthenticatedIdentity Identity => Session?.Identity;
        public int RetryAfterSeconds { get; }
        private ServerAuthenticationResult(LoginResultCode code, IAuthenticatedSessionLease session, int retryAfterSeconds)
        { Code = code; Session = session; RetryAfterSeconds = retryAfterSeconds; }

        public static ServerAuthenticationResult Accepted(IAuthenticatedSessionLease session) =>
            new ServerAuthenticationResult(LoginResultCode.Success, session ?? throw new ArgumentNullException(nameof(session)), 0);

        public static ServerAuthenticationResult Rejected(LoginResultCode code, int retryAfterSeconds = 0)
        {
            if (code == LoginResultCode.Success || !EntryProtocol.IsKnownCode(code) || retryAfterSeconds < 0)
                throw new ArgumentException("Invalid authentication rejection.");
            return new ServerAuthenticationResult(code, null, retryAfterSeconds);
        }
    }

    /// <summary>
    /// S1 server implementation owns readiness, IP/username/global limits, bounded KDF work and storage.
    /// AuthenticateAsync is invoked on Unity's main thread: do not block it. Do not use Unity objects on workers.
    /// Enforce request.DeadlineTimestamp (Stopwatch.GetTimestamp units) across queue, DB and KDF stages.
    /// Only disclose Disabled/InUse after verifying the correct password. Missing users use bounded equivalent work.
    /// A successful result transfers ownership of its acquired session to the authenticator. On cancellation or
    /// exceptions before returning a result, the provider must release any session it acquired itself.
    /// Never log request/credential objects; discard password references when work completes.
    /// </summary>
    public interface IServerAuthenticationProvider
    {
        ServerAuthenticationReadiness Readiness { get; }
        Task<ServerAuthenticationResult> AuthenticateAsync(ServerAuthenticationRequest request, CancellationToken cancellationToken);
    }
}
