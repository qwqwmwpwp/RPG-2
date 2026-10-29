using UnityEngine;
using RPG4.StateMachine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>移动状态：执行相机相对移动、播放跑步动画，并在无移动输入时返回待机。</summary>
    internal sealed class MoveState : State
    {
        private readonly ExplorationState exploration;
        private readonly PlayerContext context;
        private readonly ExplorationState.Data data;
        private float turnVelocity;

        internal MoveState(HierarchicalStateMachine machine, ExplorationState parent, PlayerContext context)
            : base(machine, parent)
        {
            exploration = parent;
            this.context = context;
            data = context.Exploration;
        }

        protected override State GetTransition()
        {
            return exploration.HasMovementInput ? null : exploration.Idle;
        }

        protected override void OnEnter()
        {
            turnVelocity = 0f;
            exploration.PlayAnimation(data.MoveAnimation);
        }

        protected override void OnExit()
        {
            turnVelocity = 0f;
        }

        protected override void OnUpdate(float deltaTime)
        {
            exploration.PlayAnimation(data.MoveAnimation);
        }

        protected override void OnFixedUpdate(float deltaTime)
        {
            if (!exploration.HasMovementInput)
            {
                turnVelocity = 0f;
                exploration.SetPlanarVelocity(Vector3.zero);
                return;
            }

            Rigidbody body = context.Content.Body;
            Vector2 moveInput = context.Input.Move;
            Quaternion viewYaw = Quaternion.Euler(0f, context.Content.CameraController.ViewYaw, 0f);
            Vector3 direction = viewYaw * new Vector3(moveInput.x, 0f, moveInput.y);
            exploration.SetPlanarVelocity(direction * data.MoveSpeed);

            float targetYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            float yaw = Mathf.SmoothDampAngle(body.rotation.eulerAngles.y, targetYaw,
                ref turnVelocity, data.TurnSmoothTime, Mathf.Infinity, deltaTime);
            body.MoveRotation(Quaternion.Euler(0f, yaw, 0f));
        }
    }
}
