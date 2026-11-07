using NUnit.Framework;
using UnityEngine;

namespace RPG4.Networking.Tests
{
    public sealed class ServerMovementTimelineTests
    {
        private static ServerMovementTimeline Timeline()
        {
            var timeline = new ServerMovementTimeline();
            timeline.Reset(MovementTestData.Baseline(), 100);
            return timeline;
        }

        [Test]
        public void OneServerTickCannotConsumeTwoSteps()
        {
            var timeline = Timeline();
            Assert.That(timeline.Enqueue(MovementTestData.Report(2), 100), Is.True);
            Assert.That(timeline.Enqueue(MovementTestData.Report(1), 100), Is.True);
            Assert.That(timeline.Take(100, out _), Is.EqualTo(MovementTakeResult.None));
            Assert.That(timeline.Take(101, out var first), Is.EqualTo(MovementTakeResult.Ready));
            Assert.That(first.Step, Is.EqualTo(1));
            Assert.That(timeline.AcceptedStep, Is.Zero);
            timeline.CommitResult(first, true);
            Assert.That(timeline.Take(101, out _), Is.EqualTo(MovementTakeResult.None));
            Assert.That(timeline.Take(102, out var second), Is.EqualTo(MovementTakeResult.Ready));
            Assert.That(second.Step, Is.EqualTo(2));
        }

        [Test]
        public void MissingInputRequiresRebaseWithoutAcknowledgingOrCreatingReceipt()
        {
            var timeline = Timeline();
            timeline.Enqueue(MovementTestData.Report(2), 101);
            Assert.That(timeline.Take(101, out _), Is.EqualTo(MovementTakeResult.None));
            Assert.That(timeline.Take(102, out _), Is.EqualTo(MovementTakeResult.None));
            Assert.That(timeline.Take(103, out _), Is.EqualTo(MovementTakeResult.RebaseRequired));
            Assert.That(timeline.RebaseReason, Is.EqualTo(MovementRejectReason.MissingInput));
            Assert.That(timeline.AcceptedStep, Is.Zero);
            Assert.That(timeline.TryGetConsumedInputReceipt(1, out _), Is.False);
            Assert.That(timeline.TryGetConsumedInputReceipt(2, out _), Is.False);
        }

        [Test]
        public void DuplicateIsIgnoredButConflictingPredictionRequiresCorrection()
        {
            var timeline = Timeline();
            var report = MovementTestData.Report();
            Assert.That(timeline.Enqueue(report, 100), Is.True);
            Assert.That(timeline.Enqueue(report, 100), Is.False);
            Assert.That(timeline.RebaseReason, Is.EqualTo(MovementRejectReason.None));
            report.PredictedMotor.Position = Vector3.right;
            Assert.That(timeline.Enqueue(report, 100), Is.False);
            Assert.That(timeline.Take(101, out _), Is.EqualTo(MovementTakeResult.RebaseRequired));
        }

        [Test]
        public void ExpiredAndOldEpochReportsCannotBecomeAcceptedInput()
        {
            var timeline = Timeline();
            Assert.That(timeline.Enqueue(MovementTestData.Report(1, 2), 100), Is.False);
            timeline.Enqueue(MovementTestData.Report(), 100);
            Assert.That(timeline.Take(114, out _), Is.EqualTo(MovementTakeResult.RebaseRequired));
            Assert.That(timeline.ProcessedInputSequence, Is.Zero);
            Assert.That(timeline.AcceptedStep, Is.Zero);
        }

        [Test]
        public void ProcessedRejectedStepDoesNotExtendAcceptanceUntilCorrectionClosesIt()
        {
            var timeline = Timeline();
            timeline.Enqueue(MovementTestData.Report(), 100);
            timeline.Take(101, out var report);
            timeline.CommitResult(report, false);
            Assert.That(timeline.ProcessedStep, Is.EqualTo(1));
            Assert.That(timeline.ProcessedInputSequence, Is.EqualTo(1));
            Assert.That(timeline.AcceptedStep, Is.Zero);
            timeline.CommitCorrection(MovementTestData.Baseline(1, 1, 1));
            var next = MovementTestData.Report(2, 1, 1);
            timeline.Enqueue(next, 101); timeline.Take(102, out next); timeline.CommitResult(next, true);
            Assert.That(timeline.AcceptedStep, Is.EqualTo(2));
        }

