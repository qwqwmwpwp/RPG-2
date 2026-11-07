using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Threading;
using System.Threading.Tasks;
using Mirror;
using Mirror.Transports.Encryption;
using RPG4.Networking.Contracts;
using UnityEngine;

namespace RPG4.Networking
{
    /// <summary>Shared entry protocol bridge. A real, injected server provider is mandatory.
    /// All public lifecycle methods and events belong to Unity's main thread.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkManager))]
    public sealed class AccountAuthenticator : NetworkAuthenticator
    {
        public event Action<ClientAuthenticationContext> SecureConnectionReady;
        public event Action<ClientAuthenticationResult> AuthenticationCompleted;

        private static AccountAuthenticator clientHandlerOwner, serverHandlerOwner;
        private const double RejectionFlushSeconds = 1;
        private NetworkManager manager;
        private bool clientRunning, serverRunning;
        private ClientAttempt clientAttempt;
        private IServerAuthenticationProvider provider;
        private string serverId, serverContentHash;
        private Func<bool> worldReady;
        private Func<long> worldGeneration;
        private readonly ServerAuthenticationCleanup serverCleanup = new ServerAuthenticationCleanup();

        /// <summary>Retain this managed handle before destruction; stop the server before awaiting its snapshot.</summary>
        public ServerAuthenticationCleanup CaptureServerCleanup() => serverCleanup;
        private readonly Dictionary<NetworkConnectionToClient, ServerAttempt> serverAttempts =
            new Dictionary<NetworkConnectionToClient, ServerAttempt>();
        private readonly List<ServerAttempt> expired = new List<ServerAttempt>();

        private sealed class ClientAttempt
        {
            public long Id;
            public LoginRequestMessage Request;
            public string Fingerprint;
            public EncryptionTransport Transport;
            public Func<PubKeyInfo, bool> Validator;
            public NetworkConnectionToServer Connection;
            public bool KeyValidated, Sent, Terminal;
            public double Deadline;
        }

        private sealed class ServerAttempt
        {
            public NetworkConnectionToClient Connection;
            public IServerAuthenticationProvider Provider;
            public readonly ServerWork Work = new ServerWork();
            public IAuthenticatedSessionLease Session;
            public string RequestId;
            public double Deadline, RejectAt;
            public long DeadlineTimestamp, WorldGeneration;
            public bool Received, Terminal, Accepted;
        }

        // Plain managed mailbox. Its observer/cleanup never touches a Unity object or a connection.
        private sealed class ServerWork
        {
            public readonly object Gate = new object();
            public readonly TaskCompletionSource<bool> Observation =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenSource Cancellation = new CancellationTokenSource();
            public ServerAuthenticationResult Result;
            public bool Started, Completed, Closed, CancellationFinished;
        }

        private void Awake() => manager = GetComponent<NetworkManager>();

        /// <summary>Call before StartClient. Installs the sole fixed-fingerprint validation callback.
        /// The caller must also clear its UI password after SecureConnectionReady returns / request dispatch.</summary>
        public bool PrepareClientAttempt(long attemptId, string requestId, string targetServerId, string contentHash,
            string username, string password, string trustedServerFingerprint, out ClientAuthenticationFailure failure)
        {
            failure = ClientAuthenticationFailure.None;
            if (NetworkClient.active || NetworkServer.active || clientRunning || clientAttempt != null)
            { failure = ClientAuthenticationFailure.Busy; return false; }
            if (manager == null) manager = GetComponent<NetworkManager>();
            var transport = manager != null ? manager.transport as EncryptionTransport : null;
            if (manager == null || manager.authenticator != this || transport == null || transport.Inner == null
                || !EntryProtocol.IsLowerHex(trustedServerFingerprint, EntryProtocol.HashLength))
            { failure = ClientAuthenticationFailure.InvalidConfiguration; return false; }
            var request = new LoginRequestMessage
            {
                protocolVersion = EntryProtocol.Version, contentHash = contentHash, serverId = targetServerId,
                requestId = requestId, username = username, password = password
            };
            if (!EntryProtocol.IsValidRequest(request))
            { failure = ClientAuthenticationFailure.InvalidInput; return false; }
            var attempt = new ClientAttempt { Id = attemptId, Request = request,
                Fingerprint = trustedServerFingerprint, Transport = transport };
            attempt.Validator = info => ValidateServerKey(attempt, info);
            transport.ClientValidateServerPubKey = EncryptionTransport.ValidationMode.Callback;
            transport.OnClientValidateServerPubKey = attempt.Validator;
            clientAttempt = attempt;
            return true;
        }

