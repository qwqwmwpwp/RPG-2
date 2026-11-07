using RPG4.PlayerIntent;
using RPG4.StateMachine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>下身行为状态；物理执行委托给世界适配，不持有表现组件。</summary>
    internal sealed class LowerBodyRootState : State
    {
        private readonly PlayerContext context;
        internal bool PhysicallyCrouched => context.World.PhysicallyCrouched;
        internal PostureState Standing { get; }
        internal PostureState Crouching { get; }
        internal LowerBodyRootState(HierarchicalStateMachine machine, PlayerContext context) : base(machine)
        {
            this.context = context;
            Standing = new PostureState(machine, this, context, false);
            Crouching = new PostureState(machine, this, context, true);
        }
        protected override State GetInitialState() => context.ReadState.Locomotion.Plan.Crouched ? Crouching : Standing;
        protected override void OnFixedUpdate(float deltaTime)
        {
            if (!context.ReadState.Locomotion.CanMove)
            {
                RestoreStrafeAngularLimit();
                return;
            }
            bool applied = context.World.ApplyPosture(context.ReadState.Locomotion.Plan.Crouched);
            context.Intents.RecordPosture(PhysicallyCrouched, !PhysicallyCrouched
                || (applied && context.ReadState.Locomotion.CanStand));
        }
        internal bool CanStand() => context.World.CanStand();
        internal void ResetTurn() => context.World.ResetTurn();
        internal void RestoreStrafeAngularLimit() => context.World.RestoreStrafeAngularLimit();
        internal void Move(bool strafe, float deltaTime) => context.World.Move(context.ReadState.Locomotion.Plan, strafe, deltaTime);
        protected override void OnExit() => context.World.Stop();
    }

    /// <summary>站立/蹲伏两个兄弟组；进入组时按本帧完整计划选择 Free/Strafe。</summary>
    internal sealed class PostureState : State
    {
        private readonly LowerBodyRootState root;
        private readonly PlayerContext context;
        private readonly bool crouched;
        internal LocomotionState Free { get; }
        internal LocomotionState Strafe { get; }
        internal PostureState(HierarchicalStateMachine machine, LowerBodyRootState parent,
            PlayerContext context, bool crouched) : base(machine, parent)
        {
            root = parent;
            this.context = context;
            this.crouched = crouched;
            Free = new LocomotionState(machine, this, root, context, false);
            Strafe = new LocomotionState(machine, this, root, context, true);
        }
        protected override State GetInitialState() => context.ReadState.Locomotion.Plan.Strafe ? Strafe : Free;
        protected override State GetTransition() => context.ReadState.Locomotion.Plan.Crouched == crouched ? null
            : crouched ? root.Standing : root.Crouching;
    }

    internal sealed class LocomotionState : State
    {
        private readonly PostureState posture;
        private readonly LowerBodyRootState root;
        private readonly PlayerContext context;
        private readonly bool strafe;
        internal LocomotionState(HierarchicalStateMachine machine, PostureState parent,
            LowerBodyRootState root, PlayerContext context, bool strafe) : base(machine, parent)
        {
            posture = parent;
            this.root = root;
            this.context = context;
            this.strafe = strafe;
        }
        protected override State GetTransition() => context.ReadState.Locomotion.Plan.Strafe == strafe ? null
            : strafe ? posture.Free : posture.Strafe;
        protected override void OnEnter() => root.ResetTurn();
        protected override void OnExit()
        {
            if (strafe) root.RestoreStrafeAngularLimit();
        }
        protected override void OnFixedUpdate(float deltaTime) => root.Move(strafe, deltaTime);
    }
}
