using System;
using NUnit.Framework;
using RPG4.PlayerIntent;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking.Tests
{
    internal sealed class FlatMovementWorld : IPlayerMovementWorld
    {
        private readonly MovementPathKind kind;
        private readonly bool canStand;
        internal FlatMovementWorld(MovementPathKind kind = MovementPathKind.ClearFlatGround, bool canStand = true)
        { this.kind = kind; this.canStand = canStand; }
        public MovementPathResult CheckPath(in PlayerMotorState from, in PlayerMotorState proposed)
            => new MovementPathResult(kind, 0f, canStand);
    }

    internal static class ValidationTestData
    {
        internal static void Create(out MovementValidationContext context, out PlayerMovementReport report,
            bool aim = false, bool crouch = false, PlayerMotorState start = default,
            PlayerMotorState reference = default, ulong step = 1)
        {
            var rules = PlayerMovementRules.Evaluate(MovementTestData.Rules(),
                new PlayerInputFrame(Vector2.up, crouchHeld: crouch, aimHeld: aim));
            var settings = MovementTestData.Settings();
            var evaluation = new PlayerMovementEvaluation(start, rules, settings, 0, 0, 100 + (long)step,
                2d + step * .02d, .02f, false);
            context = MovementValidationContext.Create(1, step, 1, evaluation, reference);
            var candidate = PlayerMotorKinematics.Step(reference, rules.Plan, settings, false, .02f);
            candidate.Position.y = 0; candidate.VerticalVelocity = 0;
            report = MovementTestData.Report(step); report.Input.Aim = aim; report.Input.Crouch = crouch;
            report.PredictedMotor = candidate; report.Aiming = rules.Aiming; report.Strafe = rules.Plan.Strafe;
        }
    }

    public sealed class PlayerMovementValidatorTests
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void SharedRulesAcceptNormalAimAndCrouchOnVerifiedFlatGround(bool aim, bool crouch)
        {
            ValidationTestData.Create(out var context, out var report, aim, crouch);
            var result = PlayerMovementValidator.Validate(context, report, new FlatMovementWorld());
            Assert.That(result.Accepted, Is.True);
            Assert.That(result.UseAuthorityMotor, Is.False);
            Assert.That(result.Motor, Is.EqualTo(report.PredictedMotor));
            Assert.That(result.ReferenceMotor.Position.y, Is.Zero);
            Assert.That(result.ReferenceMotor.VerticalVelocity, Is.Zero);
        }

        [TestCase("speed")]
        [TestCase("yaw")]
        [TestCase("turn")]
        [TestCase("vertical")]
        [TestCase("height")]
        public void ExtraMotionOutsideNumericalToleranceRequiresAuthority(string field)
        {
            ValidationTestData.Create(out var context, out var report);
            switch (field)
            {
                case "speed": report.PredictedMotor.Velocity.z += .02f; break;
                case "yaw": report.PredictedMotor.Yaw += 1f; break;
                case "turn": report.PredictedMotor.TurnVelocity += 1f; break;
                case "vertical": report.PredictedMotor.VerticalVelocity = 1f; break;
                case "height": report.PredictedMotor.Position.y = .01f; break;
            }
            var result = PlayerMovementValidator.Validate(context, report, new FlatMovementWorld());
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.UseAuthorityMotor, Is.True);
            Assert.That(result.Reason, Is.EqualTo(MovementRejectReason.Kinematics));
        }

        [TestCase(MovementPathKind.Blocked)]
        [TestCase(MovementPathKind.RequiresAuthority)]
        public void NonClearPathRequiresAuthority(MovementPathKind kind)
        {
            ValidationTestData.Create(out var context, out var report);
            var result = PlayerMovementValidator.Validate(context, report, new FlatMovementWorld(kind));
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.UseAuthorityMotor, Is.True);
            Assert.That(result.Reason, Is.EqualTo(MovementRejectReason.Collision));
        }

        [Test]
        public void BlockedStandAndAuthorityConstraintChangeCannotAdoptStalePrediction()
        {
            ValidationTestData.Create(out var context, out var report);
            Assert.That(PlayerMovementValidator.Validate(context, report,
                new FlatMovementWorld(canStand: false)).Accepted, Is.False);
            report.ConstraintRevision = 0;
            Assert.That(PlayerMovementValidator.Validate(context, report,
                new FlatMovementWorld()).Reason, Is.EqualTo(MovementRejectReason.AuthorityChanged));
            report.ConstraintRevision = 1; report.Strafe = true;
            Assert.That(PlayerMovementValidator.Validate(context, report,
                new FlatMovementWorld()).Reason, Is.EqualTo(MovementRejectReason.InvalidMode));
        }

        [Test]
        public void RepeatedToleranceDoesNotCreateTravelBudget()
        {
            ValidationTestData.Create(out var context, out var report);
            report.PredictedMotor.Position.x += .0015f;
            var first = PlayerMovementValidator.Validate(context, report, new FlatMovementWorld());
            Assert.That(first.Accepted, Is.True);
            Assert.That(first.ReferenceMotor.Position.x, Is.EqualTo(0).Within(.000001f));
            ValidationTestData.Create(out var nextContext, out var nextReport,
                start: first.Motor, reference: first.ReferenceMotor, step: 2);
            nextReport.PredictedMotor.Position.x += .003f;
            var accumulated = PlayerMovementValidator.Validate(nextContext, nextReport, new FlatMovementWorld());
            Assert.That(accumulated.Accepted, Is.False);
            Assert.That(accumulated.Reason, Is.EqualTo(MovementRejectReason.Kinematics));
        }

        private sealed class ForbiddenWorldQuery : IPlayerMovementWorld
        {
            public MovementPathResult CheckPath(in PlayerMotorState from, in PlayerMotorState proposed)
                => throw new InvalidOperationException("Unbounded report must be rejected before physics queries");
        }

        [Test]
        public void UnboundedCoordinatesAreRejectedBeforeWorldQuery()
        {
            ValidationTestData.Create(out var context, out var report);
            report.PredictedMotor.Position.x = 1000000f;
            Assert.That(PlayerMovementValidator.Validate(context, report, new ForbiddenWorldQuery()).Accepted, Is.False);
        }
    }
}
