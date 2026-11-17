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
            IPlayerReadState read = context.ReadState;
            bool canMove = read.Locomotion.CanMove && read.Input.InputAllowed;
            UpperBodyDemand demand = read.Weapon.WeaponDemand;
            blackboard.Plan = PlayerMovementRules.ResolvePlan(canMove, read.Locomotion.PhysicallyCrouched,
                read.Locomotion.CanStand, read.Input.CrouchHeld, read.Input.Move, read.View.ViewYaw, demand,
                context.Data.Crouch.MoveSpeed, context.Data.Exploration.MoveInputThreshold,
                context.Data.Aim.MoveInputThreshold);
        }

        internal void RefreshAvailability()
        {
            // Revocation is a safety boundary: never preserve a moving pending plan after stop/death.
            if (blackboard.CanMove && context.ReadState.Input.InputAllowed) return;
            LocomotionPlan plan = blackboard.Plan;
            blackboard.Plan = new LocomotionPlan(false, blackboard.PhysicallyCrouched,
                false, Vector2.zero, blackboard.ViewYaw, plan.MoveSpeed, plan.AnimationSpeed);
        }
        internal void RecordPosture(bool physicallyCrouched, bool canStand)
        {
            blackboard.PhysicallyCrouched = physicallyCrouched;
            blackboard.CanStand = canStand;
            // Physical results are next-cycle facts. Rewriting the pending desired plan here would
            // repeatedly replace a requested stand with the crouch executed from the old plan.
        }
    }
}
