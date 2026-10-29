using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>在 Humanoid IK Pass 中叠加上半身俯仰；手部 IK 仍由武器组件负责。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(Animator))]
    public sealed class PlayerAimPose : MonoBehaviour
    {
        [Tooltip("上半身总俯仰范围；负数向上，正数向下。")]
        [SerializeField] private Vector2 pitchRange = new Vector2(-30f, 45f);
        [SerializeField, Min(0f)] private float pitchSmoothTime = 0.1f;
        [Tooltip("只在最终上半身层做一次俯仰；该层与武器手 IK 共用唯一 IK Pass。")]
        [SerializeField, Min(0)] private int finalIkLayer = 1;

        private Player player;
        private PlayerCameraController cameraController;
        private Animator animator;
        private Transform spine;
        private Transform chest;
        private Quaternion spineAnimationRotation;
        private Quaternion chestAnimationRotation;
        private bool aiming;
        private bool poseApplied;
        private float pitch;
        private float pitchVelocity;

        internal void Initialize(Player owner, PlayerCameraController camera)
        {
            player = owner;
            cameraController = camera;
            animator = GetComponent<Animator>();
            if (animator.avatar == null || !animator.avatar.isValid || !animator.isHuman) return;
            spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        }

        internal void SetAiming(bool value)
        {
            if (aiming == value) return;
            aiming = value;
            if (!aiming)
            {
                pitch = 0f;
                pitchVelocity = 0f;
                RestorePose();
            }
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (layerIndex != finalIkLayer) return;
            // 此时 Animator 已给出本帧基础姿态，不能再恢复上一帧的基础旋转。
            poseApplied = false;
            if (!aiming || player == null || !player.isActiveAndEnabled || player.Input == null
                || !player.Input.CanReadGameplayInput || Cursor.lockState != CursorLockMode.Locked
                || cameraController == null || !cameraController.isActiveAndEnabled
                || animator == null || !animator.isActiveAndEnabled || spine == null || !isActiveAndEnabled)
            {
                pitch = 0f;
                pitchVelocity = 0f;
                return;
            }

            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;
            float targetPitch = Mathf.Clamp(cameraController.ViewPitch, pitchRange.x, pitchRange.y);
            pitch = pitchSmoothTime <= 0f ? targetPitch : Mathf.SmoothDamp(pitch, targetPitch,
                ref pitchVelocity, pitchSmoothTime, Mathf.Infinity, deltaTime);
            pitch = Mathf.Clamp(pitch, pitchRange.x, pitchRange.y);

            spineAnimationRotation = spine.localRotation;
            if (chest != null) chestAnimationRotation = chest.localRotation;
            float spinePitch = chest != null ? pitch * 0.5f : pitch;
            ApplyPitch(HumanBodyBones.Spine, spine, spinePitch);
            if (chest != null) ApplyPitch(HumanBodyBones.Chest, chest, pitch * 0.5f);
            poseApplied = true;
        }

        private void ApplyPitch(HumanBodyBones boneId, Transform bone, float angle)
        {
            // 使用角色横轴转到骨骼父空间，不假定导入骨骼的局部 X 轴方向。
            Vector3 localAxis = bone.parent.InverseTransformDirection(player.transform.right);
            animator.SetBoneLocalRotation(boneId,
                Quaternion.AngleAxis(angle, localAxis) * bone.localRotation);
        }

        private void RestorePose()
        {
            // 暂停或禁用时 Animator 可能不再求值，立即撤销最后一次上半身补偿。
            if (!poseApplied) return;
            if (spine != null) spine.localRotation = spineAnimationRotation;
            if (chest != null) chest.localRotation = chestAnimationRotation;
            poseApplied = false;
        }

        private void OnDisable()
        {
            SetAiming(false);
            RestorePose();
        }

        private void OnValidate()
        {
            pitchRange.x = Mathf.Clamp(pitchRange.x, -60f, 0f);
            pitchRange.y = Mathf.Clamp(pitchRange.y, 0f, 60f);
            pitchSmoothTime = Mathf.Max(0f, pitchSmoothTime);
        }
    }
}
