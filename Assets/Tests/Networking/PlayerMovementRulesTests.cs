using NUnit.Framework;
using RPG4.PlayerIntent;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking.Tests
{
    public sealed class PlayerMovementRulesTests
    {
        [Test]
        public void AimAndCrouchUseSharedPermissionsAndSpeedCaps()
        {
            var context = MovementTestData.Rules();
            var aim = PlayerMovementRules.Evaluate(context, new PlayerInputFrame(Vector2.up, aimHeld: true));
            Assert.That(aim.Aiming, Is.True);
            Assert.That(aim.Plan.Strafe, Is.True);
            Assert.That(aim.Plan.MoveSpeed, Is.EqualTo(3f));
            var crouch = PlayerMovementRules.Evaluate(context,
                new PlayerInputFrame(Vector2.up, crouchHeld: true, aimHeld: true));
            Assert.That(crouch.Plan.Crouched, Is.True);
            Assert.That(crouch.Plan.MoveSpeed, Is.EqualTo(2f));
        }

        [Test]
        public void BlockedStandStaysCrouchedAndDeadCannotMoveOrAim()
        {
            var context = MovementTestData.Rules();
            context.PhysicallyCrouched = true; context.CanStand = false;
            var blocked = PlayerMovementRules.Evaluate(context, new PlayerInputFrame(Vector2.up));
            Assert.That(blocked.Plan.Crouched, Is.True);
            context.Alive = false;
            var dead = PlayerMovementRules.Evaluate(context, new PlayerInputFrame(Vector2.up, aimHeld: true));
            Assert.That(dead.Plan.CanMove, Is.False);
            Assert.That(dead.Plan.Move, Is.EqualTo(Vector2.zero));
            Assert.That(dead.Aiming, Is.False);
        }

        [Test]
        public void HurtAndReloadCannotRestoreAimPermission()
        {
            var context = MovementTestData.Rules();
            context.IsHurt = true;
            Assert.That(PlayerMovementRules.Evaluate(context,
                new PlayerInputFrame(aimHeld: true)).Aiming, Is.False);
            context.IsHurt = false; context.Reloading = true;
            var reload = PlayerMovementRules.Evaluate(context, new PlayerInputFrame(aimHeld: true));
            Assert.That(reload.Aiming, Is.False);
            Assert.That(reload.Demand.Reloading, Is.True);
            Assert.That(reload.Plan.MoveSpeed, Is.EqualTo(2.5f));
        }

        [Test]
        public void KinematicsUsesServerStepAndDoesNotMutateStart()
        {
            var from = new PlayerMotorState { Position = new Vector3(2, 3, 4), Yaw = 90,
                TurnVelocity = 8f };
            var plan = new LocomotionPlan(true, true, true, Vector2.up, 90f, 2f, 1f);
            var result = PlayerMotorKinematics.Step(from, plan, MovementTestData.Settings(), true, .02f);
            Assert.That(result.Position.x, Is.EqualTo(2.04f).Within(.00001f));
            Assert.That(result.Position.z, Is.EqualTo(4f).Within(.00001f));
            Assert.That(result.VerticalVelocity, Is.EqualTo(-9.81f * .02f).Within(.00001f));
            Assert.That(result.Yaw, Is.EqualTo(90f).Within(.00001f));
            Assert.That(result.TurnVelocity, Is.Zero);
            Assert.That(result.Crouched, Is.True);
            Assert.That(from.Position, Is.EqualTo(new Vector3(2, 3, 4)));
            Assert.That(PlayerMotorKinematics.Step(from, plan, MovementTestData.Settings(), true, 0f), Is.EqualTo(from));
        }
    }
}
