using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>下身动画执行器；只接收桥接层提交的值数据。</summary>
    internal sealed class PlayerLocomotionPresentation
    {
        private static readonly int MoveSpeed = Animator.StringToHash("MoveSpeed");
        private static readonly int AimMoveX = Animator.StringToHash("AimMoveX");
        private static readonly int AimMoveY = Animator.StringToHash("AimMoveY");
        private static readonly int AimLocomotionSpeed = Animator.StringToHash("AimLocomotionSpeed");
        private readonly PlayerStateData data;
        private Vector2 animationMove;
        private float previousViewYaw;
        private bool hasViewYaw;
        private int turnAnimation;
        private bool AnimatorAvailable => data.Content.Animator != null
            && data.Content.Animator.runtimeAnimatorController != null && data.Content.Animator.isActiveAndEnabled;
        internal PlayerLocomotionPresentation(PlayerStateData data) { this.data = data; }
        internal void Apply(PlayerMotionTarget frame, float deltaTime)
        {
            if (!AnimatorAvailable) return;
            Animator animator = data.Content.Animator;
            LocomotionPlan plan = frame.Plan;
            bool strafe = plan.Strafe;
            Vector2 movement = new Vector2(frame.PlanarVelocity.x, frame.PlanarVelocity.z);
            Vector2 target = Vector2.zero;
            if (plan.CanMove && frame.CanMove && plan.MoveSpeed > 0f)
            {
                Vector3 direction = new Vector3(movement.x, 0f, movement.y) / plan.MoveSpeed;
                // Strafe 的可见骨架已对齐观察角，移动动画也采用同一朝向基准。
                Quaternion facing = strafe ? Quaternion.Euler(0f, plan.ViewYaw, 0f) : Quaternion.Euler(0f, frame.BodyYaw, 0f);
                Vector3 local = Quaternion.Inverse(facing) * direction;
                target = Vector2.ClampMagnitude(new Vector2(local.x, local.z), 1f);
            }
            float smooth = data.Aim.AnimationParameterSmoothTime;
            animationMove = plan.CanMove && frame.CanMove
                ? PlayerMotionMath.SmoothMove(animationMove, target, smooth, smooth, deltaTime) : Vector2.zero;
            AnimateState(animator, frame, plan, strafe, deltaTime);
            animator.SetFloat(MoveSpeed, animationMove.magnitude);
            animator.SetFloat(AimMoveX, animationMove.x);
            animator.SetFloat(AimMoveY, animationMove.y);
            animator.SetFloat(AimLocomotionSpeed, plan.AnimationSpeed);
        }
        private void AnimateState(Animator animator, PlayerMotionTarget frame, LocomotionPlan plan, bool strafe, float deltaTime)
        {
            float yawSpeed = hasViewYaw && deltaTime > 0f
                ? Mathf.DeltaAngle(previousViewYaw, plan.ViewYaw) / deltaTime : 0f;
            previousViewYaw = plan.ViewYaw;
            hasViewYaw = true;
            AimData settings = data.Aim;
            AnimatorStateInfo state = animator.IsInTransition(0)
                ? animator.GetNextAnimatorStateInfo(0) : animator.GetCurrentAnimatorStateInfo(0);
            bool canTurn = strafe && plan.CanMove && frame.IsAiming
                && Mathf.Max(plan.Move.magnitude, animationMove.magnitude) <= settings.TurnAnimationMoveThreshold;
            int requestedTurn = canTurn && Mathf.Abs(yawSpeed) > settings.TurnAnimationThreshold
                ? (yawSpeed < 0f ? -1 : 1) : 0;
            bool wasTurning = turnAnimation != 0;
            if (!canTurn) turnAnimation = 0;
            else if (requestedTurn != 0) turnAnimation = requestedTurn;
            else if (turnAnimation != 0 && state.normalizedTime >= 1f) turnAnimation = 0;

            string animation = turnAnimation != 0
                ? "Base Layer." + (plan.Crouched ? "CrouchAimTurn" : "AimTurn")
                    + (turnAnimation < 0 ? "Left" : "Right")
                : plan.Crouched
                    ? (strafe ? "Base Layer.CrouchAimLocomotion" : "Base Layer.CrouchFreeLocomotion")
                    : strafe ? settings.AimAnimation : "Base Layer.FreeLocomotion";
            int hash = Animator.StringToHash(animation);
            // 持续转身时重播已完成的单次片段；开始移动时可以立即打断，避免等待片尾。
            bool repeatTurn = turnAnimation != 0 && state.fullPathHash == hash && state.normalizedTime >= 1f;
            if (state.fullPathHash == hash && !repeatTurn) return;
            float transition = wasTurning || turnAnimation != 0 ? settings.TurnAnimationTransitionDuration
                : strafe ? settings.AnimationTransitionDuration : data.Exploration.AnimationTransitionDuration;
            animator.CrossFadeInFixedTime(animation, transition, layer: 0, fixedTimeOffset: 0f);
        }
        internal void Reset(bool crouched)
        {
            animationMove = Vector2.zero;
            hasViewYaw = false;
            turnAnimation = 0;
            if (!AnimatorAvailable) return;
            Animator animator = data.Content.Animator;
            animator.SetFloat(MoveSpeed, 0f);
            animator.SetFloat(AimMoveX, 0f);
            animator.SetFloat(AimMoveY, 0f);
            animator.SetFloat(AimLocomotionSpeed, 1f);
            animator.Play(crouched ? "Base Layer.CrouchFreeLocomotion" : "Base Layer.FreeLocomotion", 0, 0f);
        }
    }
}
