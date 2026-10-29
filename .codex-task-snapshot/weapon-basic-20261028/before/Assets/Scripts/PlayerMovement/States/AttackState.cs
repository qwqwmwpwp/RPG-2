using System;
using RPG4.PlayerIntent;
using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>腰射/瞄准射击共享的动作、冷却与末发收尾；子状态切换保留全部时序。</summary>
    internal sealed class AttackState : State
    {
        private readonly UpperBodyRootState root;
        private readonly PlayerContext context;
        private double nextShotTime;
        private double lastShotTime;
        private double actionEndTime;
        private float currentShotDuration;
        private bool fireHeld;
        private bool shotActive;
        private bool fadingOut;
        private bool hasShotInHold;
        private bool automatic;
        private bool fireArmed = true;
        private int fireRearmFrame = -1;
        internal WeaponActionSnapshot Frame { get; private set; }
        internal HipAttackState Hip { get; }
        internal AimAttackState Aim { get; }

        private bool InputAvailable => root.CanAct && fireArmed && Time.frameCount > fireRearmFrame;
        private bool Held => InputAvailable && context.Blackboard.FireHeld;
        private bool CanStartShot => InputAvailable
            && (context.Blackboard.FirePressed || context.Blackboard.FireHeld) && root.Now >= nextShotTime;
        // A held input can wait inside Attack for cooldown; a short cooldown tap is not queued.
        internal bool CanEnter => Held || CanStartShot;

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
            if (!root.CanAct) return root.AimHeld ? (State)root.Aim : root.Normal;
            // Transitions run before Update. Leave a fresh eligible press for this
            // parent's Update, even when it lands exactly on the fade's final frame.
            bool finished = !shotActive && (!fadingOut || root.Now >= actionEndTime);
            if (finished && !Held && !CanStartShot)
                return root.AimHeld ? (State)root.Aim : root.Normal;
            return null;
        }

        protected override void OnUpdate(float deltaTime)
        {
            if (deltaTime <= 0f || !root.CanAct)
            {
                Frame = new WeaponActionSnapshot(false, false, shotActive, fadingOut,
                    automatic, Frame.ShotPhase);
                return;
            }
            bool pressed = InputAvailable && context.Blackboard.FirePressed;
            bool held = Held;
            if (pressed || !held) hasShotInHold = false;
            fireHeld = held;
            bool shotStarted = (pressed || held) && root.Now >= nextShotTime;
            bool fadeStarted = false;
            if (shotStarted)
            {
                automatic = held && hasShotInHold;
                hasShotInHold = held;
                lastShotTime = root.Now;
                currentShotDuration = Math.Max(0.01f, context.Data.Attack.ShotAnimationDuration);
                nextShotTime = root.Now + Math.Max(currentShotDuration, context.Data.Attack.ShotInterval);
                actionEndTime = root.Now + currentShotDuration;
                shotActive = true;
                fadingOut = false;
            }
            else if (shotActive && root.Now >= actionEndTime)
            {
                shotActive = false;
                fadingOut = true;
                fadeStarted = true;
                actionEndTime = root.Now + Math.Max(0f, context.Data.Attack.LayerFadeOutDuration);
            }
            else if (fadingOut && root.Now >= actionEndTime)
            {
                fadingOut = false;
            }
            if (!shotActive && !fadingOut) automatic = false;
            float phase = shotActive
                ? (float)Math.Min(1d, Math.Max(0d, (root.Now - lastShotTime) / currentShotDuration)) : 0f;
            Frame = new WeaponActionSnapshot(shotStarted, fadeStarted, shotActive, fadingOut, automatic, phase);
        }

        internal void Present(bool aiming)
        {
            bool aim = root.CanAct && aiming;
            bool hasWeapon = root.CanAct && (fireHeld || shotActive || fadingOut);
            float speed = aim ? context.Data.Aim.MoveSpeed
                : hasWeapon ? context.Data.HipFire.MoveSpeed : context.Data.Exploration.MoveSpeed;
            context.Intents.RecordWeapon(new UpperBodyDemand(aim || hasWeapon, aim,
                root.CanAct && (shotActive || fadingOut), false, speed, !aim && hasWeapon ? 1.35f : 1f,
                attackCamera: hasWeapon), Frame, 0f);
        }

        internal void ObserveRelease()
        {
            if (Time.frameCount > fireRearmFrame && !context.Blackboard.FireHeld) fireArmed = true;
        }

        internal void RequireReleaseAfterReload()
        {
            fireArmed = false;
            fireRearmFrame = Time.frameCount;
        }

        internal void Cancel()
        {
            ClearAction();
            fireArmed = false;
        }

        private void ClearAction()
        {
            fireHeld = shotActive = fadingOut = hasShotInHold = automatic = false;
            lastShotTime = actionEndTime = 0d;
            currentShotDuration = 0f;
            Frame = default;
            // nextShotTime survives every exit and interruption.
        }

        protected override void OnExit() => ClearAction();
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
