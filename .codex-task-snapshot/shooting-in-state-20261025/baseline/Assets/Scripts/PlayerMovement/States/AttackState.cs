using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>持枪父状态：组织瞄准与腰射，保持共用的方向运动和腿部动画连续。</summary>
    internal sealed class AttackState : State
    {
        private static readonly int AimMoveX = Animator.StringToHash("AimMoveX");
        private static readonly int AimMoveY = Animator.StringToHash("AimMoveY");
        private static readonly int LocomotionSpeed = Animator.StringToHash("AimLocomotionSpeed");
        private readonly PlayerRootState root;
        private readonly PlayerContext context;
        private readonly AimState.Data data;
        private PlayerMode mode;
        private float locomotionSpeed;
        private float turnVelocity;
        private Vector2 animationMove;

        internal AimState Aim { get; }
        internal HipFireState HipFire { get; }

        internal AttackState(HierarchicalStateMachine machine, PlayerRootState parent, PlayerContext context)
            : base(machine, parent)
        {
            root = parent;
            this.context = context;
            data = context.Aim;
            Aim = new AimState(machine, this, context);
            HipFire = new HipFireState(machine, this, context);
        }

        protected override State GetInitialState() => context.Mode == PlayerMode.Aim ? (State)Aim : HipFire;

        protected override State GetTransition() => context.Mode == PlayerMode.Exploration
            ? root.Exploration : null;

        protected override void OnEnter()
        {
            turnVelocity = 0f;
            if (context.AnimatorIsAvailable)
            {
                Animator animator = context.Content.Animator;
                animationMove = new Vector2(animator.GetFloat(AimMoveX), animator.GetFloat(AimMoveY));
            }
        }

        internal void SetLocomotion(PlayerMode mode, float locomotionSpeed)
        {
            this.mode = mode;
            this.locomotionSpeed = locomotionSpeed;
            turnVelocity = 0f;
        }

        protected override void OnUpdate(float deltaTime)
        {
            if (!context.AnimatorIsAvailable) return;
            Animator animator = context.Content.Animator;
            animator.SetFloat(LocomotionSpeed, locomotionSpeed);
            SetAnimationMove(animationMove);
            // 互切不重播腿部；Animator 恢复后也能投影当前逻辑模式。
            int stateHash = Animator.StringToHash(data.AimAnimation);
            if (animator.GetCurrentAnimatorStateInfo(0).fullPathHash != stateHash
                && (!animator.IsInTransition(0)
                    || animator.GetNextAnimatorStateInfo(0).fullPathHash != stateHash))
                animator.CrossFadeInFixedTime(data.AimAnimation,
                    data.AnimationTransitionDuration, layer: 0);
        }

        protected override void OnExit()
        {
            turnVelocity = 0f;
        }

        protected override void OnFixedUpdate(float deltaTime)
        {
            if (!context.CanUseWeapon || context.Mode != mode)
            {
                context.SetPlanarVelocity(Vector3.zero);
                turnVelocity = 0f;
                return;
            }

            Rigidbody body = context.Content.Body;
            float viewYaw = context.Content.CameraController.ViewYaw;
            Vector2 move = Vector2.ClampMagnitude(context.Input.Move, 1f);
            if (move.sqrMagnitude <= data.MoveInputThreshold * data.MoveInputThreshold)
                move = Vector2.zero;
            Vector3 direction = Quaternion.Euler(0f, viewYaw, 0f) * new Vector3(move.x, 0f, move.y);
            float moveSpeed = mode == PlayerMode.HipFire ? context.HipFire.MoveSpeed : data.MoveSpeed;
            context.SetPlanarVelocity(direction * moveSpeed);

            // 原地也朝向观察 yaw；旋转只使用 Y 轴，根节点始终直立。
            float yaw = Mathf.SmoothDampAngle(body.rotation.eulerAngles.y, viewYaw,
                ref turnVelocity, data.TurnSmoothTime, Mathf.Infinity, deltaTime);
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            body.MoveRotation(rotation);

            Vector3 localDirection = Quaternion.Inverse(rotation) * direction;
            Vector2 target = Vector2.ClampMagnitude(new Vector2(localDirection.x, localDirection.z), 1f);
            float blend = data.AnimationParameterSmoothTime <= 0f ? 1f
                : 1f - Mathf.Exp(-deltaTime / data.AnimationParameterSmoothTime);
            animationMove = Vector2.ClampMagnitude(Vector2.Lerp(animationMove, target, blend), 1f);
            SetAnimationMove(animationMove);
        }

        private void SetAnimationMove(Vector2 move)
        {
            if (!context.AnimatorIsAvailable) return;
            context.Content.Animator.SetFloat(AimMoveX, move.x);
            context.Content.Animator.SetFloat(AimMoveY, move.y);
        }
    }
}
