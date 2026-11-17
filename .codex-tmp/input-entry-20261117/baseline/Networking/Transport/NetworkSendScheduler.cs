using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Mirror;
using UnityEngine;

namespace RPG4.Backpack.Network
{
    /// <summary>
    /// Main-thread application payload budget. This does not include Mirror/transport headers or retries.
    /// Reliable transactions stay outside the replaceable queue; callers own baseline cursors/recovery.
    /// </summary>
    public static class NetworkSendScheduler
    {
        // Shared baseline chunks and capped shot feedback both fit within this supported minimum.
        public const int MinimumBurstBytes = 2048;
        private sealed class ReferenceComparer : IEqualityComparer<NetworkConnectionToClient>
        {
            public bool Equals(NetworkConnectionToClient x, NetworkConnectionToClient y) => ReferenceEquals(x, y);
            public int GetHashCode(NetworkConnectionToClient value) => RuntimeHelpers.GetHashCode(value);
        }
        private sealed class Pending
        {
            internal uint Source;
            internal int Stream, Priority, Bytes;
            internal bool Latest;
            internal double FirstQueued, Deadline, Expires;
            internal Action Send;
            internal Func<bool> Valid;
        }
        private sealed class ConnectionQueue
        {
            internal readonly List<Pending> Items = new List<Pending>();
            internal int Bytes;
            internal double Tokens, Updated;
        }
        private static readonly Dictionary<NetworkConnectionToClient, ConnectionQueue> queues =
            new Dictionary<NetworkConnectionToClient, ConnectionQueue>(new ReferenceComparer());
        private static readonly List<NetworkConnectionToClient> connections = new List<NetworkConnectionToClient>();
        private static readonly List<Pending> candidates = new List<Pending>();
        private static int bytesPerSecond = 65536, burstBytes = 16384;
        private static int maxPending = 256, maxPendingBytes = 524288;
        private static bool flushing;
        public static long SentPayloadBytes { get; private set; }
        public static long ReplacedCount { get; private set; }
        public static long DroppedCount { get; private set; }
        public static long RejectedCount { get; private set; }

        public static void Configure(int bytesPerSecond, int burstBytes, int maxPendingPerConnection,
            int maxPendingBytesPerConnection)
        {
            NetworkSendScheduler.bytesPerSecond = Math.Max(1024, bytesPerSecond);
            NetworkSendScheduler.burstBytes = Math.Max(MinimumBurstBytes, burstBytes);
            maxPending = Math.Max(8, maxPendingPerConnection);
            maxPendingBytes = Math.Max(NetworkSendScheduler.burstBytes, maxPendingBytesPerConnection);
        }

        public static bool QueueLatest(NetworkConnectionToClient connection, uint sourceNetId, int streamId,
            int priority, int estimatedBytes, double maxDelaySeconds, double lifetimeSeconds,
            Action send, Func<bool> stillValid = null)
            => Enqueue(connection, sourceNetId, streamId, priority, estimatedBytes, maxDelaySeconds,
                lifetimeSeconds, send, stillValid, true);

        public static bool QueueEvent(NetworkConnectionToClient connection, uint sourceNetId, int streamId,
            int priority, int estimatedBytes, double maxDelaySeconds, double lifetimeSeconds,
            Action send, Func<bool> stillValid = null)
            => Enqueue(connection, sourceNetId, streamId, priority, estimatedBytes, maxDelaySeconds,
                lifetimeSeconds, send, stillValid, false);

        private static bool Enqueue(NetworkConnectionToClient connection, uint source, int stream, int priority,
            int bytes, double delay, double lifetime, Action send, Func<bool> valid, bool latest)
        {
            if (!IsConnected(connection) || send == null || bytes <= 0 || bytes > burstBytes)
            {
                if (latest) Cancel(connection, source, stream);
                RejectedCount++; return false;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (!queues.TryGetValue(connection, out ConnectionQueue queue))
            {
                queue = new ConnectionQueue { Tokens = burstBytes, Updated = now };
                queues.Add(connection, queue);
            }
            Pending item = null;
            if (latest)
                foreach (Pending pending in queue.Items)
                    if (pending.Latest && pending.Source == source && pending.Stream == stream)
                    { item = pending; break; }
            int oldBytes = item != null ? item.Bytes : 0;
            if ((item == null && queue.Items.Count >= maxPending) || queue.Bytes - oldBytes + bytes > maxPendingBytes)
            {
                // Never leave an older replaceable value scheduled after rejecting its replacement.
                if (item != null) Remove(queue, item);
                RejectedCount++; return false;
            }
            delay = double.IsNaN(delay) ? 0.25d : Math.Max(0.001d, Math.Min(5d, delay));
            if (item == null)
            {
                item = new Pending { Source = source, Stream = stream, Latest = latest,
                    FirstQueued = now, Deadline = now + delay };
                queue.Items.Add(item);
            }
            else
            {
                ReplacedCount++;
                // Continuous updates retain their original deadline, so low priority cannot starve.
                item.Deadline = Math.Min(item.Deadline, item.FirstQueued + delay);
            }
            queue.Bytes += bytes - oldBytes;
            item.Priority = priority; item.Bytes = bytes; item.Send = send; item.Valid = valid;
            item.Expires = lifetime > 0d ? now + lifetime : double.PositiveInfinity;
            return true;
        }

        private static bool IsConnected(NetworkConnectionToClient connection) => NetworkServer.active
            && connection != null && connection.isAuthenticated && connection.isReady
            && NetworkServer.connections.TryGetValue(connection.connectionId, out NetworkConnectionToClient current)
            && ReferenceEquals(current, connection);

        private static void Refill(ConnectionQueue queue, double now)
        {
            queue.Tokens = Math.Min(burstBytes, queue.Tokens + Math.Max(0d, now - queue.Updated) * bytesPerSecond);
            queue.Updated = now;
        }

        /// <summary>Optional accounting for reliable immediate results; it never delays or drops those results.</summary>
        public static void RecordImmediate(NetworkConnectionToClient connection, int estimatedBytes)
        {
            if (!IsConnected(connection) || estimatedBytes <= 0) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!queues.TryGetValue(connection, out ConnectionQueue queue))
            { queue = new ConnectionQueue { Tokens = burstBytes, Updated = now }; queues.Add(connection, queue); }
            Refill(queue, now);
            queue.Tokens = Math.Max(-burstBytes, queue.Tokens - estimatedBytes);
            SentPayloadBytes += estimatedBytes;
        }

