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
            UpperBodyDemand demand = blackboard.WeaponDemand;
            blackboard.Plan = PlayerMovementRules.ResolvePlan(canMove, physicallyCrouched, canStand,
                blackboard.CrouchHeld, blackboard.Move, blackboard.ViewYaw, demand,
                context.Data.Crouch.MoveSpeed, context.Data.Exploration.MoveInputThreshold,
                context.Data.Aim.MoveInputThreshold);
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
