using System;
using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>探索父状态：组织待机、移动子状态，并提供共用的动画播放入口。</summary>
    internal sealed class ExplorationState : State
    {
        /// <summary>探索父状态及所有子状态共享的配置。</summary>
        [Serializable]
        internal sealed class Data
        {
            [SerializeField, Min(0.1f)] private float moveSpeed = 4f;
            [SerializeField, Min(0.01f)] private float turnSmoothTime = 0.12f;
            [SerializeField, Min(0f)] private float moveInputThreshold = 0.01f;
            [SerializeField] private string idleAnimation = "Base Layer.idle";
            [SerializeField] private string moveAnimation = "Base Layer.run";
            [SerializeField, Min(0f)] private float animationTransitionDuration = 0.25f;

            internal float MoveSpeed => moveSpeed;
            internal float TurnSmoothTime => turnSmoothTime;
            internal float MoveInputThreshold => moveInputThreshold;
            internal string IdleAnimation => idleAnimation;
            internal string MoveAnimation => moveAnimation;
            internal float AnimationTransitionDuration => animationTransitionDuration;
        }

        private readonly PlayerContext context;
        private readonly Data data;
        private string currentAnimation;

        internal IdleState Idle { get; }
        internal MoveState Move { get; }

        internal bool HasMovementInput => context.Content.Body != null && !context.Content.Body.isKinematic
            && context.Input != null && context.Input.CanReadGameplayInput
            && context.Content.CameraController != null && context.Content.CameraController.isActiveAndEnabled
            && context.Input.Move.sqrMagnitude > data.MoveInputThreshold * data.MoveInputThreshold;

        internal ExplorationState(HierarchicalStateMachine machine, PlayerContext context)
            : base(machine)
        {
            this.context = context;
            data = context.Exploration;
            Idle = new IdleState(machine, this, context);
            Move = new MoveState(machine, this, context);
        }

        protected override State GetInitialState() => Idle;

        protected override void OnEnter()
        {
            currentAnimation = null;
        }

        protected override void OnExit()
        {
            SetPlanarVelocity(Vector3.zero);
            if (AnimatorIsAvailable()) context.Content.Animator.Play(data.IdleAnimation, 0, 0f);
            currentAnimation = null;
        }

        internal void SetPlanarVelocity(Vector3 velocity)
        {
            Rigidbody body = context.Content.Body;
            if (body == null || body.isKinematic) return;

            body.velocity = new Vector3(velocity.x, body.velocity.y, velocity.z);
            body.angularVelocity = Vector3.zero;
        }

        internal void PlayAnimation(string targetAnimation)
        {
            if (!AnimatorIsAvailable())
            {
                currentAnimation = null;
                return;
            }

            if (targetAnimation == currentAnimation) return;

            context.Content.Animator.CrossFadeInFixedTime(targetAnimation, data.AnimationTransitionDuration, layer: 0);
            currentAnimation = targetAnimation;
        }

        private bool AnimatorIsAvailable()
        {
            return context.Content.Animator != null && context.Content.Animator.runtimeAnimatorController != null
                && context.Content.Animator.isActiveAndEnabled;
        }
    }
}
