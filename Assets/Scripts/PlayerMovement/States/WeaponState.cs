using RPG4.PlayerIntent;
using RPG4.StateMachine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>组织武器子状态、扳机会话和时序；统一退出到消耗品分支。</summary>
    internal sealed class WeaponState : State
    {
        private readonly UpperBodyRootState root;
        private readonly PlayerContext context;
        private readonly WeaponActionTimeline timeline = new WeaponActionTimeline();
        private bool frameAvailable;
        private bool fireArmed = true;
        private long fireRearmFrame = -1;
        internal double Now => root.Now;
        internal bool CanAct => frameAvailable && !context.Owner.SimulationInterrupted && context.CanUseWeapon;
        internal bool AimHeld => CanAct && context.ReadState.Input.AimHeld;
        internal bool ReloadPressed => CanAct && context.ReadState.Input.ReloadPressed && context.Weapon.CanReload;
        internal bool FireInputAvailable => CanAct && fireArmed && context.Owner.SimulationTick > fireRearmFrame;
        internal bool FireHeld => FireInputAvailable && context.ReadState.Input.FireHeld;
        internal bool FirePressed => FireInputAvailable && context.ReadState.Input.FirePressed;
        internal bool IsReloading => context.Weapon != null && context.Weapon.IsReloading;
        internal WeaponActionSnapshot Action => timeline.Action;
        internal NormalState Normal { get; }
        internal AimState Aim { get; }
        internal AttackState Attack { get; }
        internal ReloadState Reload { get; }
        internal WeaponState(HierarchicalStateMachine machine, UpperBodyRootState parent, PlayerContext context)
            : base(machine, parent)
        {
            root = parent;
            this.context = context;
            Normal = new NormalState(machine, this);
            Aim = new AimState(machine, this);
            Attack = new AttackState(machine, this, context);
            Reload = new ReloadState(machine, this);
        }
        protected override State GetInitialState() => ReloadPressed ? (State)Reload
            : Attack.CanEnter ? Attack : AimHeld ? Aim : Normal;
        protected override State GetTransition() => root.UsingConsumable ? root.Consumable : null;
        internal void SuspendFrame() => frameAvailable = false;
        internal void PrepareFrame(float deltaTime, double now)
        {
            frameAvailable = deltaTime > 0f && context.CanUseWeapon
                && !context.ReadState.Weapon.WeaponCancellationRequested;
            if (!frameAvailable) CancelWeaponAction();
            else
            {
                // 取消前的 held 不能自行恢复；新按下边沿可以开启下一次扳机会话。
                if (context.Owner.SimulationTick > fireRearmFrame
                    && (!context.ReadState.Input.FireHeld || context.ReadState.Input.FirePressed)) fireArmed = true;
                context.Weapon.BeginFrame(now, context.Owner.SimulationTick, FireHeld);
                timeline.Advance(context.Weapon, now, context.Owner.SimulationTick);
            }
        }
        internal void AdvanceAction() => timeline.Advance(context.Weapon, Now, context.Owner.SimulationTick);
        internal void ClearAction() => timeline.ClearAction();
        private void RequireRelease()
        {
            fireArmed = false;
            fireRearmFrame = context.Owner.SimulationTick;
        }
        internal void BeginInputSession()
        {
            fireArmed = true;
            fireRearmFrame = -1;
        }
        internal void TickReady(bool aiming)
            => context.Intents.RecordWeapon(PlayerMovementDemand.Compose(context.Data, CanAct,
                aiming, false, false, false, false), default, 0f);
        internal void BeginReload()
        {
            RequireRelease();
            timeline.ClearAction();
            if (CanAct) context.Weapon.TryBeginReload();
        }
        internal void TickReload()
            => context.Intents.RecordWeapon(PlayerMovementDemand.Compose(context.Data, CanAct,
                false, true, false, false, false), default, context.Weapon != null ? context.Weapon.ReloadPhase : 0f);
        internal void EndReload()
        {
            RequireRelease();
            context.Weapon?.CancelAction();
            timeline.ClearAction();
            context.Intents.RecordEvent(PlayerEventKind.WeaponCancelled);
        }
        internal void CancelWeaponAction()
        {
            frameAvailable = false;
            RequireRelease();
            context.Weapon?.CancelAction();
            timeline.ClearAction();
            TickReady(false);
            context.Intents.ClearWeaponInput();
            context.Intents.AcknowledgeWeaponCancellation();
        }
        protected override void OnExit() => CancelWeaponAction();
    }

    internal sealed class NormalState : State
    {
        private readonly WeaponState root;
        internal NormalState(HierarchicalStateMachine machine, WeaponState parent)
            : base(machine, parent) { root = parent; }
        protected override State GetTransition() => root.ReloadPressed ? (State)root.Reload
            : root.Attack.CanEnter ? root.Attack : root.AimHeld ? root.Aim : null;
        protected override void OnUpdate(float deltaTime) => root.TickReady(false);
    }

    internal sealed class AimState : State
    {
        private readonly WeaponState root;
        internal AimState(HierarchicalStateMachine machine, WeaponState parent)
            : base(machine, parent) { root = parent; }
        protected override State GetTransition() => root.ReloadPressed ? (State)root.Reload
            : root.Attack.CanEnter ? root.Attack : !root.AimHeld ? root.Normal : null;
        protected override void OnUpdate(float deltaTime) => root.TickReady(true);
    }

    internal sealed class ReloadState : State
    {
        private readonly WeaponState root;
        internal ReloadState(HierarchicalStateMachine machine, WeaponState parent)
            : base(machine, parent) { root = parent; }
        protected override void OnEnter() => root.BeginReload();
        protected override State GetTransition()
        {
            if (!root.CanAct) return root.Normal;
            if (root.IsReloading) return null;
            return root.AimHeld ? (State)root.Aim : root.Normal;
        }
        protected override void OnUpdate(float deltaTime) => root.TickReload();
        protected override void OnExit() => root.EndReload();
    }
}