        private bool ValidateServerKey(ClientAttempt attempt, PubKeyInfo info)
        {
            if (!OwnsClientLifecycle() || !ReferenceEquals(clientAttempt, attempt) || attempt.Terminal) return false;
            attempt.Connection = NetworkClient.connection;
            bool valid = string.Equals(attempt.Fingerprint, info.Fingerprint, StringComparison.Ordinal);
            attempt.KeyValidated = valid;
            if (!valid) CompleteClient(attempt, ClientAuthenticationFailure.ServerIdentityMismatch);
            return valid;
        }

        public void CancelClientAttempt()
        {
            ClientAttempt attempt = clientAttempt;
            if (attempt == null || attempt.Terminal) return;
            CompleteClient(attempt, ClientAuthenticationFailure.Cancelled);
            ClearClientCredentials(attempt);
            // A completion subscriber may synchronously stop/recreate the session.
            if (!ReferenceEquals(clientAttempt, attempt)) return;
            if (OwnsClientLifecycle()) DisconnectClient(attempt);
            else ReleaseClientAttempt(); // Only local credentials/callbacks; never another manager's connection.
        }

        /// <summary>Identity-scoped cancellation: stale UI callbacks cannot cancel a newer attempt.</summary>
        public bool CancelClientAttempt(long attemptId, NetworkConnectionToServer connection)
        {
            ClientAttempt attempt = clientAttempt;
            if (attempt == null || attempt.Terminal || attempt.Id != attemptId
                || !ReferenceEquals(attempt.Connection, connection)) return false;
            CancelClientAttempt();
            return true;
        }

        public override void OnStartClient()
        {
            if (clientRunning) return;
            clientRunning = true;
            if (clientHandlerOwner != null && clientHandlerOwner != this) return;
            clientHandlerOwner = this;
            LoginMessageSerialization.Register();
            // Malformed messages must fail closed even if a development manager disables exception disconnects.
            NetworkClient.exceptionsDisconnect = true;
            NetworkClient.RegisterHandler<LoginResponseMessage>(OnLoginResponse, false);
        }

        public override void OnClientAuthenticate()
        {
            if (!OwnsClientLifecycle()) return;
            ClientAttempt attempt = clientAttempt;
            NetworkConnectionToServer connection = NetworkClient.connection;
            if (attempt == null || attempt.Terminal || attempt.Sent || clientHandlerOwner != this || connection == null)
            { DisconnectClient(attempt); return; }
            attempt.Connection = connection;
            attempt.Deadline = Time.realtimeSinceStartupAsDouble + EntryProtocol.ClientAuthenticationTimeoutSeconds;
            if (manager == null || manager.transport != attempt.Transport || Transport.active != attempt.Transport
                || attempt.Transport.ClientValidateServerPubKey != EncryptionTransport.ValidationMode.Callback
                || attempt.Transport.OnClientValidateServerPubKey != attempt.Validator
                || !attempt.KeyValidated || !attempt.Transport.ClientConnected())
            {
                CompleteClient(attempt, ClientAuthenticationFailure.SecureConnectionRequired);
                DisconnectClient(attempt);
                return;
            }
            Publish(SecureConnectionReady, Context(attempt));
            // A subscriber is permitted to cancel synchronously.
            if (!OwnsClientLifecycle() || !ReferenceEquals(clientAttempt, attempt)
                || attempt.Terminal || NetworkClient.connection != connection) return;
            SubmitClientAttempt(attempt.Id, connection);
        }

