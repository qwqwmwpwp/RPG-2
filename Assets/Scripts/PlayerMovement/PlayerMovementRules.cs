using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Pure rules; configuration and business facts are supplied by the character host.</summary>
    public static class PlayerMovementRules
    {
        public static MovementRuleResult Evaluate(in MovementRuleContext context, in PlayerInputFrame input)
        {
            bool canMove = context.Active && context.Alive;
            bool canAct = canMove && context.CanUseWeapon && !context.IsHurt;
            UpperBodyDemand demand = ComposeDemand(context, canAct, input.AimHeld);
            LocomotionPlan plan = ResolvePlan(canMove, context.PhysicallyCrouched, context.CanStand,
                input.CrouchHeld, input.Move, context.ViewYaw, demand, context.CrouchMoveSpeed,
                context.FreeMoveInputThreshold, context.StrafeMoveInputThreshold);
            return new MovementRuleResult(plan, demand, demand.AimCamera);
        }

        public static UpperBodyDemand ComposeDemand(in MovementRuleContext context, bool canAct, bool aiming)
        {
            if (context.UsingConsumable) return new UpperBodyDemand(true, false, false, false, context.AimMoveSpeed, 1f);
            bool aim = canAct && aiming;
            bool reload = canAct && context.Reloading;
            bool attack = canAct && !reload && (context.ShotActive || context.FadingOut || context.WaitingAutomatic);
            float speed = reload ? context.ReloadMoveSpeed : aim ? context.AimMoveSpeed
                : attack ? context.HipFireMoveSpeed : context.ExplorationMoveSpeed;
            return new UpperBodyDemand(aim || reload || attack, aim && !reload,
                canAct && !reload && (context.ShotActive || context.FadingOut), reload, speed,
                !aim && attack ? 1.35f : 1f, attackCamera: attack);
        }

        public static LocomotionPlan ResolvePlan(bool canMove, bool physicallyCrouched, bool canStand,
            bool crouchHeld, Vector2 move, float viewYaw, UpperBodyDemand demand, float crouchSpeed,
            float freeThreshold, float strafeThreshold)
        {
            bool crouched = !canMove ? physicallyCrouched : crouchHeld || (physicallyCrouched && !canStand);
            float speed = crouched ? Mathf.Min(demand.MoveSpeed, crouchSpeed) : demand.MoveSpeed;
            move = canMove ? Vector2.ClampMagnitude(move, 1f) : Vector2.zero;
            float threshold = demand.Strafe ? strafeThreshold : freeThreshold;
            if (move.sqrMagnitude <= threshold * threshold) move = Vector2.zero;
            return new LocomotionPlan(canMove, crouched, demand.Strafe, move, viewYaw,
                Mathf.Max(0f, speed), demand.AnimationSpeed);
        }
    }
}
