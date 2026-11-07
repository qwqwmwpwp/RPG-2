using System.Collections.Generic;
using Mirror;
using NUnit.Framework;
using UnityEngine;

namespace RPG4.Networking.Tests
{
    public sealed class PlayerMovementProtocolTests
    {
        [Test]
        public void FourReportsRoundTripThroughActualMirrorCommandAndBatch()
        {
            var report = MovementTestData.Report(37, 4, 9);
            report.ConstraintRevision = 12;
            report.Input.FireRequestId = 71; report.Input.Look = new Vector2(13, -7);
            report.Input.Crouch = report.Input.Aim = report.Input.FireHeld = true;
            report.Input.FirePressed = report.Input.Reload = report.Input.Interact = true;
            report.PredictedMotor = new RPG4.PlayerMovement.PlayerMotorState {
                Position = new Vector3(2, 3, 4), Velocity = new Vector3(1, 0, 2), Yaw = 33,
                VerticalVelocity = -2, TurnVelocity = 11, Crouched = true };
            report.Aiming = report.Strafe = true;
            var payload = new NetworkWriter();
            payload.WritePlayerMovementReport(report);
            payload.WritePlayerMovementReport(report);
            payload.WritePlayerMovementReport(report);
            payload.WritePlayerMovementReport(report);
            var envelope = new NetworkWriter();
            NetworkMessages.Pack(new CommandMessage { netId = 123, componentIndex = 2,
                functionHash = 456, payload = payload.ToArraySegment() }, envelope);
            var batcher = new Batcher(1194);
            batcher.AddMessage(envelope.ToArraySegment(), 2d);
            var batch = new NetworkWriter();
            Assert.That(batcher.GetBatch(batch), Is.True);
            Assert.That(batch.Position, Is.LessThanOrEqualTo(1194), "Includes Mirror command and batch overhead");

            var reader = new NetworkReader(envelope.ToArraySegment());
            Assert.That(NetworkMessages.UnpackId(reader, out ushort id), Is.True);
            Assert.That(id, Is.EqualTo(NetworkMessageId<CommandMessage>.Id));
            var command = reader.Read<CommandMessage>();
            Assert.That(command.netId, Is.EqualTo(123));
            var reports = new NetworkReader(command.payload);
            for (int i = 0; i < 4; i++)
                Assert.That(reports.ReadPlayerMovementReport(), Is.EqualTo(report));
            Assert.That(reports.Remaining, Is.Zero);
        }

        private static IEnumerable<TestCaseData> NonFiniteCases()
        {
            for (int field = 0; field < 13; field++)
                foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                    yield return new TestCaseData(field, invalid);
        }

        [TestCaseSource(nameof(NonFiniteCases))]
        public void EveryInputAndMotorFloatMustBeFinite(int field, float invalid)
        {
            var report = MovementTestData.Report();
            switch (field)
            {
                case 0: report.Input.Move.x = invalid; break;
                case 1: report.Input.Move.y = invalid; break;
                case 2: report.Input.Look.x = invalid; break;
                case 3: report.Input.Look.y = invalid; break;
                case 4: report.PredictedMotor.Position.x = invalid; break;
                case 5: report.PredictedMotor.Position.y = invalid; break;
                case 6: report.PredictedMotor.Position.z = invalid; break;
                case 7: report.PredictedMotor.Velocity.x = invalid; break;
                case 8: report.PredictedMotor.Velocity.y = invalid; break;
                case 9: report.PredictedMotor.Velocity.z = invalid; break;
                case 10: report.PredictedMotor.Yaw = invalid; break;
                case 11: report.PredictedMotor.VerticalVelocity = invalid; break;
                case 12: report.PredictedMotor.TurnVelocity = invalid; break;
            }
            Assert.That(report.IsValid, Is.False);
        }

        [Test]
        public void ZeroEpochStepOrInputSequenceCannotAuthorizeMovement()
        {
            var report = MovementTestData.Report();
            Assert.That(report.IsValid, Is.True);
            report.Epoch = 0; Assert.That(report.IsValid, Is.False);
            report = MovementTestData.Report(); report.Step = 0; Assert.That(report.IsValid, Is.False);
            report = MovementTestData.Report(); report.Input.Sequence = 0; Assert.That(report.IsValid, Is.False);
        }

        [Test]
        public void AckAndCorrectionRetainIndependentProgressAndServerClock()
        {
            var ack = MovementTestData.Ack(3); ack.ProcessedInputSequence = 51; ack.ServerTick = 902;
            var correction = new PlayerMovementCorrection { Baseline = MovementTestData.Baseline(2, 4, 5),
                Reason = MovementRejectReason.Collision };
            correction.Baseline.State.Time = 18.04d; correction.Baseline.State.Tick = 902;
            var writer = new NetworkWriter();
            writer.WritePlayerMovementAck(ack); writer.WritePlayerMovementCorrection(correction);
            var reader = new NetworkReader(writer.ToArraySegment());
            Assert.That(reader.ReadPlayerMovementAck(), Is.EqualTo(ack));
            Assert.That(reader.ReadPlayerMovementCorrection(), Is.EqualTo(correction));
            Assert.That(reader.Remaining, Is.Zero);
        }

        [Test]
        public void AuthorityStateRoundTripRetainsBusinessFieldsWithoutMotorPayload()
        {
            var authority = new PlayerAuthorityState { Revision = 7, ConstraintRevision = 3,
                Snapshot = new RPG4.PlayerMovement.PlayerAuthoritySnapshot {
                    Tick = 801, Time = 16.02d, HurtUntil = 17.2d, ShotTime = 15.9d,
                    Health = 51f, MaxHealth = 100f, ReloadPhase = .3f, ShotDuration = .2f,
                    FadeDuration = .4f, ReloadDuration = 1.5f, Alive = true, WeaponAvailable = true,
                    Reloading = true, WeaponId = 77, ShotSequence = 89,
                    Action = new RPG4.PlayerIntent.WeaponActionSnapshot(true, true, true, true, true, .6f) } };
            var writer = new NetworkWriter(); writer.WritePlayerAuthorityState(authority);
            var reader = new NetworkReader(writer.ToArraySegment());
            Assert.That(reader.ReadPlayerAuthorityState(), Is.EqualTo(authority));
            Assert.That(reader.Remaining, Is.Zero);
        }
    }
}
