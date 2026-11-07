using RPG4.StateMachine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>上身互斥动作分组；消耗品事实仍由 PlayerConsumableUse 持有。</summary>
    internal sealed class UpperBodyRootState : State
    {
        private readonly PlayerContext context;
        internal WeaponState Weapon { get; }
        internal ConsumableUseState Consumable { get; }
        internal bool UsingConsumable => context.Owner.Consumables != null && context.Owner.Consumables.Active;
        internal bool ConsumableStateActive => ActiveChild == Consumable;
        internal double Now { get; private set; }
        internal float FrameDeltaTime { get; private set; }

        internal UpperBodyRootState(HierarchicalStateMachine machine, PlayerContext context) : base(machine)
        {
            this.context = context;
            Weapon = new WeaponState(machine, this, context);
            Consumable = new ConsumableUseState(machine, this, context);
        }
        protected override State GetInitialState() => UsingConsumable ? (State)Consumable : Weapon;
        internal void PrepareFrame(float deltaTime, double now)
        {
            Now = now;
            FrameDeltaTime = deltaTime;
            if (UsingConsumable) Consumable.PrepareFrame();
            else Weapon.PrepareFrame(deltaTime, now);
        }
        // 外部事实变更只同步状态与需求，不推进武器时序或消费输入。
        internal void PreparePublication()
        {
            FrameDeltaTime = 0f;
            Weapon.SuspendFrame();
        }
        internal void RecordInactiveDemand()
            => context.Intents.RecordWeapon(PlayerMovementDemand.Compose(context.Data, false,
                false, false, false, false, false, UsingConsumable), default, 0f);
        internal void CancelWeaponAction() => Weapon.CancelWeaponAction();
        internal void BeginInputSession() => Weapon.BeginInputSession();
        protected override void OnExit() => CancelWeaponAction();
    }
}
