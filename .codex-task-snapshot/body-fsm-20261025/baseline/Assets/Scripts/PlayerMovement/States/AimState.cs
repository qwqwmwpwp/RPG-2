using System;
using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>瞄准叶状态：选择瞄准运动参数，并在统一模式改变时转入腰射。</summary>
    internal sealed class AimState : State
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

        private readonly AttackState attack;
        private readonly PlayerContext context;

        internal AimState(HierarchicalStateMachine machine, AttackState parent, PlayerContext context)
            : base(machine, parent)
        {
            attack = parent;
            this.context = context;
        }

        protected override State GetTransition() => context.Mode == PlayerMode.HipFire
            ? attack.HipFire : null;

        protected override void OnEnter() => attack.SetLocomotion(PlayerMode.Aim, 1f);
    }
}
