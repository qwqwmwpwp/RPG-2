using System.Collections.Generic;

namespace RPG4.Networking
{
    /// <summary>Only acknowledged or baseline-closed history may be retired.</summary>
    public sealed class OwnerMovementHistory
    {
        public const int Capacity = 128;
        private readonly List<PlayerMovementRecord> records = new List<PlayerMovementRecord>(Capacity);
        private bool acceptsOwnerMessages;
        private uint retiredEpoch;
        private ulong acknowledged;
        private PlayerMovementAck pendingAck;
        public bool HasBaseline { get; private set; }
        public uint Epoch { get; private set; }
        public uint CorrectionId { get; private set; }
        public uint ConstraintRevision { get; private set; }
        public ulong LastStep { get; private set; }
        public int Count => records.Count;
        public IReadOnlyList<PlayerMovementRecord> Records => records;
        public bool Store(in PlayerMovementRecord record)
        {
            if (!acceptsOwnerMessages || !HasBaseline || records.Count >= Capacity || !record.Report.IsValid
                || record.Report.Epoch != Epoch || record.Report.CorrectionId != CorrectionId
                || record.Report.ConstraintRevision != ConstraintRevision || record.Report.Step != LastStep + 1) return false;
            records.Add(record); LastStep = record.Report.Step; return true;
        }
        public bool Acknowledge(in PlayerMovementAck ack)
        {
            if (!acceptsOwnerMessages || !HasBaseline || ack.Epoch != Epoch || ack.AcceptedStep <= acknowledged || ack.AcceptedStep > LastStep) return false;
            if (ack.CorrectionId < CorrectionId || ack.ConstraintRevision < ConstraintRevision) return false;
            if (ack.CorrectionId != CorrectionId || ack.ConstraintRevision != ConstraintRevision)
            {
                if (pendingAck.Epoch == 0 || ack.ServerTick > pendingAck.ServerTick) pendingAck = ack;
                return false;
            }
            acknowledged = ack.AcceptedStep;
            records.RemoveAll(r => r.Report.Step <= acknowledged);
            return true;
        }
        public bool TryRebase(in PlayerMovementBaseline baseline, out PlayerInputPacket[] replay)
        {
            replay = System.Array.Empty<PlayerInputPacket>();
            if (!acceptsOwnerMessages || !baseline.IsValid || baseline.Epoch <= retiredEpoch || baseline.Epoch < Epoch
                || baseline.ConstraintRevision < ConstraintRevision) return false;
            PlayerMovementBaseline basis = baseline; // in parameters cannot be captured by the retirement predicates.
            bool sameEpoch = HasBaseline && baseline.Epoch == Epoch;
            if (sameEpoch && (baseline.CorrectionId <= CorrectionId || baseline.Step < acknowledged)) return false;
            if (!sameEpoch)
            {
                records.RemoveAll(r => r.Report.Input.Sequence <= basis.ProcessedInputSequence);
                for (int i = 0; i < records.Count; i++)
                {
                    PlayerMovementRecord record = records[i];
                    record.Report.Epoch = baseline.Epoch; record.Report.Step = baseline.Step + (ulong)i + 1;
                    record.Report.CorrectionId = baseline.CorrectionId; record.Report.ConstraintRevision = baseline.ConstraintRevision;
                    records[i] = record;
                }
            }
            else records.RemoveAll(r => r.Report.Step <= basis.Step || r.Report.Input.Sequence <= basis.ProcessedInputSequence);
            Epoch = baseline.Epoch; CorrectionId = baseline.CorrectionId; ConstraintRevision = baseline.ConstraintRevision;
            HasBaseline = true; acknowledged = baseline.Step;
            LastStep = records.Count > 0 ? records[records.Count - 1].Report.Step : baseline.Step;
            replay = new PlayerInputPacket[records.Count];
            for (int i = 0; i < records.Count; i++) replay[i] = records[i].Report.Input;
            // Any pending ACK is reconsidered only after the caller has installed and replayed this baseline.
            return true;
        }
        /// <summary>Hard recovery only: obsolete unsent inputs must not restart an endless missing-step cycle.</summary>
        public void RetireExpiredInputs(double minimumPredictionTime)
        { records.RemoveAll(r => r.PredictedState.Time < minimumPredictionTime); }
        public void ReplaceReplay(int index, in PlayerMovementRecord record)
        {
            if (index < 0 || index >= records.Count || record.Report.Epoch != Epoch || record.Report.CorrectionId != CorrectionId
                || record.Report.Step != records[index].Report.Step || !MovementValues.SameInput(record.Report.Input, records[index].Report.Input)) return;
            records[index] = record;
        }
        public void ApplyConstraintRevision(uint revision)
        { if (acceptsOwnerMessages && revision >= ConstraintRevision) { ConstraintRevision = revision; RetryPendingAck(); } }
        public bool RetryPendingAck()
        {
            PlayerMovementAck ack = pendingAck;
            if (ack.Epoch == 0 || ack.CorrectionId != CorrectionId || ack.ConstraintRevision != ConstraintRevision) return false;
            pendingAck = default; return Acknowledge(ack);
        }
        public void Reset(bool acceptsOwnerMessages)
        {
            if (HasBaseline) retiredEpoch = System.Math.Max(retiredEpoch, Epoch);
            this.acceptsOwnerMessages = acceptsOwnerMessages;
            HasBaseline = false; records.Clear(); pendingAck = default; acknowledged = LastStep = 0;
            Epoch = CorrectionId = ConstraintRevision = 0;
        }
    }
}
