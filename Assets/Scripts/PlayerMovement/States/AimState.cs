using System;
using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>瞄准叶状态：观察方向决定移动方向和人物朝向，2D 动画树处理局部步伐。</summary>
    internal class AimState : State
    {
        [Serializable]
        internal sealed class Data
        {
            [SerializeField, Min(0.1f)] private float moveSpeed = 2.5f;
            [SerializeField, Min(0.01f)] private float turnSmoothTime = 0.12f;
            [SerializeField, Min(0f)] private float moveInputThreshold = 0.01f;
            [SerializeField] private string aimAnimation = "Base Layer.AimLocomotion";
            [SerializeField, Min(0f)] private float animationTransitionDuration = 0.15f;
            [SerializeField, Min(0f)] private float animationParameterSmoothTime = 0.1f;

            internal float MoveSpeed => moveSpeed;
            internal float TurnSmoothTime => turnSmoothTime;
            internal float MoveInputThreshold => moveInputThreshold;
            internal string AimAnimation => aimAnimation;
            internal float AnimationTransitionDuration => animationTransitionDuration;
            internal float AnimationParameterSmoothTime => animationParameterSmoothTime;
        }

        private static readonly int AimMoveX = Animator.StringToHash("AimMoveX");
        private static readonly int AimMoveY = Animator.StringToHash("AimMoveY");
        private static readonly int LocomotionSpeed = Animator.StringToHash("AimLocomotionSpeed");
        private readonly PlayerContext context;
        private readonly Data data;
        private readonly bool hipFire;
        private float turnVelocity;
        private Vector2 animationMove;

        internal AimState(HierarchicalStateMachine machine, PlayerRootState parent, PlayerContext context,
            bool hipFire = false)
            : base(machine, parent)
        {
            this.context = context;
            data = context.Aim;
            this.hipFire = hipFire;
        }

        protected override void OnEnter()
        {
            turnVelocity = 0f;
            if (context.AnimatorIsAvailable)
            {
                Animator animator = context.Content.Animator;
                animationMove = new Vector2(animator.GetFloat(AimMoveX), animator.GetFloat(AimMoveY));
                animator.SetFloat(LocomotionSpeed, hipFire ? 1.35f : 1f);
                // 腰射/ADS共享底层运动，不重播同一状态以免步伐在切换时跳变。
                int stateHash = Animator.StringToHash(data.AimAnimation);
                if (animator.GetCurrentAnimatorStateInfo(0).fullPathHash != stateHash
                    && (!animator.IsInTransition(0)
                        || animator.GetNextAnimatorStateInfo(0).fullPathHash != stateHash))
                    animator.CrossFadeInFixedTime(data.AimAnimation,
                        data.AnimationTransitionDuration, layer: 0);
            }
        }

        protected override void OnExit()
        {
            turnVelocity = 0f;
        }

        protected override void OnFixedUpdate(float deltaTime)
        {
            if (hipFire ? !context.WantsHipFire || context.WantsAim : !context.WantsAim)
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
            context.SetPlanarVelocity(direction * (hipFire ? context.HipFire.MoveSpeed : data.MoveSpeed));

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
