using Cinemachine;
using RPG4.Input;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>摄像机块：维护独立观察角和距离，移动后更新跟随节点；构图与避障由 Cinemachine 完成。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CinemachineVirtualCamera))]
    [DefaultExecutionOrder(-100)] // 输入先于角色移动，LateUpdate 先于 Brain（100）。
    public sealed class PlayerCameraController : MonoBehaviour
    {
        [SerializeField] private CinemachineVirtualCamera virtualCamera;
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private Transform cameraTarget;
        [SerializeField] private Transform cameraPivot;
        [SerializeField, Min(0.1f)] private float initialDistance = 4f;
        [SerializeField] private float initialYaw;
        [SerializeField] private float initialPitch = 12f;
        [Tooltip("鼠标位移到观察角度的倍率，不乘 deltaTime。")]
        [SerializeField] private Vector2 lookSensitivity = new Vector2(0.15f, 0.12f);
        [Tooltip("俯仰角范围；负数向上看，正数向下看。")]
        [SerializeField] private Vector2 pitchRange = new Vector2(-35f, 65f);
        [SerializeField, Min(0.1f)] private float minimumDistance = 2f;
        [SerializeField, Min(0.1f)] private float maximumDistance = 6f;
        [Tooltip("每个滚轮刻度改变的相机距离（米），不改变肩部偏移或 FOV。")]
        [SerializeField, Min(0.01f)] private float zoomSpeed = 0.5f;
        [SerializeField, Min(0f)] private float zoomSmoothTime = 0.1f;
        [SerializeField, Min(0.1f)] private float aimDistance = 1.5f;
        [SerializeField, Range(1f, 179f)] private float aimFieldOfView = 45f;
        [SerializeField, Min(0f)] private float aimSensitivityMultiplier = 0.65f;
        [SerializeField, Min(0f)] private float aimTransitionTime = 0.15f;

        private Cinemachine3rdPersonFollow follow;
        private float viewYaw;
        private float viewPitch;
        private float targetDistance;
        private float currentDistance;
        private float explorationFieldOfView;
        private float fieldOfViewVelocity;
        private float zoomVelocity;
        private bool aiming;
        private bool restoringAim;
        private bool initialized;

        /// <summary>角色移动只读取逻辑观察角，不读取受避障或视觉反馈影响的实际相机。</summary>
        public float ViewYaw => viewYaw;
        public float ViewPitch => viewPitch;

        public void SetAiming(bool value)
        {
            if (value && (!initialized || !isActiveAndEnabled)) return;
            if (aiming == value) return;
            aiming = value;
            restoringAim = !value;
        }

        private void Awake()
        {
            if (virtualCamera == null) virtualCamera = GetComponent<CinemachineVirtualCamera>();
            follow = virtualCamera.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
            if (cameraTarget == null || cameraPivot == null || follow == null)
            {
                Debug.LogError("PlayerCameraController 需要配置 CameraTarget、CameraPivot 和 3rd Person Follow。", this);
                enabled = false;
                return;
            }

            viewYaw = Mathf.DeltaAngle(0f, initialYaw);
            viewPitch = Mathf.Clamp(initialPitch, pitchRange.x, pitchRange.y);
            targetDistance = Mathf.Clamp(initialDistance, minimumDistance, maximumDistance);
            currentDistance = targetDistance;
            explorationFieldOfView = virtualCamera.m_Lens.FieldOfView;
            follow.CameraDistance = targetDistance;
            virtualCamera.Follow = cameraPivot;
            virtualCamera.LookAt = null;
            UpdatePivot();
            virtualCamera.PreviousStateIsValid = false;
            initialized = true;
        }

        private void Update()
        {
            if (!CanKeepAiming()) SetAiming(false);
            if (playerInput == null || !playerInput.isActiveAndEnabled || !playerInput.CanReadGameplayInput) return;

            Vector2 look = playerInput.Look;
            float sensitivity = aiming ? aimSensitivityMultiplier : 1f;
            viewYaw = Mathf.DeltaAngle(0f, viewYaw + look.x * lookSensitivity.x * sensitivity);
            viewPitch = Mathf.Clamp(viewPitch - look.y * lookSensitivity.y * sensitivity, pitchRange.x, pitchRange.y);
            // 鼠标滚轮已在输入资产中换算为刻度，不再乘 deltaTime。
            if (aiming) return;
            float scroll = playerInput.Zoom;
            targetDistance = Mathf.Clamp(targetDistance - scroll * zoomSpeed, minimumDistance, maximumDistance);
        }

        private void LateUpdate()
        {
            if (!initialized || cameraTarget == null) return;
            // 即使 UI 正在占用输入，也继续跟随角色位置。
            UpdatePivot();
            if (!CanKeepAiming()) SetAiming(false);
            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f) return;
            float distance = aiming ? aimDistance : targetDistance;
            float smoothTime = aiming || restoringAim ? aimTransitionTime : zoomSmoothTime;
            // 仅平滑逻辑距离，绝不从 Cinemachine 的实际避障位置回写目标距离。
            currentDistance = smoothTime <= 0f ? distance : Mathf.SmoothDamp(
                currentDistance, distance, ref zoomVelocity, smoothTime, Mathf.Infinity, deltaTime);
            follow.CameraDistance = currentDistance;
            float fieldOfView = aiming ? aimFieldOfView : explorationFieldOfView;
            virtualCamera.m_Lens.FieldOfView = aimTransitionTime <= 0f ? fieldOfView : Mathf.SmoothDamp(
                virtualCamera.m_Lens.FieldOfView, fieldOfView, ref fieldOfViewVelocity,
                aimTransitionTime, Mathf.Infinity, deltaTime);
            if (restoringAim && Mathf.Abs(currentDistance - targetDistance) < 0.001f
                && Mathf.Abs(virtualCamera.m_Lens.FieldOfView - explorationFieldOfView) < 0.001f)
                restoringAim = false;
        }

        private bool CanKeepAiming()
        {
            return playerInput != null && playerInput.isActiveAndEnabled && playerInput.CanReadGameplayInput
                && Cursor.lockState == CursorLockMode.Locked;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) SetAiming(false);
        }

        private void OnDisable()
        {
            aiming = false;
            restoringAim = false;
            zoomVelocity = 0f;
            fieldOfViewVelocity = 0f;
            if (!initialized) return;
            currentDistance = targetDistance;
            follow.CameraDistance = currentDistance;
            virtualCamera.m_Lens.FieldOfView = explorationFieldOfView;
            virtualCamera.PreviousStateIsValid = false;
        }

        private void UpdatePivot()
        {
            cameraPivot.SetPositionAndRotation(cameraTarget.position, Quaternion.Euler(viewPitch, viewYaw, 0f));
        }

        private void OnValidate()
        {
            minimumDistance = Mathf.Max(0.1f, minimumDistance);
            maximumDistance = Mathf.Max(minimumDistance, maximumDistance);
            initialDistance = Mathf.Clamp(initialDistance, minimumDistance, maximumDistance);
            pitchRange.x = Mathf.Clamp(pitchRange.x, -89f, 89f);
            pitchRange.y = Mathf.Clamp(pitchRange.y, pitchRange.x, 89f);
            initialPitch = Mathf.Clamp(initialPitch, pitchRange.x, pitchRange.y);
            lookSensitivity.x = Mathf.Max(0f, lookSensitivity.x);
            lookSensitivity.y = Mathf.Max(0f, lookSensitivity.y);
            aimDistance = Mathf.Max(0.1f, aimDistance);
            aimFieldOfView = Mathf.Clamp(aimFieldOfView, 1f, 179f);
            aimSensitivityMultiplier = Mathf.Max(0f, aimSensitivityMultiplier);
            aimTransitionTime = Mathf.Max(0f, aimTransitionTime);
        }
    }
}
