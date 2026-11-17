using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RPG4.LoadoutTemplate
{
    /// <summary>离线请求在拥有者、会话和通道范围共享回执，窗口释放后自动释放对应记录。</summary>
    internal sealed class LocalLoadoutReceipts
    {
        private sealed class Sessions
        {
            internal readonly ConditionalWeakTable<object, Channels> Values = new ConditionalWeakTable<object, Channels>();
        }

        private sealed class Channels
        {
            private readonly LocalLoadoutReceipts[] values = new LocalLoadoutReceipts[5];
            private readonly string sessionId;
            internal Channels(object session)
            { sessionId = (session as LoadoutWindowSession)?.Id ?? Guid.NewGuid().ToString("N"); }
            internal LocalLoadoutReceipts Get(LoadoutOperationChannel channel)
            {
                int index = (int)channel;
                if ((uint)index >= values.Length) throw new ArgumentOutOfRangeException(nameof(channel));
                return values[index] ?? (values[index] = new LocalLoadoutReceipts(sessionId));
            }
        }

        private readonly struct Receipt
        {
            internal Receipt(LoadoutOperationRequest request, LoadoutOperationOutcome outcome, string contextId)
            {
                Source = request.Source; Target = request.Target; InteractionId = request.InteractionId;
                Intent = request.Intent; Outcome = outcome; ContextId = contextId;
            }
            private object Source { get; }
            private object Target { get; }
            private long InteractionId { get; }
            private LoadoutDropIntent Intent { get; }
            private string ContextId { get; }
            internal LoadoutOperationOutcome Outcome { get; }
            internal bool Matches(LoadoutOperationRequest request, LoadoutOperationKey key, string contextId) =>
                ReferenceEquals(Source, request.Source) && ReferenceEquals(Target, request.Target)
                && InteractionId == request.InteractionId && Intent.Equals(request.Intent)
                && Outcome.Key.Equals(key) && string.Equals(ContextId, contextId, StringComparison.Ordinal);
        }

        private const int Capacity = 128;
        private static readonly ConditionalWeakTable<object, Sessions> scopes = new ConditionalWeakTable<object, Sessions>();
        private readonly Dictionary<int, Receipt> receipts = new Dictionary<int, Receipt>();
        private readonly Queue<int> order = new Queue<int>();
        private int highestRequest;

        private LocalLoadoutReceipts(string sessionId) { SessionId = sessionId; }
        internal string SessionId { get; }
        internal string FallbackEpoch { get; } = Guid.NewGuid().ToString("N");

        internal static LocalLoadoutReceipts Get(object ownerOrEndpoint, object sessionObject,
            LoadoutOperationChannel channel, object fallbackContext)
        {
            Sessions sessions = scopes.GetValue(ownerOrEndpoint, _ => new Sessions());
            Channels channels = sessions.Values.GetValue(sessionObject ?? fallbackContext, key => new Channels(key));
            return channels.Get(channel);
        }

        // 返回 true 表示已有回执或应拒绝。只有新编号会推进最高编号并进入执行。
        internal bool TryReplay(LoadoutOperationRequest request, LoadoutOperationKey key, LoadoutStateVersion current,
            out LoadoutOperationOutcome outcome, out bool replayed, string contextId = null)
        {
            outcome = null;
            replayed = false;
            if (receipts.TryGetValue(request.RequestId, out Receipt receipt))
            {
                replayed = receipt.Matches(request, key, contextId);
                outcome = replayed
                    ? new LoadoutOperationOutcome(key, receipt.Outcome.Code, receipt.Outcome.Message, receipt.Outcome.Commit, current)
                    : new LoadoutOperationOutcome(key, LoadoutResultCode.DuplicateRequestConflict,
                        "重复请求编号对应不同操作。", null, current);
                return true;
            }
            if (request.RequestId <= 0)
            {
                outcome = new LoadoutOperationOutcome(key, LoadoutResultCode.InvalidRequest, "操作编号无效。", null, current);
                return true;
            }
            if (request.RequestId <= highestRequest)
            {
                outcome = new LoadoutOperationOutcome(key, LoadoutResultCode.RequestExpired,
                    "旧操作回执已过期，请重新操作。", null, current);
                return true;
            }
            highestRequest = request.RequestId;
            return false;
        }

        internal void Store(LoadoutOperationRequest request, LoadoutOperationOutcome outcome, string contextId = null)
        {
            if (receipts.Count == Capacity) receipts.Remove(order.Dequeue());
            receipts.Add(request.RequestId, new Receipt(request, outcome, contextId));
            order.Enqueue(request.RequestId);
        }
    }
}
