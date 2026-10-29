using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>唯一的姿态/运动约束合并处；仅消费下身提供的物理事实，不写胶囊。</summary>
    internal sealed class LocomotionIntentProcessor
    {
        private readonly PlayerContext context;
        internal LocomotionIntentProcessor(PlayerContext context) { this.context = context; }

        internal void Resolve(bool physicallyCrouched, bool canStand)
        {
            PlayerBlackboard blackboard = context.Blackboard;
            blackboard.PhysicallyCrouched = physicallyCrouched;
            blackboard.CanStand = canStand;
            bool canMove = blackboard.CanMove && Time.deltaTime > 0f;
            bool crouched = !canMove ? physicallyCrouched : blackboard.CrouchHeld
                || (physicallyCrouched && !canStand);
            UpperBodyDemand demand = context.WeaponDemand;
            float speed = demand.MoveSpeed;
            if (crouched) speed = Mathf.Min(speed, context.Data.Crouch.MoveSpeed);
            Vector2 move = canMove ? Vector2.ClampMagnitude(blackboard.Move, 1f) : Vector2.zero;
            float threshold = demand.Strafe ? context.Data.Aim.MoveInputThreshold
                : context.Data.Exploration.MoveInputThreshold;
            if (move.sqrMagnitude <= threshold * threshold) move = Vector2.zero;
            float yaw = blackboard.ViewYaw;
            blackboard.Plan = new LocomotionPlan(canMove, crouched, demand.Strafe, move, yaw,
                Mathf.Max(0f, speed), demand.AnimationSpeed);
        }

        internal void RefreshAvailability()
        {
            if (context.Blackboard.CanMove) return;
            LocomotionPlan plan = context.Plan;
            context.Blackboard.Plan = new LocomotionPlan(false, context.Blackboard.PhysicallyCrouched,
                false, Vector2.zero, context.Blackboard.ViewYaw, plan.MoveSpeed, plan.AnimationSpeed);
        }
        internal void RecordPosture(bool physicallyCrouched, bool canStand)
        {
            context.Blackboard.PhysicallyCrouched = physicallyCrouched;
            context.Blackboard.CanStand = canStand;
            if (!physicallyCrouched || context.Plan.Crouched) return;
            LocomotionPlan plan = context.Plan;
            context.Blackboard.Plan = new LocomotionPlan(plan.CanMove, true, plan.Strafe, plan.Move,
                plan.ViewYaw, Mathf.Min(plan.MoveSpeed, context.Data.Crouch.MoveSpeed), plan.AnimationSpeed);
        }
    }
}
