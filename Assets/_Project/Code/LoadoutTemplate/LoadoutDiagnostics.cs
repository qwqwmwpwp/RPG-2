using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Unity.Profiling;

namespace RPG4.LoadoutTemplate
{
    public enum LoadoutDiagnosticStage { Prepare, Encode, Commit, Notify }

    /// <summary>
    /// Process-local, opt-in operation diagnostics. No payloads or rejection messages are retained.
    /// Stage scopes must be disposed synchronously on their originating thread, normally with using.
    /// </summary>
    public static class LoadoutDiagnostics
    {
        private const int PendingCapacity = 128, RecentCapacity = 64, CodeCapacity = 32, KindCapacity = 8;
        private const double RequestLifetimeSeconds = 120;
        private static readonly object Gate = new object();
        private static readonly ProfilerMarker[] Markers =
        {
            new ProfilerMarker("Loadout.Prepare"), new ProfilerMarker("Loadout.Encode"),
            new ProfilerMarker("Loadout.Commit"), new ProfilerMarker("Loadout.Notify")
        };
        private static readonly Timing[] Stages = new Timing[4];
        private static readonly long[] Rejections = new long[CodeCapacity];
        private static readonly SnapshotKind[] Kinds = new SnapshotKind[KindCapacity];
        private static readonly PendingRequest[] Pending = new PendingRequest[PendingCapacity];
        private static readonly RecentRecord[] Recent = new RecentRecord[RecentCapacity];
        private static volatile bool enabled;
        private static volatile int generation;
        private static long outcomes, successes, rejected, replays, encodings, encodedBytes, resyncs;
        private static long requests, unmatchedReceipts, expiredRequests, evictedRequests, cancelledRequests;
        private static int recentNext, recentCount, pendingCount;
        private static Timing requestLatency;

        public static bool Enabled
        {
            get => enabled;
            set
            {
                lock (Gate)
                {
                    if (enabled == value) return;
                    enabled = value;
                    unchecked { generation++; }
                    // A receipt after a capture gap must never be paired with an earlier request.
                    if (!value) ClearPending();
                }
            }
        }

        /// <summary>Returns a value type; retain it as Scope/var, rather than boxing to IDisposable.</summary>
        public static Scope Begin(LoadoutDiagnosticStage stage)
        {
            int index = (int)stage;
            if (!enabled || (uint)index >= Stages.Length) return default;
            int capturedGeneration = generation;
            long started = Stopwatch.GetTimestamp();
            Markers[index].Begin();
            return new Scope(index, capturedGeneration, started);
        }

        public struct Scope : IDisposable
        {
            private readonly int stage, captureGeneration;
            private readonly long started;
            private bool active;

            internal Scope(int stage, int captureGeneration, long started)
            { this.stage = stage; this.captureGeneration = captureGeneration; this.started = started; active = true; }

            public void Dispose()
            {
                if (!active) return;
                active = false;
                // Do not gate End on Enabled: capture may have been disabled/reset inside this scope.
                try { Markers[stage].End(); }
                finally
                {
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    lock (Gate)
                        if (enabled && captureGeneration == generation) Stages[stage].Add(elapsed);
                }
            }
        }

        /// <summary>One call per actual encoding, with UTF8 application JSON bytes, before fan-out.</summary>
        public static void RecordSnapshot(LoadoutOperationKey key, string kind, int utf8Bytes)
        {
            if (!enabled || utf8Bytes < 0) return;
            lock (Gate)
            {
                if (!enabled) return;
                encodings++; encodedBytes += utf8Bytes;
                int index = FindKind(kind);
                Kinds[index].Count++; Kinds[index].Bytes += utf8Bytes;
                AddRecent(key, "snapshot", LoadoutResultCode.Unknown, utf8Bytes, Kinds[index].Name);
            }
        }

        /// <summary>One call per terminal authority outcome. Replay deliveries are counted separately.</summary>
        public static void RecordOutcome(LoadoutOperationOutcome outcome, bool replayed = false)
        {
            if (!enabled || outcome == null) return;
            lock (Gate)
            {
                if (!enabled) return;
                if (replayed) replays++;
                else
                {
                    outcomes++;
                    if (outcome.Success) successes++;
                    else { rejected++; Rejections[CodeIndex(outcome.Code)]++; }
                }
                AddRecent(outcome.Key, replayed ? "replay" : "outcome", outcome.Code);
            }
        }