        public static void Flush()
        {
            if (flushing) return;
            flushing = true;
            try
            {
                connections.Clear(); connections.AddRange(queues.Keys);
                double now = Time.realtimeSinceStartupAsDouble;
                foreach (NetworkConnectionToClient connection in connections)
                {
                    if (!queues.TryGetValue(connection, out ConnectionQueue queue)) continue;
                    if (!IsConnected(connection)) { queues.Remove(connection); continue; }
                    Refill(queue, now);
                    candidates.Clear(); candidates.AddRange(queue.Items);
                    // Freeze candidates before callbacks: chained baseline chunks wait until the next Flush.
                    candidates.Sort((a, b) => Compare(a, b, now));
                    foreach (Pending item in candidates)
                    {
                        if (!queues.TryGetValue(connection, out ConnectionQueue current) || !ReferenceEquals(queue, current)) break;
                        if (!queue.Items.Contains(item)) continue;
                        bool valid = false;
                        try { valid = IsConnected(connection) && (item.Valid == null || item.Valid()); }
                        catch (Exception exception) { Debug.LogException(exception); }
                        // Validation is caller code too; cancellation/reset during it must prevent this send.
                        if (!queues.TryGetValue(connection, out current) || !ReferenceEquals(queue, current)) break;
                        if (!queue.Items.Contains(item)) continue;
                        if (!valid || now >= item.Expires || item.Bytes > burstBytes)
                        { Remove(queue, item); DroppedCount++; continue; }
                        // The oldest overdue entry owns the next accumulated tokens, even if cheaper work fits now.
                        if (queue.Tokens < item.Bytes)
                        { if (now >= item.Deadline) break; continue; }
                        Remove(queue, item);
                        queue.Tokens -= item.Bytes;
                        try { item.Send(); SentPayloadBytes += item.Bytes; }
                        catch (Exception exception) { Debug.LogException(exception); }
                    }
                }
            }
            finally { candidates.Clear(); connections.Clear(); flushing = false; }
        }

        private static int Compare(Pending a, Pending b, double now)
        {
            bool overdueA = now >= a.Deadline, overdueB = now >= b.Deadline;
            if (overdueA != overdueB) return overdueA ? -1 : 1;
            if (overdueA) return a.Deadline.CompareTo(b.Deadline);
            int priority = b.Priority.CompareTo(a.Priority);
            return priority != 0 ? priority : a.FirstQueued.CompareTo(b.FirstQueued);
        }
        private static void Remove(ConnectionQueue queue, Pending item)
        { if (queue.Items.Remove(item)) queue.Bytes -= item.Bytes; }
        public static void Cancel(NetworkConnectionToClient connection, uint sourceNetId, int streamId)
        {
            if (connection == null || !queues.TryGetValue(connection, out ConnectionQueue queue)) return;
            for (int i = queue.Items.Count - 1; i >= 0; i--)
                if (queue.Items[i].Source == sourceNetId && queue.Items[i].Stream == streamId) Remove(queue, queue.Items[i]);
        }
        public static void CancelSource(uint sourceNetId)
        {
            foreach (ConnectionQueue queue in queues.Values)
                for (int i = queue.Items.Count - 1; i >= 0; i--)
                    if (queue.Items[i].Source == sourceNetId) Remove(queue, queue.Items[i]);
        }
        public static void CancelSource(uint sourceNetId, int streamId)
        {
            foreach (ConnectionQueue queue in queues.Values)
                for (int i = queue.Items.Count - 1; i >= 0; i--)
                    if (queue.Items[i].Source == sourceNetId && queue.Items[i].Stream == streamId) Remove(queue, queue.Items[i]);
        }
        public static void CancelConnection(NetworkConnectionToClient connection)
        { if (connection != null) queues.Remove(connection); }
        public static void Reset()
        {
            queues.Clear();
            SentPayloadBytes = ReplacedCount = DroppedCount = RejectedCount = 0;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Reset();
    }
}
