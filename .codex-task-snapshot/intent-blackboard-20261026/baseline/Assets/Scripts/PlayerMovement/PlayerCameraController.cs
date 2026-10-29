using Cinemachine;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>读取角色视角状态，只调节距离与 FOV；位置、旋转、构图和避障由 Cinemachine 完成。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CinemachineVirtualCamera))]
    [DefaultExecutionOrder(50)] // 角色视角节点（-100）之后，Brain（100）之前。
    public sealed class PlayerCameraController : MonoBehaviour
    {
        [SerializeField] private CinemachineVirtualCamera virtualCamera;
        [SerializeField] private PlayerViewController viewController;
        [SerializeField, Min(0.1f)] private float initialDistance = 4f;
        [SerializeField, Min(0.1f)] private float minimumDistance = 2f;
        [SerializeField, Min(0.1f)] private float maximumDistance = 6f;
        [Tooltip("每个滚轮刻度改变的相机距离（米），不改变肩部偏移或 FOV。")]
        [SerializeField, Min(0.01f)] private float zoomSpeed = 0.5f;
        [SerializeField, Min(0f)] private float zoomSmoothTime = 0.1f;
        [SerializeField, Min(0.1f)] private float aimDistance = 1.5f;
        [SerializeField, Range(1f, 179f)] private float aimFieldOfView = 45f;
        [SerializeField, Min(0f)] private float aimTransitionTime = 0.15f;

        private Cinemachine3rdPersonFollow follow;
        private float targetDistance;
        private float currentDistance;
        private float explorationFieldOfView;
        private float fieldOfViewVelocity;
        private float zoomVelocity;
        private bool aiming;
        private bool restoringAim;
        private bool initialized;

        private void Awake()
        {
            if (virtualCamera == null) virtualCamera = GetComponent<CinemachineVirtualCamera>();
            follow = virtualCamera.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
            if (viewController == null || follow == null)
            {
                Debug.LogError("PlayerCameraController 需要配置 PlayerViewController 和 3rd Person Follow。", this);
                enabled = false;
                return;
            }

            targetDistance = Mathf.Clamp(initialDistance, minimumDistance, maximumDistance);
            currentDistance = targetDistance;
            explorationFieldOfView = virtualCamera.m_Lens.FieldOfView;
            follow.CameraDistance = targetDistance;
            virtualCamera.Follow = viewController.transform;
            virtualCamera.LookAt = null;
            virtualCamera.PreviousStateIsValid = false;
            initialized = true;
        }

        private void Update()
        {
            if (!initialized || viewController == null || !viewController.isActiveAndEnabled
                || viewController.IsAiming) return;
            // 鼠标滚轮已在输入资产中换算为刻度，不再乘 deltaTime。
            float scroll = viewController.Zoom;
            targetDistance = Mathf.Clamp(targetDistance - scroll * zoomSpeed, minimumDistance, maximumDistance);
        }

        private void LateUpdate()
        {
            if (!initialized) return;
            bool wantsAim = viewController != null && viewController.isActiveAndEnabled && viewController.IsAiming;
            if (aiming != wantsAim)
            {
                aiming = wantsAim;
                restoringAim = !aiming;
            }
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

        private void OnValidate()
        {
            minimumDistance = Mathf.Max(0.1f, minimumDistance);
            maximumDistance = Mathf.Max(minimumDistance, maximumDistance);
            initialDistance = Mathf.Clamp(initialDistance, minimumDistance, maximumDistance);
            aimDistance = Mathf.Max(0.1f, aimDistance);
            aimFieldOfView = Mathf.Clamp(aimFieldOfView, 1f, 179f);
            aimTransitionTime = Mathf.Max(0f, aimTransitionTime);
        }
    }
}
