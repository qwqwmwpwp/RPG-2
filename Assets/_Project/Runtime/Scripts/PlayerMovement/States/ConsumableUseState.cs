using RPG4.StateMachine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>消耗动作期间的上身行为及武器中断；不参与库存与治疗提交。</summary>
    internal sealed class ConsumableUseState : State
    {
        private readonly UpperBodyRootState root;
        private readonly PlayerContext context;
        internal ConsumableUseState(HierarchicalStateMachine machine, UpperBodyRootState parent, PlayerContext context)
            : base(machine, parent) { root = parent; this.context = context; }
        internal void PrepareFrame()
            => context.Weapon?.BeginFrame(root.Now, context.Owner.SimulationTick, false);
        protected override State GetTransition()
        {
            if (!root.UsingConsumable) return root.Weapon;
            if (root.FrameDeltaTime <= 0f || context.Owner.SimulationInterrupted || !context.CanUseWeapon) return null;
            var input = context.ReadState.Input;
            var weapon = context.Weapon;
            if (!(input.FirePressed && weapon.CanFire(input.FirePressed, input.FireHeld)
                || input.ReloadPressed && weapon.CanReload)) return null;
            // Cancel 的发布由宿主延后到本次状态切换完成，保留触发中断的新边沿。
            context.Owner.Consumables.Cancel();
            root.Weapon.PrepareFrame(root.FrameDeltaTime, root.Now);
            return root.Weapon;
        }
        protected override void OnUpdate(float deltaTime)
            => context.Intents.RecordWeapon(PlayerMovementDemand.Compose(context.Data, false,
                false, false, false, false, false, root.UsingConsumable), default, 0f);
    }
}
