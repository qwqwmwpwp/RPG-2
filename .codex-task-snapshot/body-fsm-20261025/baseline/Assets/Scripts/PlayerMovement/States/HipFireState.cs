using System;
using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>腰射叶状态：选择腰射运动参数，并在统一模式改变时转入瞄准。</summary>
    internal sealed class HipFireState : State
    {
        [Serializable]
        internal sealed class Data
        {
            [SerializeField, Min(0.1f)] private float moveSpeed = 6f;
            internal float MoveSpeed => moveSpeed;
        }

        private readonly AttackState attack;
        private readonly PlayerContext context;

        internal HipFireState(HierarchicalStateMachine machine, AttackState parent, PlayerContext context)
            : base(machine, parent)
        {
            attack = parent;
            this.context = context;
        }

        protected override State GetTransition() => context.Mode == PlayerMode.Aim
            ? attack.Aim : null;

        protected override void OnEnter() => attack.SetLocomotion(PlayerMode.HipFire, 1.35f);
    }
}