        /// <summary>Start immediately before the local client's send; repeated keys keep their first start.</summary>
        public static void BeginRequest(LoadoutOperationKey key)
        {
            if (!enabled) return;
            lock (Gate)
            {
                if (!enabled) return;
                long now = Stopwatch.GetTimestamp();
                ExpirePending(now);
                if (FindPending(key) >= 0) return;
                requests++;
                int slot = -1, oldest = 0;
                for (int i = 0; i < Pending.Length; i++)
                {
                    if (!Pending[i].Active) { slot = i; break; }
                    if (Pending[i].Started < Pending[oldest].Started) oldest = i;
                }
                if (slot < 0) { slot = oldest; evictedRequests++; }
                else pendingCount++;
                Pending[slot] = new PendingRequest { Active = true, Key = key, Started = now };
            }
        }

        /// <summary>Call only for an authority receipt, not a local timeout; it does not count an outcome.</summary>
        public static void CompleteRequest(LoadoutOperationKey key, LoadoutResultCode code)
        {
            if (!enabled) return;
            lock (Gate)
            {
                if (!enabled) return;
                long now = Stopwatch.GetTimestamp();
                ExpirePending(now);
                int slot = FindPending(key);
                if (slot < 0) { unmatchedReceipts++; return; }
                long elapsed = now - Pending[slot].Started;
                Pending[slot] = default; pendingCount--;
                requestLatency.Add(elapsed);
                AddRecent(key, "receipt", code, elapsed);
            }
        }

        public static void CancelRequest(LoadoutOperationKey key)
        {
            if (!enabled) return;
            lock (Gate)
            {
                if (!enabled) return;
                int slot = FindPending(key);
                if (slot < 0) return;
                Pending[slot] = default; pendingCount--; cancelledRequests++;
            }
        }

        public static void RecordResync(LoadoutOperationKey key)
        {
            if (!enabled) return;
            lock (Gate)
            {
                if (!enabled) return;
                resyncs++;
                AddRecent(key, "resync", LoadoutResultCode.Unknown);
            }
        }

