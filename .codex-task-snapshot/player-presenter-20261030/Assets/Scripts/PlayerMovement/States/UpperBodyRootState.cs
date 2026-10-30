using RPG4.PlayerIntent;
using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>组织上身需求和角色扳机会话，武器推进只发生在正常帧准备阶段。</summary>
    internal sealed class UpperBodyRootState : State
    {
        private readonly PlayerContext context;
        private readonly PlayerWeaponPresentation presentation;
        private bool frameAvailable;
        private bool fireArmed = true;
        private int fireRearmFrame = -1;
        internal double Now { get; private set; }
        internal bool CanAct => frameAvailable && context.CanUseWeapon;
        internal bool AimHeld => CanAct && context.ReadState.Input.AimHeld;
        internal bool ReloadPressed => CanAct && context.ReadState.Input.ReloadPressed && context.Weapon.CanReload;
        internal bool FireInputAvailable => CanAct && fireArmed && Time.frameCount > fireRearmFrame;
        internal bool FireHeld => FireInputAvailable && context.ReadState.Input.FireHeld;
        internal bool FirePressed => FireInputAvailable && context.ReadState.Input.FirePressed;
        internal bool IsReloading => context.Weapon != null && context.Weapon.IsReloading;
        internal WeaponActionSnapshot Action => presentation.Action;
        internal NormalState Normal { get; }
        internal AimState Aim { get; }
        internal AttackState Attack { get; }
        internal ReloadState Reload { get; }
        internal UpperBodyRootState(HierarchicalStateMachine machine, PlayerContext context) : base(machine)
        {
            this.context = context;
            presentation = new PlayerWeaponPresentation(context);
            Normal = new NormalState(machine, this);
            Aim = new AimState(machine, this);
            Attack = new AttackState(machine, this, context);
            Reload = new ReloadState(machine, this);
        }
        protected override State GetInitialState() => ReloadPressed ? (State)Reload
            : Attack.CanEnter ? Attack : AimHeld ? Aim : Normal;

        // 准备时间与硬中断；不消费射击，也不维护第二套活动模式。
        internal void PrepareFrame(float deltaTime, double now)
        {
            Now = now;
            frameAvailable = deltaTime > 0f && context.CanUseWeapon
                && !context.ReadState.Weapon.WeaponCancellationRequested;
            if (!frameAvailable) CancelWeaponAction();
            else
            {
                if (Time.frameCount > fireRearmFrame && !context.ReadState.Input.FireHeld) fireArmed = true;
                context.Weapon.BeginFrame(now, Time.frameCount, FireHeld);
                presentation.AdvanceAction(now);
            }
        }
        internal void AdvanceAction() => presentation.AdvanceAction(Now);
        internal void ClearAction() => presentation.ClearAction();
        private void RequireRelease()
        {
            fireArmed = false;
            fireRearmFrame = Time.frameCount;
        }

        internal void TickReady(bool aiming)
        {
            bool aim = CanAct && aiming;
            context.Intents.RecordWeapon(new UpperBodyDemand(aim, aim, false, false,
                aim ? context.Data.Aim.MoveSpeed : context.Data.Exploration.MoveSpeed, 1f), default, 0f);
        }

        internal void BeginReload()
        {
            RequireRelease();
            presentation.Cancel();
            if (CanAct) context.Weapon.TryBeginReload();
        }
        internal void TickReload()
        {
            context.Intents.RecordWeapon(new UpperBodyDemand(true, false, false, true,
                context.Data.Reload.MoveSpeed, 1f), default, context.Weapon != null ? context.Weapon.ReloadPhase : 0f);
        }
        internal void EndReload()
        {
            RequireRelease();
            context.Weapon?.CancelAction();
            presentation.Cancel();
            // 换弹素材尾事件可能被完成/中断/站蹲切换跳过，明确恢复右手挂点。
            Animator animator = context.Content.Animator;
            if (animator != null)
                animator.gameObject.SendMessage("SwitchSocketByString", "To_Hand_R_Socket",
                    SendMessageOptions.DontRequireReceiver);
        }
        internal void ApplyLayerWeight() => presentation.ApplyLayerWeight();
        internal void SyncPresentation() => presentation.Sync();
        internal void CancelWeaponAction()
        {
            frameAvailable = false;
            RequireRelease();
            context.Weapon?.CancelAction();
            presentation.Cancel();
            context.Intents.RecordWeapon(new UpperBodyDemand(false, false, false, false,
                context.Data.Exploration.MoveSpeed, 1f), default, 0f);
            context.Intents.ClearWeaponInput();
            context.Intents.AcknowledgeWeaponCancellation();
            presentation.ApplyLayerWeight();
        }
        protected override void OnExit() => CancelWeaponAction();
    }

    internal sealed class NormalState : State
    {
        private readonly UpperBodyRootState root;
        internal NormalState(HierarchicalStateMachine machine, UpperBodyRootState parent)
            : base(machine, parent) { root = parent; }
        protected override State GetTransition() => root.ReloadPressed ? (State)root.Reload
            : root.Attack.CanEnter ? root.Attack : root.AimHeld ? root.Aim : null;
        protected override void OnUpdate(float deltaTime) => root.TickReady(false);
    }

    internal sealed class AimState : State
    {
        private readonly UpperBodyRootState root;
        internal AimState(HierarchicalStateMachine machine, UpperBodyRootState parent)
            : base(machine, parent) { root = parent; }
        protected override State GetTransition() => root.ReloadPressed ? (State)root.Reload
            : root.Attack.CanEnter ? root.Attack : !root.AimHeld ? root.Normal : null;
        protected override void OnUpdate(float deltaTime) => root.TickReady(true);
    }

    internal sealed class ReloadState : State
    {
        private readonly UpperBodyRootState root;
        internal ReloadState(HierarchicalStateMachine machine, UpperBodyRootState parent)
            : base(machine, parent) { root = parent; }
        protected override void OnEnter()
        {
            root.BeginReload();
        }
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
