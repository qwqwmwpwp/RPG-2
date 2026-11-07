using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    // 这些目标由桥接层生成，不回写核心黑板，也不携带逻辑对象引用。
    internal readonly struct PlayerMotionTarget
    {
        internal readonly LocomotionPlan Plan;
        internal readonly bool CanMove, IsAiming;
        internal readonly Vector3 PlanarVelocity;
        internal readonly float BodyYaw;
        internal PlayerMotionTarget(LocomotionPlan plan, bool canMove, bool aiming, Vector3 velocity, float bodyYaw)
        {
            Plan = plan; CanMove = canMove; IsAiming = aiming; PlanarVelocity = velocity; BodyYaw = bodyYaw;
        }
    }

    internal readonly struct PlayerWeaponTarget
    {
        internal readonly ulong WeaponId, ShotSequence;
        internal readonly WeaponActionSnapshot WeaponAction;
        internal readonly bool IsAiming, Reloading, Crouched, AnticipatedIdentity;
        internal readonly float ShotDuration, FadeDuration, ReloadDuration, ReloadPhase, LayerWeight;
        internal PlayerWeaponTarget(PlayerPublishedFrame frame, float layerWeight,
            bool anticipatedIdentity = false, ulong presentationSequence = 0, bool suppressShot = false)
        {
            WeaponId = frame.WeaponId; ShotSequence = anticipatedIdentity ? presentationSequence : frame.ShotSequence;
            WeaponAction = suppressShot ? default : frame.WeaponAction; AnticipatedIdentity = anticipatedIdentity;
            IsAiming = frame.IsAiming; Reloading = frame.Reloading; Crouched = frame.Plan.Crouched;
            ShotDuration = frame.ShotDuration; FadeDuration = frame.FadeDuration;
            ReloadDuration = frame.ReloadDuration; ReloadPhase = frame.ReloadPhase; LayerWeight = layerWeight;
        }
        internal PlayerWeaponTarget(PlayerPublishedFrame frame, float layerWeight, PlayerLocalWeaponPresentation local,
            bool suppressShot = false)
        {
            WeaponId = local.WeaponId; ShotSequence = local.ShotSequence;
            WeaponAction = suppressShot ? default : local.Action;
            AnticipatedIdentity = true;
            IsAiming = local.IsAiming; Reloading = local.Reloading; Crouched = frame.Plan.Crouched;
            ShotDuration = local.ShotDuration; FadeDuration = local.FadeDuration;
            ReloadDuration = local.ReloadDuration; ReloadPhase = local.ReloadPhase; LayerWeight = layerWeight;
        }
    }

    internal readonly struct PlayerPoseTarget
    {
        internal readonly bool Enabled, FollowView, Reloading, ImmediateView;
        internal readonly float ViewYaw, Pitch, YawOffset, WaistPitchOffset, SupportHandWeight, LayerWeight;
        internal PlayerPoseTarget(bool enabled, bool followView, bool reloading, float yaw, float pitch,
            float yawOffset, float waistPitchOffset, float supportHandWeight, float layerWeight, bool immediateView = false)
        {
            Enabled = enabled; FollowView = followView; Reloading = reloading; ViewYaw = yaw; Pitch = pitch;
            YawOffset = yawOffset; WaistPitchOffset = waistPitchOffset;
            SupportHandWeight = supportHandWeight; LayerWeight = layerWeight;
            ImmediateView = immediateView;
        }
    }

    internal readonly struct PlayerCameraTarget
    {
        internal readonly bool Attacking;
        internal readonly float Distance, FieldOfView, ExplorationDistance;
        internal PlayerCameraTarget(bool attacking, float distance, float fieldOfView, float explorationDistance)
        {
            Attacking = attacking; Distance = distance; FieldOfView = fieldOfView;
            ExplorationDistance = explorationDistance;
        }
    }
}
