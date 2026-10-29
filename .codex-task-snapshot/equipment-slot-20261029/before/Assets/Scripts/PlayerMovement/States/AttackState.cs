using RPG4.Backpack.Items;
using RPG4.PlayerIntent;
using RPG4.StateMachine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>腰射/瞄准射击共享武器使用状态；模式切换不重建武器或时序。</summary>
    internal sealed class AttackState : State
    {
        private readonly UpperBodyRootState root;
        private readonly PlayerContext context;
        internal HipAttackState Hip { get; }
        internal AimAttackState Aim { get; }
        private bool CanStartShot => root.FireInputAvailable && context.Weapon != null
            && context.Weapon.CanFire(root.FirePressed, root.FireHeld);
        private bool WaitingAutomatic => root.FireHeld && context.Weapon != null
            && context.Weapon.Definition.FireMode == WeaponFireMode.Automatic;
        // 自动枪允许长按在冷却中等待；半自动仅响应合格的按下，不排队短按。
        internal bool CanEnter => WaitingAutomatic || CanStartShot;

        internal AttackState(HierarchicalStateMachine machine, UpperBodyRootState parent, PlayerContext context)
            : base(machine, parent)
        {
            root = parent;
            this.context = context;
            Hip = new HipAttackState(machine, this, root);
            Aim = new AimAttackState(machine, this, root);
        }
        protected override State GetInitialState() => root.AimHeld ? (State)Aim : Hip;
        protected override State GetTransition()
        {
            if (root.ReloadPressed) return root.Reload;
            if (!root.CanAct) return root.Normal;
            WeaponActionSnapshot action = root.Action;
            if (!action.ShotActive && !action.FadingOut && !WaitingAutomatic && !CanStartShot)
                return root.AimHeld ? (State)root.Aim : root.Normal;
            return null;
        }
        protected override void OnUpdate(float deltaTime)
        {
            if (deltaTime <= 0f || !root.CanAct) return;
            context.Weapon.TryFire(root.FirePressed, root.FireHeld, out _);
            root.AdvanceAction();
        }
        internal void Present(bool aiming)
        {
            WeaponActionSnapshot frame = root.Action;
            bool aim = root.CanAct && aiming;
            bool hasWeapon = root.CanAct && (WaitingAutomatic || frame.ShotActive || frame.FadingOut);
            float speed = aim ? context.Data.Aim.MoveSpeed
                : hasWeapon ? context.Data.HipFire.MoveSpeed : context.Data.Exploration.MoveSpeed;
            context.Intents.RecordWeapon(new UpperBodyDemand(aim || hasWeapon, aim,
                root.CanAct && (frame.ShotActive || frame.FadingOut), false, speed, !aim && hasWeapon ? 1.35f : 1f,
                attackCamera: hasWeapon), frame, 0f);
        }
        protected override void OnExit() => root.ClearAction();
    }

    internal sealed class HipAttackState : State
    {
        private readonly AttackState attack;
        private readonly UpperBodyRootState root;
        internal HipAttackState(HierarchicalStateMachine machine, AttackState parent, UpperBodyRootState root)
            : base(machine, parent) { attack = parent; this.root = root; }
        protected override State GetTransition() => root.AimHeld ? attack.Aim : null;
        protected override void OnUpdate(float deltaTime) => attack.Present(false);
    }

    internal sealed class AimAttackState : State
    {
        private readonly AttackState attack;
        private readonly UpperBodyRootState root;
        internal AimAttackState(HierarchicalStateMachine machine, AttackState parent, UpperBodyRootState root)
            : base(machine, parent) { attack = parent; this.root = root; }
        protected override State GetTransition() => !root.AimHeld ? attack.Hip : null;
        protected override void OnUpdate(float deltaTime) => attack.Present(true);
    }
}
