using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("RPG4.Input")]
[assembly: InternalsVisibleTo("RPG4.PlayerMovement")]

namespace RPG4.PlayerIntent
{
    /// <summary>每角色唯一共享快照；只存值，不采样、校验、推进或复位。</summary>
    public sealed class PlayerBlackboard
    {
        public bool InputAllowed { get; internal set; }
        public bool CanMove { get; internal set; }
        public bool CanLook { get; internal set; }
        public bool CanUseWeapon { get; internal set; }
        public bool CanInteract { get; internal set; }
        public Vector2 Move { get; internal set; }
        public Vector2 Look { get; internal set; }
        public float Zoom { get; internal set; }
        public bool CrouchHeld { get; internal set; }
        public bool AimHeld { get; internal set; }
        public bool FirePressed { get; internal set; }
        public bool FireHeld { get; internal set; }
        public bool ReloadPressed { get; internal set; }
        public bool InteractPressed { get; internal set; }
        public float ViewYaw { get; internal set; }
        public float ViewPitch { get; internal set; }
        public float AimPosePitch { get; internal set; }
        public float CameraExplorationDistance { get; internal set; }
        public float CameraTargetDistance { get; internal set; }
        public float CameraTargetFieldOfView { get; internal set; }
        public bool UseAttackCamera { get; internal set; }
        public bool PhysicallyCrouched { get; internal set; }
        public bool CanStand { get; internal set; }
        public UpperBodyDemand WeaponDemand { get; internal set; }
        public LocomotionPlan Plan { get; internal set; }
        public WeaponActionSnapshot WeaponAction { get; internal set; }
        public float ReloadPhase { get; internal set; }
        public bool FollowView { get; internal set; }
        public bool IsAiming { get; internal set; }
        public bool AimPoseEnabled { get; internal set; }
        public float SupportHandWeight { get; internal set; }
        public bool WeaponIKEnabled { get; internal set; }
        public bool Reloading { get; internal set; }
        public bool WeaponCancellationRequested { get; internal set; }
        public float UpperBodyLayerWeight { get; internal set; }
        public MonoBehaviour InteractionTarget { get; internal set; }
    }

    public readonly struct UpperBodyDemand
    {
        public readonly bool Strafe, AimCamera, ShotPose, Reloading, AttackCamera;
        public readonly float MoveSpeed, AnimationSpeed;
        public UpperBodyDemand(bool strafe, bool aimCamera, bool shotPose, bool reloading,
            float moveSpeed, float animationSpeed, bool attackCamera = false)
        {
            Strafe = strafe; AimCamera = aimCamera; ShotPose = shotPose; Reloading = reloading;
            AttackCamera = attackCamera;
            MoveSpeed = moveSpeed; AnimationSpeed = animationSpeed;
        }
    }

    public readonly struct LocomotionPlan
    {
        public readonly bool CanMove, Crouched, Strafe;
        public readonly Vector2 Move;
        public readonly float ViewYaw, MoveSpeed, AnimationSpeed;
        public LocomotionPlan(bool canMove, bool crouched, bool strafe, Vector2 move,
            float viewYaw, float moveSpeed, float animationSpeed)
        {
            CanMove = canMove; Crouched = crouched; Strafe = strafe; Move = move;
            ViewYaw = viewYaw; MoveSpeed = moveSpeed; AnimationSpeed = animationSpeed;
        }
    }

    public readonly struct WeaponActionSnapshot
    {
        public readonly bool ShotStarted, FadeStarted, ShotActive, FadingOut, IsAutomatic;
        public readonly float ShotPhase;
        public WeaponActionSnapshot(bool shotStarted, bool fadeStarted, bool shotActive,
            bool fadingOut, bool isAutomatic, float shotPhase)
        {
            ShotStarted = shotStarted; FadeStarted = fadeStarted; ShotActive = shotActive;
            FadingOut = fadingOut; IsAutomatic = isAutomatic; ShotPhase = shotPhase;
        }
    }
}
