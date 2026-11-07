using NUnit.Framework;

namespace RPG4.Networking.Tests
{
    public sealed class MovementLifecycleTests
    {
        [Test]
        public void InitialPredictionRequiresExplicitBaseline()
        {
            var history = new OwnerMovementHistory(); history.Reset(true);
            Assert.That(history.HasBaseline, Is.False);
            Assert.That(history.Store(MovementTestData.Record(1)), Is.False);
            Assert.That(history.Acknowledge(MovementTestData.Ack(1)), Is.False);
            Assert.That(history.TryRebase(MovementTestData.Baseline(), out _), Is.True);
            Assert.That(history.Store(MovementTestData.Record(1)), Is.True);
        }

        [Test]
        public void HostDoesNotApplyItsOwnCorrection()
        {
            var history = new OwnerMovementHistory(); history.Reset(false);
            Assert.That(history.TryRebase(MovementTestData.Baseline(), out _), Is.False);
            Assert.That(history.Acknowledge(MovementTestData.Ack(1)), Is.False);
            Assert.That(history.Store(MovementTestData.Record(1)), Is.False);
            Assert.That(history.HasBaseline, Is.False);
        }

        [Test]
        public void DisabledOrDisconnectedHistoryCannotReopenFromIncomingMessages()
        {
            var history = new OwnerMovementHistory(); history.Reset(true);
            history.TryRebase(MovementTestData.Baseline(), out _); history.Store(MovementTestData.Record(1));
            history.Reset(false);
            Assert.That(history.HasBaseline, Is.False);
            Assert.That(history.Count, Is.Zero);
            Assert.That(history.TryRebase(MovementTestData.Baseline(2), out _), Is.False);
            Assert.That(history.Acknowledge(MovementTestData.Ack(1)), Is.False);
        }

        [Test]
        public void OldEpochMessagesCannotReadyNewSession()
        {
            var history = new OwnerMovementHistory(); history.Reset(true);
            history.TryRebase(MovementTestData.Baseline(4), out _);
            history.Reset(false); history.Reset(true);
            Assert.That(history.TryRebase(MovementTestData.Baseline(4, 9), out _), Is.False);
            Assert.That(history.HasBaseline, Is.False);
            Assert.That(history.TryRebase(MovementTestData.Baseline(5), out _), Is.True);
            Assert.That(history.TryRebase(MovementTestData.Baseline(4, 99), out _), Is.False);
            Assert.That(history.Acknowledge(MovementTestData.Ack(1, 4)), Is.False);
            Assert.That(history.Epoch, Is.EqualTo(5));
            Assert.That(history.TryRebase(MovementTestData.Baseline(5), out _), Is.False);
        }
    }
}
