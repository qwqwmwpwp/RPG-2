using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>唯一的姿态/运动约束合并处；仅消费下身提供的物理事实，不写胶囊。</summary>
    internal sealed class LocomotionIntentProcessor
    {
        private readonly PlayerContext context;
        private readonly PlayerBlackboard blackboard;
        internal LocomotionIntentProcessor(PlayerContext context, PlayerBlackboard blackboard)
        {
            this.context = context;
            this.blackboard = blackboard;
        }

        internal void Resolve(bool physicallyCrouched, bool canStand)
        {
            blackboard.PhysicallyCrouched = physicallyCrouched;
            blackboard.CanStand = canStand;
            bool canMove = blackboard.CanMove && blackboard.InputAllowed;
            bool crouched = !canMove ? physicallyCrouched : blackboard.CrouchHeld
                || (physicallyCrouched && !canStand);
            UpperBodyDemand demand = blackboard.WeaponDemand;
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
            if (blackboard.CanMove && blackboard.InputAllowed) return;
            LocomotionPlan plan = blackboard.Plan;
            blackboard.Plan = new LocomotionPlan(false, blackboard.PhysicallyCrouched,
                false, Vector2.zero, blackboard.ViewYaw, plan.MoveSpeed, plan.AnimationSpeed);
        }
        internal void RecordPosture(bool physicallyCrouched, bool canStand)
        {
            blackboard.PhysicallyCrouched = physicallyCrouched;
            blackboard.CanStand = canStand;
            if (!physicallyCrouched || blackboard.Plan.Crouched) return;
            LocomotionPlan plan = blackboard.Plan;
            blackboard.Plan = new LocomotionPlan(plan.CanMove, true, plan.Strafe, plan.Move,
                plan.ViewYaw, Mathf.Min(plan.MoveSpeed, context.Data.Crouch.MoveSpeed), plan.AnimationSpeed);
        }
    }
}