        /// <summary>May be called from SecureConnectionReady; automatic submission follows that event if needed.</summary>
        public bool SubmitClientAttempt(long attemptId, NetworkConnectionToServer connection)
        {
            ClientAttempt attempt = clientAttempt;
            if (!OwnsClientLifecycle() || attempt == null || attempt.Id != attemptId || attempt.Terminal || attempt.Sent
                || !ReferenceEquals(attempt.Connection, connection) || !ReferenceEquals(NetworkClient.connection, connection)
                || attempt.Deadline == 0) return false;
            if (Time.realtimeSinceStartupAsDouble >= attempt.Deadline)
            {
                CompleteClient(attempt, ClientAuthenticationFailure.AuthTimeout);
                DisconnectClient(attempt);
                return false;
            }
            if (!attempt.KeyValidated || manager.transport != attempt.Transport || Transport.active != attempt.Transport
                || attempt.Transport.ClientValidateServerPubKey != EncryptionTransport.ValidationMode.Callback
                || attempt.Transport.OnClientValidateServerPubKey != attempt.Validator || !attempt.Transport.ClientConnected())
            {
                CompleteClient(attempt, ClientAuthenticationFailure.SecureConnectionRequired);
                DisconnectClient(attempt);
                return false;
            }
            attempt.Sent = true;
            try { NetworkClient.Send(attempt.Request, Channels.Reliable); }
            catch (Exception)
            {
                CompleteClient(attempt, ClientAuthenticationFailure.InternalError);
                DisconnectClient(attempt);
            }
            finally { ClearClientCredentials(attempt); }
            return !attempt.Terminal;
        }

        private void OnLoginResponse(LoginResponseMessage response, int channel)
        {
            if (!OwnsClientLifecycle()) return;
            ClientAttempt attempt = clientAttempt;
            if (attempt == null || attempt.Terminal || !attempt.Sent
                || !ReferenceEquals(NetworkClient.connection, attempt.Connection))
            { DisconnectClient(attempt); return; }
            if (Time.realtimeSinceStartupAsDouble >= attempt.Deadline)
            {
                CompleteClient(attempt, ClientAuthenticationFailure.AuthTimeout);
                DisconnectClient(attempt);
                return;
            }
            if (channel != Channels.Reliable || !EntryProtocol.IsValidResponse(response)
                || !string.Equals(response.requestId, attempt.Request.requestId, StringComparison.Ordinal))
            {
                CompleteClient(attempt, ClientAuthenticationFailure.InvalidResponse);
                DisconnectClient(attempt);
                return;
            }
            if (response.code != (ushort)LoginResultCode.Success)
            {
                CompleteClient(attempt, ClientAuthenticationFailure.ServerRejected, response);
                DisconnectClient(attempt);
                return;
            }
            if (response.protocolVersion != EntryProtocol.Version
                || !string.Equals(response.contentHash, attempt.Request.contentHash, StringComparison.Ordinal)
                || response.mapId != EntryProtocol.MapId)
            {
                CompleteClient(attempt, ClientAuthenticationFailure.InvalidResponse);
                DisconnectClient(attempt);
                return;
            }
            // Publish exactly once before Mirror callbacks can synchronously end the session.
            // Subsequent disconnects belong to the manager's session-ended lifecycle.
            attempt.Terminal = true;
            ClearClientCredentials(attempt);
            Publish(AuthenticationCompleted, new ClientAuthenticationResult(Context(attempt), ClientAuthenticationFailure.None, response));
            if (!OwnsClientLifecycle() || !ReferenceEquals(clientAttempt, attempt)
                || !ReferenceEquals(NetworkClient.connection, attempt.Connection) || !NetworkClient.isConnected) return;
            try
            {
                ClientAccept();
            }
            catch (Exception)
            {
                DisconnectClient(attempt);
            }
        }

