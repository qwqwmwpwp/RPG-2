using UnityEngine;

namespace RPG4.PlayerIntent
{
    /// <summary>按职责提供同一份黑板的只读视图，不复制数据或驱动更新。</summary>
    public interface IPlayerReadState
    {
        IPlayerInputState Input { get; }
        IPlayerLocomotionState Locomotion { get; }
        IPlayerViewState View { get; }
        IPlayerWeaponState Weapon { get; }
        IPlayerPoseState Pose { get; }
        IPlayerInteractionState Interaction { get; }
    }

    public interface IPlayerInputState
    {
        bool InputAllowed { get; }
        Vector2 Move { get; }
        Vector2 Look { get; }
        float Zoom { get; }
        bool CrouchHeld { get; }
        bool AimHeld { get; }
        bool FirePressed { get; }
        bool FireHeld { get; }
        bool ReloadPressed { get; }
    }

    public interface IPlayerLocomotionState
    {
        bool CanMove { get; }
        bool PhysicallyCrouched { get; }
        bool CanStand { get; }
        LocomotionPlan Plan { get; }
    }

    public interface IPlayerViewState
    {
        bool CanLook { get; }
        float ViewYaw { get; }
        float ViewPitch { get; }
    }

    public interface IPlayerWeaponState
    {
        bool CanUseWeapon { get; }
        UpperBodyDemand WeaponDemand { get; }
        WeaponActionSnapshot WeaponAction { get; }
        float ReloadPhase { get; }
        bool IsAiming { get; }
        bool Reloading { get; }
        bool WeaponCancellationRequested { get; }
    }

    public interface IPlayerPoseState
    {
        bool CanLook { get; }
        float ViewYaw { get; }
        bool IsAiming { get; }
        bool Reloading { get; }
    }

    public interface IPlayerInteractionState
    {
        bool CanInteract { get; }
        bool InteractPressed { get; }
    }
}