        [Test]
        public void ReplayedPendingReportCanReplaceOnlyItsPrediction()
        {
            var timeline = Timeline();
            timeline.Enqueue(MovementTestData.Report(1), 100);
            timeline.Enqueue(MovementTestData.Report(2), 100);
            timeline.Take(101, out var first); timeline.CommitResult(first, false);
            var replay = MovementTestData.Report(2, 1, 1); replay.PredictedMotor.Position = Vector3.right;
            Assert.That(timeline.Enqueue(replay, 101), Is.False, "Unknown future correction");
            timeline.CommitCorrection(MovementTestData.Baseline(1, 1, 1));
            Assert.That(timeline.Enqueue(replay, 101), Is.True);
            Assert.That(timeline.Enqueue(MovementTestData.Report(2), 101), Is.False, "Old prediction cannot replace replay");
            Assert.That(timeline.Enqueue(MovementTestData.Report(1, 1, 1), 101), Is.False, "Processed step cannot run again");
            Assert.That(timeline.Take(102, out var replaced), Is.EqualTo(MovementTakeResult.Ready));
            Assert.That(replaced.PredictedMotor.Position, Is.EqualTo(Vector3.right));

            var conflict = Timeline(); conflict.Enqueue(MovementTestData.Report(2), 100);
            var changedInput = MovementTestData.Report(2); changedInput.Input.Look = Vector2.right;
            Assert.That(conflict.Enqueue(changedInput, 101), Is.False);
            Assert.That(conflict.RebaseReason, Is.EqualTo(MovementRejectReason.InvalidInput));
        }

        [Test]
        public void TakenReceiptRetainsOriginalArrivalTime()
        {
            var timeline = Timeline();
            timeline.Enqueue(MovementTestData.Report(), 100);
            timeline.Take(110, out var report);
            Assert.That(timeline.TakenReceivedTick, Is.EqualTo(100));
            Assert.That(timeline.TryGetConsumedInputReceipt(report.Input.Sequence, out _), Is.False);
            timeline.RecordConsumedInput(report.Input.Sequence, timeline.TakenReceivedTick);
            Assert.That(timeline.CanAuthorizeAction(report.Input.Sequence, 114, 13), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RetiringPendingInputsAdvancesOnlyInputRetirementWatermark(bool includeQueued)
        {
            var timeline = Timeline();
            var first = MovementTestData.Report(1); first.Input.Sequence = 11;
            Assert.That(timeline.Enqueue(first, 100), Is.True);
            if (includeQueued)
            {
                var queued = MovementTestData.Report(2); queued.Input.Sequence = 12;
                Assert.That(timeline.Enqueue(queued, 100), Is.True);
            }
            Assert.That(timeline.Take(101, out var taken), Is.EqualTo(MovementTakeResult.Ready));
            timeline.RetirePendingInputs();
            Assert.That(timeline.ProcessedInputSequence, Is.EqualTo(includeQueued ? 12UL : 11UL));
            Assert.That(timeline.ProcessedStep, Is.Zero);
            Assert.That(timeline.AcceptedStep, Is.Zero);
            Assert.That(timeline.TryGetConsumedInputReceipt(11, out _), Is.False);
            Assert.That(timeline.TryGetConsumedInputReceipt(12, out _), Is.False);
            Assert.That(timeline.Take(102, out _), Is.EqualTo(MovementTakeResult.None));
            timeline.CommitResult(taken, true);
            Assert.That(timeline.AcceptedStep, Is.Zero, "Retired taken report cannot later be committed");
        }

        [Test]
        public void FourReportRedundancyRecoversOneLostTwoStepPacketWithoutExtraSimulation()
        {
            var timeline = Timeline();
            timeline.Enqueue(MovementTestData.Report(2), 102);
            timeline.Enqueue(MovementTestData.Report(1), 102);
            Assert.That(timeline.Take(102, out var first), Is.EqualTo(MovementTakeResult.Ready));
            timeline.CommitResult(first, true);
            Assert.That(timeline.Take(103, out var second), Is.EqualTo(MovementTakeResult.Ready));
            timeline.CommitResult(second, true);
            // The 104 packet carrying steps 4/3/2/1 is lost. The next 25Hz packet repeats both lost steps.
            Assert.That(timeline.Take(104, out _), Is.EqualTo(MovementTakeResult.None));
            Assert.That(timeline.Take(105, out _), Is.EqualTo(MovementTakeResult.None));
            for (ulong step = 6; step >= 3; step--)
                Assert.That(timeline.Enqueue(MovementTestData.Report(step), 106), Is.True);
            for (long tick = 106; tick <= 109; tick++)
            {
                Assert.That(timeline.Take(tick, out var report), Is.EqualTo(MovementTakeResult.Ready));
                Assert.That(report.Step, Is.EqualTo((ulong)(tick - 103)));
                timeline.CommitResult(report, true);
                Assert.That(timeline.Take(tick, out _), Is.EqualTo(MovementTakeResult.None));
            }
            Assert.That(timeline.AcceptedStep, Is.EqualTo(6));
            Assert.That(timeline.RebaseReason, Is.EqualTo(MovementRejectReason.None));
        }
    }
}