        public override void OnStopClient()
        {
            clientRunning = false;
            if (clientHandlerOwner == this)
            {
                NetworkClient.UnregisterHandler<LoginResponseMessage>();
                clientHandlerOwner = null;
            }
            if (clientAttempt != null) CompleteClient(clientAttempt, ClientAuthenticationFailure.Disconnected);
            ReleaseClientAttempt();
        }

        private void CompleteClient(ClientAttempt attempt, ClientAuthenticationFailure failure, LoginResponseMessage? response = null)
        {
            if (attempt.Terminal) return;
            attempt.Terminal = true;
            ClearClientCredentials(attempt);
            Publish(AuthenticationCompleted, new ClientAuthenticationResult(Context(attempt), failure, response));
        }
        private static ClientAuthenticationContext Context(ClientAttempt attempt) =>
            new ClientAuthenticationContext(attempt.Id, attempt.Request.requestId, attempt.Connection);
        private static void ClearClientCredentials(ClientAttempt attempt)
        { attempt.Request.username = null; attempt.Request.password = null; }
        private void ReleaseClientAttempt()
        {
            ClientAttempt attempt = clientAttempt;
            clientAttempt = null;
            if (attempt == null) return;
            ClearClientCredentials(attempt);
            if (attempt.Transport != null && attempt.Transport.OnClientValidateServerPubKey == attempt.Validator)
                attempt.Transport.OnClientValidateServerPubKey = null;
        }
        private bool OwnsClientLifecycle() => clientRunning && ReferenceEquals(clientHandlerOwner, this)
            && manager != null && ReferenceEquals(NetworkManager.singleton, manager)
            && ReferenceEquals(manager.authenticator, this);

        private void DisconnectClient(ClientAttempt expectedAttempt)
        {
            if (!OwnsClientLifecycle() || !ReferenceEquals(clientAttempt, expectedAttempt)) return;
            NetworkConnectionToServer connection = NetworkClient.connection;
            // Before the encrypted transport is ready the owning lifecycle may have no connection yet.
            // Once a connection was captured, it must still be the exact same connection.
            if (expectedAttempt != null && expectedAttempt.Connection != null
                && !ReferenceEquals(expectedAttempt.Connection, connection)) return;
            if (connection != null) connection.Disconnect();
            else if (NetworkClient.active) NetworkClient.Disconnect();
        }
        private static void Publish<T>(Action<T> handler, T value)
        {
            if (handler == null) return;
            foreach (Action<T> subscriber in handler.GetInvocationList())
            {
                try { subscriber(value); }
                catch (Exception) { Debug.LogError("Entry authentication event subscriber failed."); }
            }
        }

        /// <summary>Configure while stopped. A null provider deliberately leaves authentication unavailable.</summary>
        public void ConfigureServer(IServerAuthenticationProvider authenticationProvider, string configuredServerId, string contentHash)
        {
            if (serverRunning || serverAttempts.Count != 0) throw new InvalidOperationException("Stop authentication before reconfiguration.");
            if (!EntryProtocol.IsAscii(configuredServerId, EntryProtocol.MaxServerIdBytes)
                || !EntryProtocol.IsLowerHex(contentHash, EntryProtocol.HashLength))
                throw new ArgumentException("Invalid server authentication configuration.");
            provider = authenticationProvider; serverId = configuredServerId; serverContentHash = contentHash;
        }

