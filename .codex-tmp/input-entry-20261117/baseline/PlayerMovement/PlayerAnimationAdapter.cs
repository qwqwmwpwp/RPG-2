using RPG4.CharacterAnimation;
using RPG4.Input;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Business/network/local anticipation is resolved here, before entering the independent animation module.</summary>
    internal sealed class PlayerAnimationAdapter
    {
        private readonly Player owner;
        private readonly PlayerInput input;
        private bool anticipationOpen, hasEndedAnticipation, wasReloading;
        private ulong anticipatedWeapon, highestAnticipatedSequence, endedWeapon, endedSequence;
        private ulong reloadSequence, reloadWeapon;
        private uint epoch;
        internal UpperBodyDemand Demand { get; private set; }
        internal PlayerAnimationAdapter(Player owner, PlayerInput input) { this.owner = owner; this.input = input; }
        private bool Suppressed(ulong weapon, ulong sequence) => anticipatedWeapon == weapon && sequence < highestAnticipatedSequence
            || hasEndedAnticipation && endedWeapon == weapon && endedSequence == sequence;

        internal CharacterAnimationState Build(PlayerPublishedFrame frame, Vector3 velocity, float bodyYaw,
            Vector2 view, float renderYawOffset, CharacterEquipmentPresentation equipment)
        {
            bool allowed = frame.Active && frame.Alive && frame.CanUseWeapon && !frame.Consumable.Active;
            var local = owner != null ? owner.GetLocalWeaponPresentation() : default;
            bool anticipated = allowed && local.Active;
            UpperBodyDemand demand = anticipated ? local.Demand : frame.WeaponDemand;
            var action = anticipated ? local.Action : frame.WeaponAction;
            bool aiming = anticipated ? local.IsAiming : frame.IsAiming;
            bool reloading = anticipated ? local.Reloading : frame.Reloading;
            ulong mappedSequence = 0;
            bool mapped = owner != null && owner.TryGetAnticipatedPresentationSequence(frame.WeaponId, frame.ShotSequence, out mappedSequence);
            ulong weapon = anticipated ? local.WeaponId : frame.WeaponId;
            ulong sequence = anticipated ? local.ShotSequence : mapped ? mappedSequence : frame.ShotSequence;
            bool localIdentity = anticipated || mapped;
            if (anticipationOpen && (reloading || !action.ShotActive && !action.FadingOut))
            {
                endedWeapon = anticipatedWeapon; endedSequence = highestAnticipatedSequence;
                hasEndedAnticipation = true; anticipationOpen = false;
            }
            bool suppress = anticipated ? Suppressed(weapon, sequence)
                : owner != null && owner.ShouldDeferConfirmedShotPresentation(frame.WeaponId, frame.ShotSequence)
                    || mapped && Suppressed(weapon, sequence);
            if (suppress)
            {
                action = default;
                demand = new UpperBodyDemand(demand.AimCamera || reloading, demand.AimCamera,
                    false, reloading, demand.MoveSpeed, demand.AnimationSpeed);
            }
            if (allowed && localIdentity && action.ShotActive)
            {
                anticipatedWeapon = weapon; highestAnticipatedSequence = sequence; anticipationOpen = true;
            }
            Demand = demand;
            float shotDuration = anticipated ? local.ShotDuration : frame.ShotDuration;
            float fade = anticipated ? local.FadeDuration : frame.FadeDuration;
            float shotFadePhase = anticipated ? local.ShotFadePhase : action.FadingOut
                ? Mathf.Clamp01((float)((frame.Time - frame.ShotTime - frame.ShotDuration) / System.Math.Max(0.01d, frame.FadeDuration))) : 0f;
            float reloadDuration = anticipated ? local.ReloadDuration : frame.ReloadDuration;
            float reloadPhase = anticipated ? local.ReloadPhase : frame.ReloadPhase;
            if (reloading && (!wasReloading || reloadWeapon != weapon)) { reloadSequence++; reloadWeapon = weapon; }
            wasReloading = reloading;
            var consumable = frame.Consumable;
            float useDuration = Mathf.Max(0.01f, (float)(consumable.EndTime - consumable.StartTime));
            float usePhase = Mathf.Clamp01((float)((frame.Time - consumable.StartTime) / useDuration));
            bool hurt = frame.HurtUntil > frame.Time;
            float hurtDuration = owner != null ? owner.HurtPresentationDuration : 0.45f;
            Vector3 gripPosition = Vector3.zero; Quaternion gripRotation = Quaternion.identity;
            bool hasGrip = equipment != null && equipment.TryGetGripPose(out gripPosition, out gripRotation);
            return new CharacterAnimationState
            {
                Time = frame.Time, Active = frame.Active, Alive = frame.Alive, CanMove = frame.CanMove,
                CanLook = frame.CanLook, CanUseWeapon = allowed, Crouched = frame.PhysicallyCrouched,
                Strafe = frame.Consumable.Active || allowed && demand.Strafe, Aiming = aiming,
                HasMoveInput = frame.Plan.Move.sqrMagnitude > 0f, ImmediateView = input != null && input.UsesLocalInput,
                PlanarVelocity = velocity, BodyYaw = bodyYaw, ViewYaw = view.x, ViewPitch = view.y, RenderYawOffset = renderYawOffset,
                HasEquipment = equipment != null && equipment.View != null, HasGripPose = hasGrip,
                GripCarrierLocalPosition = gripPosition, GripCarrierLocalRotation = gripRotation,
                EquipmentIdentity = equipment != null ? equipment.Identity : 0,
                EquipmentVersion = equipment != null ? equipment.Version : 0,
                Shot = new CharacterActionState(new CharacterActionIdentity(weapon, sequence, localIdentity ? epoch + 1u : epoch),
                    action.ShotActive, anticipated ? frame.Time - action.ShotPhase * shotDuration : frame.ShotTime,
                    shotDuration, action.ShotPhase, action.FadingOut, action.IsAutomatic, fade, shotFadePhase),
                Reload = new CharacterActionState(new CharacterActionIdentity(weapon, reloadSequence, epoch),
                    reloading, frame.Time - reloadPhase * reloadDuration, reloadDuration, reloadPhase),
                Consumable = new CharacterActionState(new CharacterActionIdentity(0, consumable.ActionId), consumable.Active,
                    consumable.StartTime, useDuration, usePhase),
                Hurt = new CharacterActionState(new CharacterActionIdentity(0, unchecked((ulong)System.BitConverter.DoubleToInt64Bits(frame.HurtUntil))),
                    hurt, frame.HurtUntil - hurtDuration, hurtDuration,
                    hurtDuration > 0f ? Mathf.Clamp01(1f - (float)(frame.HurtUntil - frame.Time) / hurtDuration) : 1f)
            };
        }
        internal void Reset()
        {
            anticipationOpen = hasEndedAnticipation = wasReloading = false;
            anticipatedWeapon = highestAnticipatedSequence = endedWeapon = endedSequence = 0;
            epoch += 2;
        }
    }
}
