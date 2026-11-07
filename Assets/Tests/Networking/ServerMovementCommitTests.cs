using NUnit.Framework;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking.Tests
{
    public sealed class ServerMovementCommitTests
    {
        [Test]
        public void ActualCommitDecisionUsesOnlyValidatedMotor()
        {
            ValidationTestData.Create(out var context, out var report);
            var accepted = PlayerMovementValidator.Validate(context, report, new FlatMovementWorld()).ToCommit();
            Assert.That(accepted.UseAuthorityMotor, Is.False);
            Assert.That(accepted.Motor.Position, Is.EqualTo(report.PredictedMotor.Position));
            report.PredictedMotor.Position = new Vector3(100, 0, 100);
            var rejected = PlayerMovementValidator.Validate(context, report, new FlatMovementWorld()).ToCommit();
            Assert.That(rejected.UseAuthorityMotor, Is.True);
            Assert.That(rejected.Motor.Position, Is.Not.EqualTo(report.PredictedMotor.Position));
            ValidationTestData.Create(out context, out report);
            var blocked = PlayerMovementValidator.Validate(context, report,
                new FlatMovementWorld(MovementPathKind.Blocked)).ToCommit();
            Assert.That(blocked.UseAuthorityMotor, Is.True);
        }

        [Test]
        public void SkippedInputNeverAuthorizesActionAndAcceptanceIsNotAnActionReceipt()
        {
            var timeline = new ServerMovementTimeline(); timeline.Reset(MovementTestData.Baseline(), 100);
            timeline.Enqueue(MovementTestData.Report(1), 100);
            timeline.Take(101, out var first); timeline.CommitResult(first, true);
            Assert.That(timeline.AcceptedStep, Is.EqualTo(1));
            Assert.That(timeline.CanAuthorizeAction(1, 101, 13), Is.False);
            timeline.RecordConsumedInput(1, 100);
            Assert.That(timeline.CanAuthorizeAction(1, 101, 13), Is.True);
            timeline.Enqueue(MovementTestData.Report(3), 102);
            Assert.That(timeline.Take(104, out _), Is.EqualTo(MovementTakeResult.RebaseRequired));
            Assert.That(timeline.CanAuthorizeAction(2, 104, 13), Is.False);
            Assert.That(timeline.CanAuthorizeAction(3, 104, 13), Is.False);
            Assert.That(timeline.CanAuthorizeAction(1, 114, 13), Is.False);
        }
    }
}