        /// <summary>Configure before server start. Both callbacks are mandatory and run only on the main thread.</summary>
        public void ConfigureWorldReadiness(Func<bool> isWorldReady, Func<long> currentWorldGeneration)
        {
            if (serverRunning || serverAttempts.Count != 0) throw new InvalidOperationException("Stop authentication before reconfiguration.");
            worldReady = isWorldReady ?? throw new ArgumentNullException(nameof(isWorldReady));
            worldGeneration = currentWorldGeneration ?? throw new ArgumentNullException(nameof(currentWorldGeneration));
        }

        public override void OnStartServer()
        {
            if (serverRunning) return;
            serverRunning = true;
            if (serverHandlerOwner != null && serverHandlerOwner != this) return;
            serverHandlerOwner = this;
            LoginMessageSerialization.Register();
            NetworkServer.exceptionsDisconnect = true;
            NetworkServer.RegisterHandler<LoginRequestMessage>(OnLoginRequest, false);
        }

        public override void OnServerAuthenticate(NetworkConnectionToClient connection)
        {
            if (manager == null) manager = GetComponent<NetworkManager>();
            if (!serverRunning || serverHandlerOwner != this || connection == null || connection.isAuthenticated
                || connection is LocalConnectionToClient
                || serverAttempts.ContainsKey(connection) || !(manager != null && manager.transport is EncryptionTransport transport)
                || Transport.active != transport || transport.Inner == null
                || !EntryProtocol.IsAscii(serverId, EntryProtocol.MaxServerIdBytes)
                || !EntryProtocol.IsLowerHex(serverContentHash, EntryProtocol.HashLength))
            { connection?.Disconnect(); return; }
            serverAttempts.Add(connection, new ServerAttempt
            {
                Connection = connection, Provider = provider,
                Deadline = Time.realtimeSinceStartupAsDouble + EntryProtocol.ServerAuthenticationTimeoutSeconds,
                DeadlineTimestamp = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * EntryProtocol.ServerAuthenticationTimeoutSeconds)
            });
        }

        private void OnLoginRequest(NetworkConnectionToClient connection, LoginRequestMessage request, int channel)
        {
            if (!serverAttempts.TryGetValue(connection, out ServerAttempt attempt) || attempt.Terminal || attempt.Received
                || channel != Channels.Reliable || !EntryProtocol.IsValidRequest(request))
            { NotifyServerDisconnected(connection); connection.Disconnect(); return; }
            attempt.Received = true;
            attempt.RequestId = request.requestId;
            if (Time.realtimeSinceStartupAsDouble >= attempt.Deadline) { Reject(attempt, LoginResultCode.AuthTimeout); return; }
            if (request.protocolVersion != EntryProtocol.Version) { Reject(attempt, LoginResultCode.VersionMismatch); return; }
            if (request.contentHash != serverContentHash) { Reject(attempt, LoginResultCode.ContentMismatch); return; }
            if (request.serverId != serverId) { Reject(attempt, LoginResultCode.InvalidRequest); return; }
            try
            {
                LoginResultCode readiness = GetReadiness(attempt, false);
                if (readiness != LoginResultCode.Success) { Reject(attempt, readiness); return; }
                attempt.WorldGeneration = worldGeneration();
                if (!IsPending(attempt)) return;
                var input = new ServerAuthenticationRequest(request.requestId, request.username, request.password,
                    connection.address, serverId, attempt.DeadlineTimestamp);
                attempt.Work.Started = true;
                // Register before invoking provider code: it may synchronously stop this lifecycle and
                // capture a cleanup snapshot before AuthenticateAsync has even returned its Task.
                serverCleanup.Track(attempt.Work.Observation.Task);
                Task<ServerAuthenticationResult> task;
                try { task = attempt.Provider.AuthenticateAsync(input, attempt.Work.Cancellation.Token); }
                catch (Exception) { task = Task.FromResult<ServerAuthenticationResult>(null); }
                input = null;
                _ = ObserveWorkAsync(attempt.Work, task, serverCleanup); // Observation above represents the entire operation.
            }
            catch (Exception) { Reject(attempt, LoginResultCode.InternalError); }
            finally { request.username = null; request.password = null; }
        }

