using UnityEngine;

namespace RPG4.PlayerIntent
{
    public enum PlayerInputSource { Local, External }

    /// <summary>设备无关意图。Look 是角度增量；可选 ViewAngles 是绝对 yaw/pitch。
    /// 外部 pressed、Look、Zoom 在下一角色逻辑周期 消费一次；移动及 held 保留到下次提交或清理。
    /// 消费前多次提交将同类 pressed 合并为一次、累加 Look/Zoom，移动及 held 取最新；新绝对角替换旧角基准及此前 Look 增量。</summary>
    public readonly struct PlayerInputFrame
    {
        public readonly Vector2 Move, Look;
        public readonly float Zoom;
        public readonly bool CrouchHeld, AimHeld, FirePressed, FireHeld, ReloadPressed, InteractPressed;
        public readonly Vector2? ViewAngles;
        public readonly bool QuickUsePressed;
        public PlayerInputFrame(Vector2 move = default, Vector2 look = default, float zoom = 0f,
            bool crouchHeld = false, bool aimHeld = false, bool firePressed = false, bool fireHeld = false,
            bool reloadPressed = false, bool interactPressed = false, Vector2? viewAngles = null, bool quickUsePressed = false)
        {
            Move = move; Look = look; Zoom = zoom; CrouchHeld = crouchHeld; AimHeld = aimHeld;
            FirePressed = firePressed; FireHeld = fireHeld; ReloadPressed = reloadPressed;
            InteractPressed = interactPressed; ViewAngles = viewAngles; QuickUsePressed = quickUsePressed;
        }
    }

    /// <summary>按职责访问角色周期冻结的已提交状态；各系统只写下一周期数据。</summary>
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
        PlayerInputFrame Frame { get; }
        ulong Version { get; }
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
