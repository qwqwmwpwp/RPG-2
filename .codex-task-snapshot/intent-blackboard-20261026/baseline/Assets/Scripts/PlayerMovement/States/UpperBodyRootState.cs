using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>共享开火节拍/表现；各叶状态负责转换、动作与完整运动需求。</summary>
    internal sealed class UpperBodyRootState : State
    {
        private readonly PlayerContext context;
        private readonly PlayerFireAction fireAction = new PlayerFireAction();
        private readonly PlayerWeaponPresentation presentation;
        private PlayerFireAction.Frame fireFrame;
        private bool fireArmed = true;
        private int fireRearmFrame = -1;
        private bool frameAvailable;
        private float reloadPhase;
        internal double Now { get; private set; }
        internal bool CanAct => frameAvailable && context.CanUseWeapon;
        internal bool AimHeld => CanAct && context.Input.AimHeld;
        internal bool ReloadPressed => CanAct && context.Input.ReloadPressed;
        internal float ReloadDuration => context.Data.Reload.Duration;
        internal NormalState Normal { get; }
        internal AimState Aim { get; }
        internal ReloadState Reload { get; }
        internal UpperBodyRootState(HierarchicalStateMachine machine, PlayerContext context) : base(machine)
        {
            this.context = context;
            presentation = new PlayerWeaponPresentation(context);
            Normal = new NormalState(machine, this);
            Aim = new AimState(machine, this);
            Reload = new ReloadState(machine, this);
        }
        protected override State GetInitialState() => ReloadPressed ? (State)Reload : AimHeld ? Aim : Normal;

        // 准备时间与硬中断；不消费射击，也不维护第二套活动模式。
        internal void PrepareFrame(float deltaTime, double now)
        {
            Now = now;
            frameAvailable = deltaTime > 0f && context.CanUseWeapon;
            if (!frameAvailable) CancelWeaponAction();
        }

        internal void TickWeapon(bool aiming)
        {
            // 完成帧连快速按下再松开也不消费；下一帧起先观察释放再重新武装。
            if (CanAct && Time.frameCount > fireRearmFrame)
            {
                bool held = context.Input.FireHeld;
                if (!held) fireArmed = true;
                AttackData data = context.Data.Attack;
                fireFrame = fireAction.Tick(fireArmed && context.Input.FirePressed, fireArmed && held,
                    Now, data.ShotInterval, data.ShotAnimationDuration, data.LayerFadeOutDuration);
            }
            bool aim = CanAct && aiming;
            bool hasWeapon = CanAct && fireAction.HasWeaponAction;
            float speed = aim ? context.Data.Aim.MoveSpeed
                : hasWeapon ? context.Data.HipFire.MoveSpeed : context.Data.Exploration.MoveSpeed;
            context.WeaponDemand = new UpperBodyDemand(aim || hasWeapon, aim,
                CanAct && fireAction.HasShotAction, false, speed, !aim && hasWeapon ? 1.35f : 1f);
        }

        internal void BeginReload()
        {
            fireAction.Cancel();
            fireFrame = default;
            presentation.Cancel();
            fireArmed = false;
            reloadPhase = 0f;
        }
        internal void TickReload(float phase)
        {
            reloadPhase = phase;
            context.WeaponDemand = new UpperBodyDemand(true, false, false, true,
                context.Data.Reload.MoveSpeed, 1f);
        }
        internal void EndReload()
        {
            fireArmed = false;
            fireRearmFrame = Time.frameCount;
            reloadPhase = 0f;
            presentation.Cancel();
            // 换弹素材尾事件可能被完成/中断/站蹲切换跳过，明确恢复右手挂点。
            Animator animator = context.Content.Animator;
            if (animator != null)
                animator.gameObject.SendMessage("SwitchSocketByString", "To_Hand_R_Socket",
                    SendMessageOptions.DontRequireReceiver);
        }
        internal void SyncPresentation()
        {
            UpperBodyDemand demand = context.WeaponDemand;
            context.SetWeaponMode(demand.AimCamera, context.ShotPoseActive, demand.Reloading);
            if (demand.Reloading) presentation.Reload(context.Plan.Crouched, reloadPhase);
            else presentation.Fire(fireFrame, demand.AimCamera);
        }
        internal void CancelWeaponAction()
        {
            frameAvailable = false;
            fireAction.Cancel();
            fireFrame = default;
            fireArmed = false;
            presentation.Cancel();
            context.WeaponDemand = new UpperBodyDemand(false, false, false, false,
                context.Data.Exploration.MoveSpeed, 1f);
            context.SetWeaponMode(false, false, false);
            if (context.Input != null) context.Input.ClearWeaponInput();
        }
        protected override void OnExit() => CancelWeaponAction();
    }

    internal sealed class NormalState : State
    {
        private readonly UpperBodyRootState root;
        internal NormalState(HierarchicalStateMachine machine, UpperBodyRootState parent)
            : base(machine, parent) { root = parent; }
        protected override State GetTransition() => root.ReloadPressed ? (State)root.Reload
            : root.AimHeld ? root.Aim : null;
        protected override void OnUpdate(float deltaTime) => root.TickWeapon(false);
    }

    internal sealed class AimState : State
    {
        private readonly UpperBodyRootState root;
        internal AimState(HierarchicalStateMachine machine, UpperBodyRootState parent)
            : base(machine, parent) { root = parent; }
        protected override State GetTransition() => root.ReloadPressed ? (State)root.Reload
            : !root.AimHeld ? root.Normal : null;
        protected override void OnUpdate(float deltaTime) => root.TickWeapon(true);
    }

    internal sealed class ReloadState : State
    {
        private readonly UpperBodyRootState root;
        private double startedAt;
        private float duration;
        internal ReloadState(HierarchicalStateMachine machine, UpperBodyRootState parent)
            : base(machine, parent) { root = parent; }
        protected override void OnEnter()
        {
            startedAt = root.Now;
            duration = root.ReloadDuration;
            root.BeginReload();
        }
        protected override State GetTransition()
        {
            if (!root.CanAct) return root.Normal;
            if (root.Now < startedAt + duration) return null;
            return root.AimHeld ? (State)root.Aim : root.Normal;
        }
        protected override void OnUpdate(float deltaTime) => root.TickReload(
            Mathf.Clamp01((float)((root.Now - startedAt) / duration)));
        protected override void OnExit() => root.EndReload();
    }
}