        private static async Task ObserveWorkAsync(ServerWork work, Task<ServerAuthenticationResult> task, ServerAuthenticationCleanup cleanup)
        {
            ServerAuthenticationResult result = null;
            try
            {
                try { if (task != null) result = await task.ConfigureAwait(false); }
                catch (Exception) { /* Never retain or log a provider exception with credential references. */ }
                lock (work.Gate)
                {
                    work.Completed = true;
                    if (!work.Closed) { work.Result = result; result = null; }
                    if (work.Closed && work.CancellationFinished) DisposeCancellation(work);
                }
            }
            catch (Exception) { cleanup.RecordReleaseFailure(); }
            finally
            {
                try
                {
                    // A stopped/destroyed owner cannot strand a late reservation on Unity's context.
                    if (result?.Session != null) await ReleaseSessionAsync(result.Session, cleanup).ConfigureAwait(false);
                }
                finally { work.Observation.TrySetResult(true); }
            }
        }

        private LoginResultCode GetReadiness(ServerAttempt attempt, bool committing)
        {
            try
            {
                if (attempt.Provider == null || worldReady == null || worldGeneration == null) return LoginResultCode.ServerNotReady;
                long generation = worldGeneration();
                if (!worldReady()) return LoginResultCode.ServerNotReady;
                ServerAuthenticationReadiness readiness = attempt.Provider.Readiness;
                if (!worldReady() || worldGeneration() != generation
                    || (committing && generation != attempt.WorldGeneration)) return LoginResultCode.ServerNotReady;
                return committing ? readiness.CommitFailure : readiness.AdmissionFailure;
            }
            catch (Exception) { return LoginResultCode.ServerNotReady; }
        }

        private void CommitResult(ServerAttempt attempt, ServerAuthenticationResult result)
        {
            IAuthenticatedSessionLease session = result?.Session;
            bool transferred = false;
            try
            {
                if (!IsPending(attempt)) return;
                if (Stopwatch.GetTimestamp() >= attempt.DeadlineTimestamp)
                { Reject(attempt, LoginResultCode.AuthTimeout); return; }
                if (result == null) { Reject(attempt, LoginResultCode.InternalError); return; }
                if (result.Code != LoginResultCode.Success)
                { Reject(attempt, result.Code, result.RetryAfterSeconds); return; }
                LoginResultCode readiness = GetReadiness(attempt, true);
                if (readiness != LoginResultCode.Success) { Reject(attempt, readiness); return; }
                AuthenticatedIdentity identity = session?.Identity;
                if (session == null || identity == null || !session.IsValid || !session.IsOwnedBy(serverId, attempt.RequestId))
                { Reject(attempt, LoginResultCode.InternalError); return; }
                // Trusted provider/lease callbacks can re-enter stop or scene changes. Transfer only to the
                // still-current attempt after the final external callback has returned.
                readiness = GetReadiness(attempt, true);
                if (!IsPending(attempt)) return;
                if (Stopwatch.GetTimestamp() >= attempt.DeadlineTimestamp)
                { Reject(attempt, LoginResultCode.AuthTimeout); return; }
                if (readiness != LoginResultCode.Success) { Reject(attempt, readiness); return; }
                attempt.Session = session;
                transferred = true;
                attempt.Terminal = true;
                attempt.Accepted = true;
                CloseWork(attempt.Work, serverCleanup);
                var response = new LoginResponseMessage
                {
                    requestId = attempt.RequestId, code = (ushort)LoginResultCode.Success,
                    accountId = identity.AccountId, characterId = identity.CharacterId, displayName = identity.DisplayName,
                    protocolVersion = EntryProtocol.Version, contentHash = serverContentHash,
                    mapId = EntryProtocol.MapId, sessionId = identity.SessionId
                };
                attempt.Connection.authenticationData = identity;
                attempt.Connection.Send(response, Channels.Reliable);
                // Sending can invoke diagnostics subscribers; re-check after any synchronous callback.
                bool sessionCurrent = session.IsValid && session.IsOwnedBy(serverId, attempt.RequestId);
                readiness = GetReadiness(attempt, true);
                if (!IsLive(attempt) || readiness != LoginResultCode.Success || !sessionCurrent
                    || Stopwatch.GetTimestamp() >= attempt.DeadlineTimestamp
                    || !ReferenceEquals(attempt.Connection.authenticationData, identity))
                {
                    NotifyServerDisconnected(attempt.Connection);
                    attempt.Connection.Disconnect();
                    return;
                }
                ServerAccept(attempt.Connection);
            }
            catch (Exception)
            {
                if (transferred)
                {
                    NotifyServerDisconnected(attempt.Connection);
                    attempt.Connection.Disconnect();
                }
                else if (IsPending(attempt)) Reject(attempt, LoginResultCode.InternalError);
            }
            finally { if (!transferred && session != null) serverCleanup.Track(ReleaseSessionAsync(session, serverCleanup)); }
        }

