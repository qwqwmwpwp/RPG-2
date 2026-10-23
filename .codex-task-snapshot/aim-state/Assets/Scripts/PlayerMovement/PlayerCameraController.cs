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

        private Cinemachine3rdPersonFollow follow;
        private float viewYaw;
        private float viewPitch;
        private float targetDistance;
        private float zoomVelocity;
        private bool initialized;

        /// <summary>角色移动只读取逻辑观察角，不读取受避障或视觉反馈影响的实际相机。</summary>
        public float ViewYaw => viewYaw;

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
            follow.CameraDistance = targetDistance;
            virtualCamera.Follow = cameraPivot;
            virtualCamera.LookAt = null;
            UpdatePivot();
            virtualCamera.PreviousStateIsValid = false;
            initialized = true;
        }

        private void Update()
        {
            if (playerInput == null || !playerInput.isActiveAndEnabled || !playerInput.CanReadGameplayInput) return;

            Vector2 look = playerInput.Look;
            viewYaw = Mathf.DeltaAngle(0f, viewYaw + look.x * lookSensitivity.x);
            viewPitch = Mathf.Clamp(viewPitch - look.y * lookSensitivity.y, pitchRange.x, pitchRange.y);
            // 鼠标滚轮已在输入资产中换算为刻度，不再乘 deltaTime。
            float scroll = playerInput.Zoom;
            targetDistance = Mathf.Clamp(targetDistance - scroll * zoomSpeed, minimumDistance, maximumDistance);
        }

        private void LateUpdate()
        {
            if (!initialized || cameraTarget == null) return;
            // 即使 UI 正在占用输入，也继续跟随角色位置。
            UpdatePivot();
            if (Time.deltaTime <= 0f) return;
            follow.CameraDistance = zoomSmoothTime <= 0f ? targetDistance : Mathf.SmoothDamp(
                follow.CameraDistance, targetDistance, ref zoomVelocity, zoomSmoothTime,
                Mathf.Infinity, Time.deltaTime);
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
        }
    }
}
