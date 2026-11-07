using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Numerical candidate only. Ground projection and collisions belong to the world.</summary>
    public static class PlayerMotorKinematics
    {
        public static PlayerMotorState Step(in PlayerMotorState from, in LocomotionPlan plan,
            in MovementKinematicsSettings settings, bool resetTurn, float dt)
        {
            if (dt <= 0f) return from;
            PlayerMotorState result = from;
            if (resetTurn) result.TurnVelocity = 0f;
            Vector3 direction = Quaternion.Euler(0f, plan.ViewYaw, 0f) * new Vector3(plan.Move.x, 0f, plan.Move.y);
            Vector2 target = new Vector2(direction.x, direction.z) * plan.MoveSpeed;
            Vector2 move = plan.CanMove ? Vector2.ClampMagnitude(PlayerMotionMath.SmoothMove(
                new Vector2(from.Velocity.x, from.Velocity.z), target,
                settings.DirectionSmoothTime, settings.SpeedSmoothTime, dt), plan.MoveSpeed) : Vector2.zero;
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
