using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    public enum PlayerLocomotionMode : byte
    {
        Uninitialized, StandingFree, StandingStrafe, CrouchedFree, CrouchedStrafe
    }

    /// <summary>Numerical candidate only. Ground projection and collisions belong to the world.</summary>
    public static class PlayerMotorKinematics
    {
        public static PlayerLocomotionMode ModeFor(in LocomotionPlan plan)
        {
            if (plan.Crouched)
                return plan.Strafe ? PlayerLocomotionMode.CrouchedStrafe : PlayerLocomotionMode.CrouchedFree;
            return plan.Strafe ? PlayerLocomotionMode.StandingStrafe : PlayerLocomotionMode.StandingFree;
        }

        public static bool NeedsTurnReset(PlayerLocomotionMode lastMode, in LocomotionPlan plan)
            => lastMode != ModeFor(plan);

        public static PlayerMotorState Step(in PlayerMotorState from, in LocomotionPlan plan,
            in MovementKinematicsSettings settings, bool resetTurn, float dt)
        {
            if (dt <= 0f) return from;
            PlayerMotorState result = from;
            if (resetTurn) result.TurnVelocity = 0f;
            Vector3 direction = Quaternion.Euler(0f, plan.ViewYaw, 0f) * new Vector3(plan.Move.x, 0f, plan.Move.y);
            Vector2 target = Vector2.ClampMagnitude(new Vector2(direction.x, direction.z), 1f)
                * Mathf.Max(0f, plan.MoveSpeed);
            Vector2 move = plan.CanMove ? PlayerMotionMath.StepVelocity(
                new Vector2(from.Velocity.x, from.Velocity.z), target,
                settings, dt) : Vector2.zero;
            result.Velocity = new Vector3(move.x, 0f, move.y);
            if (plan.Strafe || move.sqrMagnitude > 0.000001f)
            {
                float targetYaw = plan.Strafe ? plan.ViewYaw : Mathf.Atan2(move.x, move.y) * Mathf.Rad2Deg;
                float smooth = plan.Strafe ? settings.StrafeTurnSmoothTime : settings.FreeTurnSmoothTime;
                result.Yaw = smooth <= 0f ? targetYaw : Mathf.SmoothDampAngle(from.Yaw, targetYaw,
                    ref result.TurnVelocity, smooth, Mathf.Infinity, dt);
            }
            else result.TurnVelocity = 0f;
            result.VerticalVelocity += settings.Gravity * dt;
            result.Position += new Vector3(move.x, result.VerticalVelocity, move.y) * dt;
            result.Crouched = plan.Crouched;
            return result;
        }
    }
}
