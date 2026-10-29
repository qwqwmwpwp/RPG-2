using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>在动画与手部 IK 完成后，整体叠加上半身俯仰。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(Animator))]
    public sealed class PlayerAimPose : MonoBehaviour
    {
        [Tooltip("上半身总俯仰范围；负数向上，正数向下。")]
        [SerializeField] private Vector2 pitchRange = new Vector2(-30f, 45f);
        [SerializeField, Min(0f)] private float pitchSmoothTime = 0.1f;
        [Tooltip("仅在本帧最终上半身层完成 IK Pass 后叠加俯仰。")]
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
        private int finalIkFrame = -1;
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

        private void Update()
        {
            // 在下一次动画求值前还原，避免 IK 目标带着上一帧的俯仰或重复叠加。
            RestorePose();
            finalIkFrame = -1;
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (layerIndex == finalIkLayer) finalIkFrame = Time.frameCount;
        }

        private void LateUpdate()
        {
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
            if (finalIkFrame != Time.frameCount) return;
            float targetPitch = Mathf.Clamp(cameraController.ViewPitch, pitchRange.x, pitchRange.y);
            pitch = pitchSmoothTime <= 0f ? targetPitch : Mathf.SmoothDamp(pitch, targetPitch,
                ref pitchVelocity, pitchSmoothTime, Mathf.Infinity, deltaTime);
            pitch = Mathf.Clamp(pitch, pitchRange.x, pitchRange.y);

            spineAnimationRotation = spine.localRotation;
            if (chest != null) chestAnimationRotation = chest.localRotation;
            float spinePitch = chest != null ? pitch * 0.5f : pitch;
            // 两臂和挂在右手的枪已完成本帧求值；共同父骨骼的俯仰保持握持相对关系。
            ApplyPitch(spine, spinePitch);
            if (chest != null) ApplyPitch(chest, pitch * 0.5f);
            poseApplied = true;
        }

        private void ApplyPitch(Transform bone, float angle)
        {
            // 使用角色横轴转到骨骼父空间，不假定导入骨骼的局部 X 轴方向。
            Vector3 localAxis = bone.parent.InverseTransformDirection(player.transform.right);
            bone.localRotation = Quaternion.AngleAxis(angle, localAxis) * bone.localRotation;
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
