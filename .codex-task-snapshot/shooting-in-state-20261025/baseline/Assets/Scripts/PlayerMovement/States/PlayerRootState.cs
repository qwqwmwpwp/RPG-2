using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色共同根：选择初始状态组，维护角色公共表现，并在停机时复位。</summary>
    internal sealed class PlayerRootState : State
    {
        private readonly PlayerContext context;

        internal ExplorationState Exploration { get; }
        internal AttackState Attack { get; }

        internal PlayerRootState(HierarchicalStateMachine machine, PlayerContext context) : base(machine)
        {
            this.context = context;
            Exploration = new ExplorationState(machine, this, context);
            Attack = new AttackState(machine, this, context);
        }

        protected override State GetInitialState() => context.Mode == PlayerMode.Exploration
            ? (State)Exploration : Attack;

        protected override void OnUpdate(float deltaTime)
        {
            context.SetWeaponMode(context.Mode == PlayerMode.Aim, context.ShotPoseActive);
            if (!context.CanControl) context.SetPlanarVelocity(Vector3.zero);
        }

        protected override void OnExit()
        {
            context.SetWeaponMode(false, false);
            context.SetPlanarVelocity(Vector3.zero);
            if (context.AnimatorIsAvailable)
                context.Content.Animator.Play(context.Exploration.IdleAnimation, 0, 0f);
        }
    }
}
