using RPG4.Input;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>共享引用与单向数据：上身需求 → resolver → 下身计划。</summary>
    internal sealed class PlayerContext
    {
        internal PlayerInput Input { get; }
        internal PlayerStateData Data { get; }
        internal PlayerContentData Content => Data.Content;
        internal UpperBodyDemand WeaponDemand { get; set; }
        internal LocomotionPlan Plan { get; set; }
        internal bool AnimatorIsAvailable => Content.Animator != null
            && Content.Animator.runtimeAnimatorController != null && Content.Animator.isActiveAndEnabled;
        internal bool CanControl => Input != null && Input.CanReadGameplayInput
            && Content.Body != null && !Content.Body.isKinematic
            && Content.ViewController != null && Content.ViewController.isActiveAndEnabled;
        internal bool CanUseWeapon => CanControl && Cursor.lockState == CursorLockMode.Locked;
        internal bool ShotPoseActive => WeaponDemand.ShotPose && CanUseWeapon && Time.deltaTime > 0f;
        internal PlayerContext(PlayerInput input, PlayerStateData data)
        {
            Input = input;
            Data = data;
        }
        internal void SetWeaponMode(bool aiming, bool shotPose, bool reload)
        {
            if (Content.ViewController != null) Content.ViewController.SetMode(WeaponDemand.Strafe, aiming);
            if (Content.AimPose != null) Content.AimPose.SetAiming(shotPose);
            Animator animator = Content.Animator;
            if (animator != null && animator.runtimeAnimatorController != null && animator.layerCount > 1)
                animator.SetLayerWeight(1, shotPose || reload ? 1f : 0f);
        }
    }
}
