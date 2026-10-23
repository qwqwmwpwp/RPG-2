using RPG4.Input;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>每个角色独立的运行时引用集合；输入和配置由各自的数据源维护。</summary>
    internal sealed class PlayerContext
    {
        internal PlayerInput Input { get; }
        internal PlayerContentData Content { get; }
        internal ExplorationState.Data Exploration { get; }
        internal AimState.Data Aim { get; }

        internal bool AnimatorIsAvailable => Content.Animator != null
            && Content.Animator.runtimeAnimatorController != null && Content.Animator.isActiveAndEnabled;

        internal bool CanControl => Input != null && Input.CanReadGameplayInput
            && Content.Body != null && !Content.Body.isKinematic
            && Content.CameraController != null && Content.CameraController.isActiveAndEnabled;

        internal bool WantsAim => CanControl && AnimatorIsAvailable && Input.AimHeld;

        internal PlayerContext(PlayerInput input, PlayerContentData content,
            ExplorationState.Data exploration, AimState.Data aim)
        {
            Input = input;
            Content = content;
            Exploration = exploration;
            Aim = aim;
        }

        internal void SetPlanarVelocity(Vector3 velocity)
        {
            Rigidbody body = Content.Body;
            if (body == null || body.isKinematic) return;
            body.velocity = new Vector3(velocity.x, body.velocity.y, velocity.z);
            body.angularVelocity = Vector3.zero;
        }

        internal void SetAiming(bool aiming)
        {
            if (Content.CameraController != null) Content.CameraController.SetAiming(aiming);
            if (Content.AimPose != null) Content.AimPose.SetAiming(aiming);
        }
    }
}