        /// <summary>Invalidates open timing samples without changing Enabled or leaving markers open.</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                unchecked { generation++; }
                Array.Clear(Stages, 0, Stages.Length); Array.Clear(Rejections, 0, Rejections.Length);
                Array.Clear(Kinds, 0, Kinds.Length); Array.Clear(Recent, 0, Recent.Length);
                ClearPending();
                outcomes = successes = rejected = replays = encodings = encodedBytes = resyncs = 0;
                requests = unmatchedReceipts = expiredRequests = evictedRequests = cancelledRequests = 0;
                recentNext = recentCount = 0; requestLatency = default;
            }
        }

        public static string Describe()
        {
            lock (Gate)
            {
                ExpirePending(Stopwatch.GetTimestamp());
                var text = new StringBuilder("库存操作诊断（进程内；启用=").Append(enabled).Append("）");
                text.Append("\n权威操作结果=").Append(outcomes).Append(" success=").Append(successes)
                    .Append(" rejected=").Append(rejected).Append(" replay deliveries=").Append(replays);
                for (int i = 0; i < Stages.Length; i++)
                {
                    text.Append('\n').Append((LoadoutDiagnosticStage)i).Append(' ');
                    AppendTiming(text, Stages[i]);
                }
                text.Append("\n实际编码次数=").Append(encodings).Append(" UTF8应用载荷=").Append(encodedBytes)
                    .Append("B（编码产物，发送前；不乘接收人数，不含消息/传输头）");
                for (int i = 0; i < Kinds.Length; i++)
                    if (Kinds[i].Count != 0)
                        text.Append("\n  ").Append(Kinds[i].Name).Append(" count=").Append(Kinds[i].Count)
                            .Append(" bytes=").Append(Kinds[i].Bytes);
                text.Append("\n按稳定结果码拒绝（不含重放）：");
                for (int i = 0; i < Rejections.Length; i++)
                    if (Rejections[i] != 0) text.Append(' ').Append((LoadoutResultCode)i).Append('=').Append(Rejections[i]);
                text.Append("\n重同步=").Append(resyncs).Append(" 本机客户端请求=").Append(requests).Append(' ');
                AppendTiming(text, requestLatency);
                text.Append(" pending=").Append(pendingCount).Append('/').Append(PendingCapacity)
                    .Append(" unmatched receipts=").Append(unmatchedReceipts).Append(" expired=").Append(expiredRequests)
                    .Append(" evicted=").Append(evictedRequests).Append(" cancelled=").Append(cancelledRequests);
                text.Append("\n阶段为同步墙钟耗时，含嵌套调用；samples 是实际样本数，不等于操作数。")
                    .Append("\n延迟为本机请求发送前到权威回执处理，含网络/排队/处理；Host 含本地连接。")
                    .Append("\n临时分配未直接计量；在 Unity Profiler 的 Loadout.* 样本查看 GC Alloc。");
                text.Append("\n最近记录=").Append(recentCount).Append('/').Append(RecentCapacity).Append("（旧记录覆盖）");
                for (int i = 0; i < recentCount; i++)
                {
                    RecentRecord record = Recent[(recentNext - recentCount + i + Recent.Length) % Recent.Length];
                    text.Append('\n').Append(record.Event).Append(" actor=").Append(record.Key.ActorNetId)
                        .Append(" epoch=").Append(record.Key.Epoch).Append(" session=").Append(record.Key.Session)
                        .Append(" channel=").Append(record.Key.Channel).Append(" request=").Append(record.Key.RequestId)
                        .Append(" code=").Append(record.Code);
                    if (record.Event == "snapshot") text.Append(" kind=").Append(record.Kind).Append(" bytes=").Append(record.Value);
                    if (record.Event == "receipt") text.Append(" latencyMs=").Append(Milliseconds(record.Value));
                }
                return text.ToString();
            }
        }

        private static void AppendTiming(StringBuilder text, Timing timing)
        {
            text.Append("samples=").Append(timing.Count).Append(" totalMs=").Append(Milliseconds(timing.Total))
                .Append(" avgMs=").Append(timing.Count == 0 ? "n/a" : Milliseconds(timing.Total / timing.Count))
                .Append(" maxMs=").Append(Milliseconds(timing.Max));
        }

        private static string Milliseconds(long ticks) =>
            (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);

        private static int CodeIndex(LoadoutResultCode code) => (uint)(int)code < CodeCapacity ? (int)code : 0;

        private static int FindKind(string kind)
        {
            // Bound both cardinality and retained labels. Snapshot callers use small static kind names.
            if (string.IsNullOrEmpty(kind) || kind.Length > 48) kind = "other";
            for (int i = 0; i < Kinds.Length - 1; i++)
            {
                if (Kinds[i].Name == kind) return i;
                if (Kinds[i].Name == null) { Kinds[i].Name = kind; return i; }
            }
            Kinds[Kinds.Length - 1].Name = "other";
            return Kinds.Length - 1;
        }

        private static int FindPending(LoadoutOperationKey key)
        {
            for (int i = 0; i < Pending.Length; i++)
                if (Pending[i].Active && SameKey(Pending[i].Key, key)) return i;
            return -1;
        }

        private static bool SameKey(LoadoutOperationKey left, LoadoutOperationKey right) =>
            left.ActorNetId == right.ActorNetId && left.Channel == right.Channel && left.RequestId == right.RequestId &&
            string.Equals(left.Epoch ?? string.Empty, right.Epoch ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(left.Session ?? string.Empty, right.Session ?? string.Empty, StringComparison.Ordinal);

        private static void ExpirePending(long now)
        {
            for (int i = 0; i < Pending.Length; i++)
                if (Pending[i].Active && (now - Pending[i].Started) / (double)Stopwatch.Frequency > RequestLifetimeSeconds)
                { Pending[i] = default; pendingCount--; expiredRequests++; }
        }

        private static void ClearPending()
        {
            cancelledRequests += pendingCount;
            Array.Clear(Pending, 0, Pending.Length); pendingCount = 0;
        }

        private static void AddRecent(LoadoutOperationKey key, string eventName, LoadoutResultCode code,
            long value = 0, string kind = null)
        {
            Recent[recentNext] = new RecentRecord { Key = key, Event = eventName, Code = code, Value = value, Kind = kind };
            recentNext = (recentNext + 1) % Recent.Length;
            if (recentCount < Recent.Length) recentCount++;
        }

        private struct Timing
        {
            public long Count, Total, Max;
            public void Add(long ticks) { Count++; Total += ticks; if (ticks > Max) Max = ticks; }
        }
        private struct SnapshotKind { public string Name; public long Count, Bytes; }
        private struct PendingRequest { public bool Active; public LoadoutOperationKey Key; public long Started; }
        private struct RecentRecord
        { public LoadoutOperationKey Key; public string Event, Kind; public LoadoutResultCode Code; public long Value; }
    }
}
