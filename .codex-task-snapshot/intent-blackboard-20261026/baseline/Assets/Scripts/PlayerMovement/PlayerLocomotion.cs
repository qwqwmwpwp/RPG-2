using UnityEngine;

namespace RPG4.PlayerMovement
{
    internal readonly struct UpperBodyDemand
    {
        internal readonly bool Strafe;
        internal readonly bool AimCamera;
        internal readonly bool ShotPose;
        internal readonly bool Reloading;
        internal readonly float MoveSpeed;
        internal readonly float AnimationSpeed;
        internal UpperBodyDemand(bool strafe, bool aimCamera, bool shotPose, bool reloading,
            float moveSpeed, float animationSpeed)
        {
            Strafe = strafe;
            AimCamera = aimCamera;
            ShotPose = shotPose;
            Reloading = reloading;
            MoveSpeed = moveSpeed;
            AnimationSpeed = animationSpeed;
        }
    }

    internal readonly struct LocomotionPlan
    {
        internal readonly bool CanMove;
        internal readonly bool Crouched;
        internal readonly bool Strafe;
        internal readonly Vector2 Move;
        internal readonly float ViewYaw;
        internal readonly float MoveSpeed;
        internal readonly float AnimationSpeed;
        internal LocomotionPlan(bool canMove, bool crouched, bool strafe, Vector2 move,
            float viewYaw, float moveSpeed, float animationSpeed)
        {
            CanMove = canMove;
            Crouched = crouched;
            Strafe = strafe;
            Move = move;
            ViewYaw = viewYaw;
            MoveSpeed = moveSpeed;
            AnimationSpeed = animationSpeed;
        }
    }

    /// <summary>唯一的姿态/运动约束合并处；仅消费下身提供的物理事实，不写胶囊。</summary>
    internal sealed class LocomotionResolver
    {
        private readonly PlayerContext context;
        internal LocomotionResolver(PlayerContext context) { this.context = context; }

        internal void Resolve(bool physicallyCrouched, bool canStand)
        {
            bool canMove = context.CanControl && Time.deltaTime > 0f;
            bool crouched = !canMove ? physicallyCrouched : context.Input.CrouchHeld
                || (physicallyCrouched && !canStand);
            UpperBodyDemand demand = context.WeaponDemand;
            float speed = demand.MoveSpeed;
            if (crouched) speed = Mathf.Min(speed, context.Data.Crouch.MoveSpeed);
            Vector2 move = canMove ? Vector2.ClampMagnitude(context.Input.Move, 1f) : Vector2.zero;
            float threshold = demand.Strafe ? context.Data.Aim.MoveInputThreshold
                : context.Data.Exploration.MoveInputThreshold;
            if (move.sqrMagnitude <= threshold * threshold) move = Vector2.zero;
            float yaw = context.Content.ViewController != null ? context.Content.ViewController.ViewYaw : 0f;
            context.Plan = new LocomotionPlan(canMove, crouched, demand.Strafe, move, yaw,
                Mathf.Max(0f, speed), demand.AnimationSpeed);
        }

    }
}
