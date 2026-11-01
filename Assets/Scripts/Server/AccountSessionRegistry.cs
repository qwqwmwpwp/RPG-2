using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RPG4.Networking.Contracts;

namespace RPG4.Server
{
    /// <summary>Single-process atomic capacity reservation; deliberately not a cross-server database lock.</summary>
    public sealed class AccountSessionRegistry
    {
        private readonly object gate = new object();
        private readonly Dictionary<ulong, string> sessions = new Dictionary<ulong, string>();
        private readonly int capacity;
        private bool closed;
        public AccountSessionRegistry(int capacity)
        { this.capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity)); }
        public bool HasCapacity { get { lock (gate) return !closed && sessions.Count < capacity; } }
        public int Count { get { lock (gate) return sessions.Count; } }

        public LoginResultCode TryAcquire(AuthenticatedIdentity identity, string serverId, string requestId, out IAuthenticatedSessionLease lease)
        {
            lease = null;
            if (identity == null || !EntryProtocol.IsAscii(serverId, EntryProtocol.MaxServerIdBytes)
                || !EntryProtocol.IsLowerHex(requestId, EntryProtocol.RequestIdLength)) return LoginResultCode.InternalError;
            lock (gate)
            {
                if (closed) return LoginResultCode.ServerNotReady;
                if (sessions.ContainsKey(identity.AccountId)) return LoginResultCode.AccountInUse;
                if (sessions.Count >= capacity) return LoginResultCode.ServerFull;
                sessions.Add(identity.AccountId, identity.SessionId);
            }
            lease = new AuthenticatedSessionLease(identity, serverId, requestId,
                () => IsCurrent(identity.AccountId, identity.SessionId),
                () => { Release(identity.AccountId, identity.SessionId); return Task.CompletedTask; });
            return LoginResultCode.Success;
        }
        private bool IsCurrent(ulong accountId, string sessionId)
        { lock (gate) return !closed && sessions.TryGetValue(accountId, out string current) && current == sessionId; }
        private void Release(ulong accountId, string sessionId)
        { lock (gate) if (sessions.TryGetValue(accountId, out string current) && current == sessionId) sessions.Remove(accountId); }
        public void Close() { lock (gate) { closed = true; sessions.Clear(); } }
    }
}
