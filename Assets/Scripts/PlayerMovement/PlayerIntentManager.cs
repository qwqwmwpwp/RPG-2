using RPG4.Input;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>每角色唯一管理器；由现有宿主明确调用阶段，只组织三个处理器。</summary>
    internal sealed class PlayerIntentManager
    {
        private readonly Player owner;
        private readonly PlayerInput source;
        private readonly PlayerContext context;
        private readonly InputIntentProcessor input;
        private readonly LocomotionIntentProcessor locomotion;
        private readonly PlayerIntentProcessor decisions;
        private readonly PlayerBlackboard blackboard;
        internal IPlayerReadState ReadState => context.ReadState;
        internal PlayerBlackboard Blackboard => blackboard;
        internal PlayerIntentManager(Player owner, PlayerInput source, PlayerContext context)
        {
            this.owner = owner;
            this.source = source;
            this.context = context;
            input = source.IntentProcessor;
            blackboard = input.Blackboard;
            locomotion = new LocomotionIntentProcessor(context, blackboard);
            decisions = new PlayerIntentProcessor(context, blackboard);
        }
        internal void Bind() => source.BindIntentManager(RefreshPermissions);
        internal void Dispose()
        {
            if (source != null) source.UnbindIntentManager(RefreshPermissions);
        }
        internal void RefreshPermissions()
        {
            PlayerContentData content = context.Content;
            blackboard.IsAlive = owner != null && owner.IsAlive;
            blackboard.IsHurt = owner != null && owner.IsHurt;
            input.SetControlAvailability(owner != null && owner.isActiveAndEnabled,
                content.Body != null && !content.Body.isKinematic,
                true,
                blackboard.IsAlive, !blackboard.IsHurt);
            locomotion.RefreshAvailability();
            decisions.Refresh();
        }
        internal void ResolveLocomotion(bool crouched, bool canStand) => locomotion.Resolve(crouched, canStand);
        internal void RecordPosture(bool crouched, bool canStand) => locomotion.RecordPosture(crouched, canStand);
        internal void RecordView(float yaw, float pitch) => decisions.RecordView(yaw, pitch);
        internal void ProcessLook(Vector2 sensitivity, Vector2 pitchRange, float aimMultiplier)
            => decisions.ProcessLook(sensitivity, pitchRange, aimMultiplier);
        internal void ApplyRecoil(float pitch, float yaw, Vector2 pitchRange)
            => decisions.ApplyRecoil(pitch, yaw, pitchRange);
        internal void RecordWeapon(UpperBodyDemand demand, WeaponActionSnapshot frame, float reloadPhase)
            => decisions.RecordWeapon(demand, frame, reloadPhase);
        internal void ClearWeaponInput() => input.ClearWeaponInput();
        internal void AcknowledgeWeaponCancellation() => decisions.AcknowledgeWeaponCancellation();
        internal void RecordEvent(PlayerEventKind kind, ulong weaponId = 0, ulong shotSequence = 0, float amount = 0f,
            Vector3 direction = default, bool success = false, bool obstructed = false)
            => blackboard.RecordEvent(kind, Time.timeAsDouble, weaponId, shotSequence, amount,
                success: success, direction: direction, obstructed: obstructed);
        internal void Publish(long tick)
        {
            blackboard.OwnerActive = owner != null && owner.SimulationActive;
            blackboard.HurtUntil = owner != null ? owner.HurtUntil : 0d;
            blackboard.CurrentHealth = owner != null && owner.Attributes != null ? owner.Attributes.CurrentHealth : 0f;
            blackboard.MaxHealth = owner != null && owner.Attributes != null ? owner.Attributes.MaxHealth : 0f;
            blackboard.PlanarVelocity = context.World.PlanarVelocity;
            blackboard.BodyYaw = context.World.BodyYaw;
            var weapon = context.Weapon;
            blackboard.WeaponId = weapon != null ? weapon.ItemId.Value : 0;
            blackboard.ShotSequence = weapon != null ? weapon.LastShot.Sequence : 0;
            blackboard.ShotTime = weapon != null ? weapon.LastShot.Time : 0d;
            blackboard.ShotDuration = weapon != null ? weapon.Definition.ShotDuration : 0.01f;
            blackboard.FadeDuration = weapon != null ? weapon.Definition.FadeOutDuration : 0f;
            blackboard.ReloadDuration = weapon != null ? weapon.Definition.ReloadDuration : 1f;
            blackboard.Publish(tick, Time.timeAsDouble);
        }
    }
}
