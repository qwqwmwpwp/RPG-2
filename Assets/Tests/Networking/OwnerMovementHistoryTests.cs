using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace RPG4.Networking.Tests
{
    public sealed class OwnerMovementHistoryTests
    {
        private static OwnerMovementHistory History(ulong step = 0)
        {
            var history = new OwnerMovementHistory(); history.Reset(true);
            Assert.That(history.TryRebase(MovementTestData.Baseline(1, 0, step), out _), Is.True);
            return history;
        }

        [Test]
        public void CumulativeAckRetiresOnlyAcceptedRecordsWithoutChangingPendingPrediction()
        {
            var history = History();
            history.Store(MovementTestData.Record(1));
            var pending = MovementTestData.Record(2);
            pending.PredictedState.Motor.Position = new Vector3(7, 0, 9);
            history.Store(pending);
            Assert.That(history.Acknowledge(MovementTestData.Ack(1)), Is.True);
            Assert.That(history.Count, Is.EqualTo(1));
            Assert.That(history.Records[0], Is.EqualTo(pending));
            Assert.That(history.LastStep, Is.EqualTo(2));
            Assert.That(history.Acknowledge(MovementTestData.Ack(1)), Is.False);
            Assert.That(history.Acknowledge(MovementTestData.Ack(2, 2)), Is.False);
            Assert.That(history.Count, Is.EqualTo(1));
        }

        [Test]
        public void AckWaitsForReferencedConstraintAndCannotRestoreOlderPermission()
        {
            var history = History(); history.Store(MovementTestData.Record(1));
            Assert.That(history.Acknowledge(MovementTestData.Ack(1, constraint: 2)), Is.False);
            Assert.That(history.Count, Is.EqualTo(1));
            history.ApplyConstraintRevision(2);
            Assert.That(history.Count, Is.Zero);
            history.ApplyConstraintRevision(1);
            Assert.That(history.ConstraintRevision, Is.EqualTo(2));
            Assert.That(history.Acknowledge(MovementTestData.Ack(1, constraint: 1)), Is.False);
        }

        [Test]
        public void AckWaitsForInstalledCorrectionBeforeRetiringHistory()
        {
            var history = History(); history.Store(MovementTestData.Record(1));
            history.Store(MovementTestData.Record(2));
            Assert.That(history.Acknowledge(MovementTestData.Ack(2, correction: 1)), Is.False);
            Assert.That(history.Count, Is.EqualTo(2));
            Assert.That(history.TryRebase(MovementTestData.Baseline(1, 1, 1), out var replay), Is.True);
            Assert.That(replay.Select(p => p.Sequence), Is.EqualTo(new ulong[] { 2 }));
            Assert.That(history.Count, Is.EqualTo(1), "Installing a baseline alone must not apply pending ACK before replay");
            Assert.That(history.RetryPendingAck(), Is.True);
            Assert.That(history.Count, Is.Zero);
        }

        [Test]
        public void DelayedCorrectionDoesNotRestartMovementSteps()
        {
            var history = History(119);
            history.Store(MovementTestData.Record(120)); history.Store(MovementTestData.Record(121));
            history.Store(MovementTestData.Record(122));
            var baseline = MovementTestData.Baseline(1, 1, 120);
            Assert.That(history.TryRebase(baseline, out var replay), Is.True);
            Assert.That(replay.Select(p => p.Sequence), Is.EqualTo(new ulong[] { 121, 122 }));
            Assert.That(history.Records.Select(r => r.Report.Step), Is.EqualTo(new ulong[] { 121, 122 }));
            Assert.That(history.LastStep, Is.EqualTo(122));
            Assert.That(history.TryRebase(baseline, out _), Is.False, "Duplicate correction must not replay twice");
            Assert.That(history.TryRebase(MovementTestData.Baseline(1, 0, 119), out _), Is.False);
            Assert.That(history.Store(MovementTestData.Record(123, correction: 1)), Is.True);
        }

        [Test]
        public void NewEpochKeepsUnprocessedInputSequencesAndRestartsOnlyMovementSteps()
        {
            var history = History(9);
            history.Store(MovementTestData.Record(10)); history.Store(MovementTestData.Record(11));
            history.Store(MovementTestData.Record(12));
            var baseline = MovementTestData.Baseline(2); baseline.ProcessedInputSequence = 10;
            Assert.That(history.TryRebase(baseline, out var replay), Is.True);
            Assert.That(replay.Select(p => p.Sequence), Is.EqualTo(new ulong[] { 11, 12 }));
            Assert.That(history.Records.Select(r => r.Report.Step), Is.EqualTo(new ulong[] { 1, 2 }));
            Assert.That(history.Records.Select(r => r.Report.Epoch), Is.All.EqualTo(2));
            Assert.That(history.LastStep, Is.EqualTo(2));
            var next = MovementTestData.Record(3, 2); next.Report.Input.Sequence = 13;
            Assert.That(history.Store(next), Is.True);
            Assert.That(history.TryRebase(MovementTestData.Baseline(1, 9, 12), out _), Is.False);
        }

        [Test]
        public void HistoryOverflowRefusesPredictionWithoutDroppingUnclosedRecords()
        {
            var history = History();
            for (ulong step = 1; step <= OwnerMovementHistory.Capacity; step++)
                Assert.That(history.Store(MovementTestData.Record(step)), Is.True);
            Assert.That(history.Store(MovementTestData.Record(129)), Is.False);
            Assert.That(history.Count, Is.EqualTo(128));
            Assert.That(history.Records[0].Report.Step, Is.EqualTo(1));
            Assert.That(history.LastStep, Is.EqualTo(128));
        }

        [Test]
        public void HardRecoveryRetiresOnlyExpiredInputsBeforeRenumberingFreshSequences()
        {
            var history = History();
            var expired = MovementTestData.Record(1);
            expired.Report.Input.Sequence = 101; expired.PredictedState.Time = 10d;
            var boundary = MovementTestData.Record(2);
            boundary.Report.Input.Sequence = 102; boundary.PredictedState.Time = 20d;
            var fresh = MovementTestData.Record(3);
            fresh.Report.Input.Sequence = 103; fresh.PredictedState.Time = 30d;
            Assert.That(history.Store(expired), Is.True);
            Assert.That(history.Store(boundary), Is.True);
            Assert.That(history.Store(fresh), Is.True);
            history.RetireExpiredInputs(20d);
            Assert.That(history.Records.Select(r => r.Report.Input.Sequence), Is.EqualTo(new ulong[] { 102, 103 }));
            Assert.That(history.Records.Select(r => r.Report.Step), Is.EqualTo(new ulong[] { 2, 3 }));
            var baseline = MovementTestData.Baseline(2); baseline.ProcessedInputSequence = 100;
            Assert.That(history.TryRebase(baseline, out var replay), Is.True);
            Assert.That(replay.Select(p => p.Sequence), Is.EqualTo(new ulong[] { 102, 103 }));
            Assert.That(history.Records.Select(r => r.Report.Step), Is.EqualTo(new ulong[] { 1, 2 }));
            Assert.That(history.Records.Select(r => r.Report.Epoch), Is.All.EqualTo(2));
        }
    }
}