        private bool IsPending(ServerAttempt attempt) => !attempt.Terminal && IsLive(attempt);
        private bool IsLive(ServerAttempt attempt) => serverRunning && ReferenceEquals(serverHandlerOwner, this)
            && manager != null && ReferenceEquals(NetworkManager.singleton, manager) && ReferenceEquals(manager.authenticator, this)
            && !attempt.Connection.isAuthenticated && serverAttempts.TryGetValue(attempt.Connection, out ServerAttempt current)
            && ReferenceEquals(current, attempt) && NetworkServer.connections.TryGetValue(attempt.Connection.connectionId, out NetworkConnectionToClient live)
            && ReferenceEquals(live, attempt.Connection);

        private void Reject(ServerAttempt attempt, LoginResultCode code, int retryAfterSeconds = 0)
        {
            if (attempt.Terminal) return;
            attempt.Terminal = true;
            CloseWork(attempt.Work, serverCleanup);
            attempt.RejectAt = Time.realtimeSinceStartupAsDouble + RejectionFlushSeconds;
            if (EntryProtocol.IsLowerHex(attempt.RequestId, EntryProtocol.RequestIdLength))
            {
                try { attempt.Connection.Send(EntryProtocol.Failure(attempt.RequestId, code, serverContentHash, retryAfterSeconds), Channels.Reliable); }
                catch (Exception) { attempt.RejectAt = 0; }
            }
            else attempt.RejectAt = 0;
        }

        private void Update()
        {
            ClientAttempt client = clientAttempt;
            if (client != null && !client.Terminal && client.Deadline != 0
                && Time.realtimeSinceStartupAsDouble >= client.Deadline)
            {
                CompleteClient(client, ClientAuthenticationFailure.AuthTimeout);
                DisconnectClient(client);
            }
            if (!serverRunning) return;
            double now = Time.realtimeSinceStartupAsDouble;
            expired.Clear();
            expired.AddRange(serverAttempts.Values); // Connection callbacks may synchronously mutate the dictionary.
            foreach (ServerAttempt attempt in expired)
            {
                if (attempt.Accepted) continue;
                ServerAuthenticationResult result = null;
                bool completed;
                lock (attempt.Work.Gate)
                {
                    completed = attempt.Work.Started && attempt.Work.Completed && !attempt.Work.Closed;
                    if (completed) { result = attempt.Work.Result; attempt.Work.Result = null; }
                }
                if (completed) CommitResult(attempt, result);
                if (attempt.Accepted || !serverAttempts.TryGetValue(attempt.Connection, out ServerAttempt current)
                    || !ReferenceEquals(current, attempt)) continue;
                if (!attempt.Terminal && now < attempt.Deadline) continue;
                if (!attempt.Terminal) Reject(attempt, LoginResultCode.AuthTimeout);
                if (now < attempt.RejectAt) continue;
                NotifyServerDisconnected(attempt.Connection);
                attempt.Connection.Disconnect();
            }
            expired.Clear();
        }

