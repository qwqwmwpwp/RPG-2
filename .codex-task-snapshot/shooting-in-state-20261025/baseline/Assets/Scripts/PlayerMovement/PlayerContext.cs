using RPG4.Input;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    internal enum PlayerMode { Exploration, Aim, HipFire }

    /// <summary>每个角色独立的运行时引用集合；输入和配置由各自的数据源维护。</summary>
    internal sealed class PlayerContext
    {
        internal PlayerInput Input { get; }
        internal PlayerContentData Content { get; }
        internal ExplorationState.Data Exploration { get; }
        internal AimState.Data Aim { get; }
        internal HipFireState.Data HipFire { get; }
        internal PlayerMode Mode { get; private set; }
        private readonly PlayerFireAction fireAction;

        internal bool AnimatorIsAvailable => Content.Animator != null
            && Content.Animator.runtimeAnimatorController != null && Content.Animator.isActiveAndEnabled;

        internal bool CanControl => Input != null && Input.CanReadGameplayInput
            && Content.Body != null && !Content.Body.isKinematic
            && Content.CameraController != null && Content.CameraController.isActiveAndEnabled;

        internal bool CanUseWeapon => CanControl && Cursor.lockState == CursorLockMode.Locked;

        internal bool ShotPoseActive => Mode != PlayerMode.Exploration && fireAction.HasShotAction
            && CanUseWeapon && Time.deltaTime > 0f;

        internal PlayerContext(PlayerInput input, PlayerContentData content,
            ExplorationState.Data exploration, AimState.Data aim, HipFireState.Data hipFire,
            PlayerFireAction fireAction)
        {
            Input = input;
            Content = content;
            Exploration = exploration;
            Aim = aim;
            HipFire = hipFire;
            this.fireAction = fireAction;
        }

        internal void ResolveMode(bool canUseWeapon, bool aimHeld)
        {
            Mode = !canUseWeapon ? PlayerMode.Exploration : aimHeld ? PlayerMode.Aim
                : fireAction.HasWeaponAction ? PlayerMode.HipFire : PlayerMode.Exploration;
        }

        internal void SetPlanarVelocity(Vector3 velocity)
        {
            Rigidbody body = Content.Body;
            if (body == null || body.isKinematic) return;
            body.velocity = new Vector3(velocity.x, body.velocity.y, velocity.z);
            body.angularVelocity = Vector3.zero;
        }

        internal void SetWeaponMode(bool aiming, bool shotPose)
        {
            if (Content.CameraController != null) Content.CameraController.SetAiming(aiming);
            if (Content.AimPose != null) Content.AimPose.SetAiming(shotPose);
            // 禁用期间也清理旧层权重；恢复求值后不能沿用上次射击表现。
            Animator animator = Content.Animator;
            if (animator != null && animator.runtimeAnimatorController != null && animator.layerCount > 1)
                animator.SetLayerWeight(1, shotPose ? 1f : 0f);
        }
    }
}
