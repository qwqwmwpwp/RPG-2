using System.Collections.Generic;

namespace RPG4.Networking
{
    /// <summary>One report is a 20ms input step, never a packet-sized simulation budget.</summary>
    public sealed class ServerMovementTimeline
    {
        public const int Capacity = 64;
        private struct Entry { public PlayerMovementReport Report; public long Received; }
        private readonly SortedDictionary<ulong, Entry> queued = new SortedDictionary<ulong, Entry>();
        private readonly Dictionary<ulong, long> receipts = new Dictionary<ulong, long>();
        private readonly Queue<ulong> receiptOrder = new Queue<ulong>();
        private uint epoch, correction;
        private long originTick, lastTakeTick = -1, gapSince = -1, takenReceivedTick;
        private ulong originStep;
        private bool discontinuous;
        private PlayerMovementReport taken;
        public int TimeoutTicks { get; set; } = 13;
        public int GapWaitTicks { get; set; } = 2;
        public ulong ProcessedStep { get; private set; }
        public ulong AcceptedStep { get; private set; }
        public ulong ProcessedInputSequence { get; private set; }
        public MovementRejectReason RebaseReason { get; private set; }
        public void ClearSession()
        {
            queued.Clear(); receipts.Clear(); receiptOrder.Clear(); taken = default;
            epoch = correction = 0; ProcessedStep = AcceptedStep = ProcessedInputSequence = originStep = 0;
            originTick = 0; lastTakeTick = gapSince = -1; discontinuous = false; RebaseReason = MovementRejectReason.None;
        }
        public void Reset(in PlayerMovementBaseline baseline, long serverTick)
        {
            queued.Clear(); receipts.Clear(); receiptOrder.Clear(); taken = default;
            epoch = baseline.Epoch; correction = baseline.CorrectionId;
            ProcessedStep = AcceptedStep = originStep = baseline.Step;
            ProcessedInputSequence = System.Math.Max(ProcessedInputSequence, baseline.ProcessedInputSequence);
            originTick = serverTick; lastTakeTick = gapSince = -1; discontinuous = false; RebaseReason = MovementRejectReason.None;
        }
        public bool Enqueue(in PlayerMovementReport report, long receivedTick)
        {
            if (!report.IsValid || report.Epoch != epoch || report.CorrectionId > correction || report.Step <= ProcessedStep
                || report.Input.Sequence <= ProcessedInputSequence || report.Step - ProcessedStep > Capacity) return false;
            if (queued.TryGetValue(report.Step, out Entry old))
            {
                if (!MovementValues.SameInput(old.Report.Input, report.Input))
                { RebaseReason = MovementRejectReason.InvalidInput; return false; }
                if (report.CorrectionId < old.Report.CorrectionId) return false;
                if (report.CorrectionId == old.Report.CorrectionId)
                {
                    if (!MovementValues.SamePrediction(old.Report, report)) RebaseReason = MovementRejectReason.Kinematics;
                    return false;
                }
                queued[report.Step] = new Entry { Report = report, Received = old.Received };
                return true;
            }
            if (queued.Count >= Capacity) { RebaseReason = MovementRejectReason.HistoryOverflow; return false; }
            queued.Add(report.Step, new Entry { Report = report, Received = receivedTick });
            return true;
        }
        public MovementTakeResult Take(long serverTick, out PlayerMovementReport report)
        {
            report = default;
            if (serverTick == lastTakeTick) return MovementTakeResult.None;
            if (RebaseReason != MovementRejectReason.None) return MovementTakeResult.RebaseRequired;
            ulong next = ProcessedStep + 1;
            if (!queued.TryGetValue(next, out Entry entry))
            {
                if (queued.Count == 0) { gapSince = -1; return MovementTakeResult.None; }
                if (gapSince < 0)
                    foreach (Entry value in queued.Values) { gapSince = value.Received; break; }
                if (serverTick - gapSince >= GapWaitTicks)
                { RebaseReason = MovementRejectReason.MissingInput; return MovementTakeResult.RebaseRequired; }
                return MovementTakeResult.None;
            }
            if (serverTick - entry.Received > TimeoutTicks)
            { RebaseReason = MovementRejectReason.MissingInput; return MovementTakeResult.RebaseRequired; }
            if (serverTick <= originTick || next - originStep > (ulong)(serverTick - originTick)) return MovementTakeResult.None;
            if (entry.Report.Input.Sequence <= ProcessedInputSequence)
            { RebaseReason = MovementRejectReason.InvalidInput; return MovementTakeResult.RebaseRequired; }
            queued.Remove(next); gapSince = -1; lastTakeTick = serverTick;
            takenReceivedTick = entry.Received; taken = report = entry.Report;
            return MovementTakeResult.Ready;
        }
        public void CommitResult(in PlayerMovementReport report, bool accepted)
        {
            if (taken.Step == 0 || report.Epoch != epoch || report.Step != taken.Step || report.Step != ProcessedStep + 1) return;
            ProcessedStep = report.Step; ProcessedInputSequence = report.Input.Sequence;
            if (accepted && !discontinuous) AcceptedStep = ProcessedStep;
            else discontinuous = true;
            taken = default;
        }
        /// <summary>Retirement closes discarded inputs without issuing consumption receipts or movement ACKs.</summary>
        public void RetirePendingInputs()
        {
            foreach (Entry entry in queued.Values)
                ProcessedInputSequence = System.Math.Max(ProcessedInputSequence, entry.Report.Input.Sequence);
            if (taken.Step != 0) ProcessedInputSequence = System.Math.Max(ProcessedInputSequence, taken.Input.Sequence);
            queued.Clear(); taken = default;
        }
        public void CommitCorrection(in PlayerMovementBaseline baseline)
        {
            if (baseline.Epoch != epoch || baseline.CorrectionId <= correction || baseline.Step != ProcessedStep) return;
            correction = baseline.CorrectionId; AcceptedStep = ProcessedStep; discontinuous = false;
        }
        public void RecordConsumedInput(ulong sequence, long receivedTick)
        {
            if (sequence == 0 || receipts.ContainsKey(sequence)) return;
            if (receiptOrder.Count == Capacity) receipts.Remove(receiptOrder.Dequeue());
            receiptOrder.Enqueue(sequence); receipts[sequence] = receivedTick;
        }
        public bool TryGetConsumedInputReceipt(ulong sequence, out long receivedTick) => receipts.TryGetValue(sequence, out receivedTick);
        public bool CanAuthorizeAction(ulong sequence, long serverTick, int maxAgeTicks) =>
            TryGetConsumedInputReceipt(sequence, out long receivedTick) && serverTick >= receivedTick && serverTick - receivedTick <= maxAgeTicks;
        public long TakenReceivedTick => takenReceivedTick;
    }
}
