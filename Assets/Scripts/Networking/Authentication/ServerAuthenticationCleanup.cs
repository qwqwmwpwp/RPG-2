using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RPG4.Networking
{
    /// <summary>Managed lifetime handle; retain it before destroying the authenticator. After OnStopServer,
    /// WaitForPendingAsync covers outstanding provider observations and all their late lease releases.
    /// The caller chooses a bounded shutdown wait without cancelling the underlying cleanup.</summary>
    public sealed class ServerAuthenticationCleanup
    {
        private readonly object gate = new object();
        private readonly HashSet<Task> pending = new HashSet<Task>();
        private int releaseFailures;
        public int PendingTaskCount { get { lock (gate) return pending.Count; } }
        /// <summary>Monotonic count only; provider exception text is never retained by this handle.</summary>
        public int ReleaseFailureCount => Volatile.Read(ref releaseFailures);

        public Task WaitForPendingAsync()
        {
            Task[] snapshot;
            lock (gate) { snapshot = new Task[pending.Count]; pending.CopyTo(snapshot); }
            return WaitSnapshotAsync(snapshot);
        }

        internal void Track(Task task)
        {
            if (task == null) return;
            lock (gate) pending.Add(task);
            _ = ObserveAsync(task);
        }

        internal void RecordReleaseFailure() => Interlocked.Increment(ref releaseFailures);

        private async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { RecordReleaseFailure(); }
            finally { lock (gate) pending.Remove(task); }
        }

        private static async Task WaitSnapshotAsync(Task[] snapshot)
        {
            try { await Task.WhenAll(snapshot).ConfigureAwait(false); }
            catch (Exception) { /* ObserveAsync counts failures without exposing credential-bearing exceptions. */ }
        }
    }
}
