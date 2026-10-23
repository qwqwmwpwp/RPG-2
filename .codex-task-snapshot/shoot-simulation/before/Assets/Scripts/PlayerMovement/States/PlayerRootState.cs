using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色共同根：仅在模式改变时切换，并在停机时复位角色表现。</summary>
    internal sealed class PlayerRootState : State
    {
        private readonly PlayerContext context;
        private readonly ExplorationState exploration;
        private readonly AimState aim;

        internal PlayerRootState(HierarchicalStateMachine machine, PlayerContext context) : base(machine)
        {
            this.context = context;
            exploration = new ExplorationState(machine, this, context);
            aim = new AimState(machine, this, context);
        }

        private State RequestedMode => context.WantsAim ? (State)aim : exploration;

        protected override State GetInitialState() => RequestedMode;

        protected override State GetTransition()
        {
            State requested = RequestedMode;
            return ActiveChild == requested ? null : requested;
        }

        protected override void OnUpdate(float deltaTime)
        {
            if (!context.CanControl) context.SetPlanarVelocity(Vector3.zero);
        }

        protected override void OnExit()
        {
            context.SetAiming(false);
            context.SetPlanarVelocity(Vector3.zero);
            if (context.AnimatorIsAvailable)
                context.Content.Animator.Play(context.Exploration.IdleAnimation, 0, 0f);
        }
    }
}