        /// <summary>The manager must call this for every server disconnect, including authenticated connections.</summary>
        public void NotifyServerDisconnected(NetworkConnectionToClient connection)
        {
            if (connection == null || !serverAttempts.TryGetValue(connection, out ServerAttempt attempt)) return;
            serverAttempts.Remove(connection);
            attempt.Terminal = true;
            CloseWork(attempt.Work, serverCleanup);
            IAuthenticatedSessionLease session = attempt.Session;
            attempt.Session = null;
            // This connection is no longer owned; its trusted identity must never outlive the lease.
            connection.authenticationData = null;
            if (session != null) serverCleanup.Track(ReleaseSessionAsync(session, serverCleanup));
        }

        /// <summary>Server-only trusted identity/lease check for player creation. Call only on Unity's main thread.</summary>
        public bool IsAuthenticatedSessionCurrent(NetworkConnectionToClient connection)
        {
            if (!serverRunning || !ReferenceEquals(serverHandlerOwner, this) || connection == null || !connection.isAuthenticated
                || manager == null || !ReferenceEquals(NetworkManager.singleton, manager) || !ReferenceEquals(manager.authenticator, this)
                || !serverAttempts.TryGetValue(connection, out ServerAttempt attempt) || !attempt.Accepted
                || !NetworkServer.connections.TryGetValue(connection.connectionId, out NetworkConnectionToClient live)
                || !ReferenceEquals(live, connection)) return false;
            try
            {
                return attempt.Session != null && attempt.Session.IsValid
                    && attempt.Session.IsOwnedBy(serverId, attempt.RequestId)
                    && worldReady != null && worldGeneration != null && worldReady() && worldGeneration() == attempt.WorldGeneration
                    && ReferenceEquals(attempt.Session.Identity, connection.authenticationData);
            }
            catch (Exception) { return false; }
        }

        public override void OnStopServer()
        {
            serverRunning = false;
            if (serverHandlerOwner == this)
            {
                NetworkServer.UnregisterHandler<LoginRequestMessage>();
                serverHandlerOwner = null;
            }
            var connections = new List<NetworkConnectionToClient>(serverAttempts.Keys);
            foreach (NetworkConnectionToClient connection in connections) NotifyServerDisconnected(connection);
        }

        private static void CloseWork(ServerWork work, ServerAuthenticationCleanup cleanup)
        {
            ServerAuthenticationResult abandoned;
            CancellationTokenSource cancellation;
            lock (work.Gate)
            {
                if (work.Closed) return;
                work.Closed = true;
                abandoned = work.Result;
                work.Result = null;
                cancellation = work.Completed ? null : work.Cancellation;
            }
            try { cancellation?.Cancel(); }
            catch (Exception) { /* Provider callback exception text is never logged. */ }
            finally
            {
                lock (work.Gate)
                {
                    work.CancellationFinished = true;
                    if (!work.Started || work.Completed) DisposeCancellation(work);
                }
            }
            if (abandoned?.Session != null) cleanup.Track(ReleaseSessionAsync(abandoned.Session, cleanup));
        }
        private static void DisposeCancellation(ServerWork work)
        {
            work.Cancellation?.Dispose();
            work.Cancellation = null;
        }
        private static async Task ReleaseSessionAsync(IAuthenticatedSessionLease session, ServerAuthenticationCleanup cleanup)
        {
            try { await session.ReleaseAsync().ConfigureAwait(false); }
            catch (Exception) { cleanup.RecordReleaseFailure(); /* Lease implementation owns storage retry/recovery. */ }
        }
        private void OnDestroy()
        {
            OnStopClient();
            OnStopServer();
        }
    }
}
