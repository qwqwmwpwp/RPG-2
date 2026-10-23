using UnityEngine;
using RPG4.StateMachine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>待机状态：播放待机动画、清除水平运动，并在有移动输入时切换到移动状态。</summary>
    internal sealed class IdleState : State
    {
        private readonly ExplorationState exploration;
        private readonly ExplorationState.Data data;

        internal IdleState(HierarchicalStateMachine machine, ExplorationState parent, PlayerContext context)
            : base(machine, parent)
        {
            exploration = parent;
            data = context.Exploration;
        }

        protected override State GetTransition()
        {
            return exploration.HasMovementInput ? exploration.Move : null;
        }

        protected override void OnEnter()
        {
            exploration.PlayAnimation(data.IdleAnimation);
        }

        protected override void OnUpdate(float deltaTime)
        {
            exploration.PlayAnimation(data.IdleAnimation);
        }

        protected override void OnFixedUpdate(float deltaTime)
        {
            exploration.SetPlanarVelocity(Vector3.zero);
        }
    }
}
