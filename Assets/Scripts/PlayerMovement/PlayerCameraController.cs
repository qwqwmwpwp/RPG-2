using Cinemachine;
using UnityEngine;
using UnityEngine.Serialization;

namespace RPG4.PlayerMovement
{
    /// <summary>按非攻击与瞄准/射击两套参数调节肩部偏移、距离和 FOV；相机跟随与避障由 Cinemachine 完成。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CinemachineVirtualCamera))]
    [DefaultExecutionOrder(50)] // 角色视角节点（-100）之后，Brain（100）之前。
    public sealed class PlayerCameraController : MonoBehaviour
    {
        [SerializeField] private CinemachineVirtualCamera virtualCamera;
        [SerializeField] private PlayerViewController viewController;
        [Header("非攻击相机")]
        [SerializeField, InspectorName("Shoulder Offset")]
        private Vector3 explorationShoulderOffset = new Vector3(0.45f, 0.1f, 0f);
        [SerializeField, Range(1f, 179f), InspectorName("Field Of View")]
        private float explorationFieldOfView = 60f;
        [SerializeField, Min(0.1f)] private float initialDistance = 4f;
        [SerializeField, Min(0.1f)] private float minimumDistance = 2f;
        [SerializeField, Min(0.1f)] private float maximumDistance = 6f;
        [Tooltip("每个滚轮刻度改变的相机距离（米），不改变肩部偏移或 FOV。")]
        [SerializeField, Min(0.01f)] private float zoomSpeed = 0.5f;
        [SerializeField, Min(0f)] private float zoomSmoothTime = 0.1f;
        [Header("瞄准 / 射击相机")]
        [SerializeField, InspectorName("Shoulder Offset")]
        private Vector3 attackShoulderOffset = new Vector3(0.45f, 0.1f, 0f);
        [FormerlySerializedAs("aimDistance")]
        [SerializeField, Min(0.1f), InspectorName("Distance")] private float attackDistance = 1.5f;
        [FormerlySerializedAs("aimFieldOfView")]
        [SerializeField, Range(1f, 179f), InspectorName("Field Of View")] private float attackFieldOfView = 45f;
        [Header("相机切换")]
        [FormerlySerializedAs("aimTransitionTime")]
        [SerializeField, Min(0f)] private float transitionTime = 0.15f;

        private PlayerPresenter presenter;
        private float explorationDistance;
        internal float InitialDistance => initialDistance;
        internal float MinimumDistance => minimumDistance;
        internal float MaximumDistance => maximumDistance;
        internal float ZoomSpeed => zoomSpeed;
        internal float ExplorationFieldOfView => explorationFieldOfView;
        internal float AttackDistance => attackDistance;
        internal float AttackFieldOfView => attackFieldOfView;
        private Cinemachine3rdPersonFollow follow;
        private float currentDistance;
        private float fieldOfViewVelocity;
        private float zoomVelocity;
        private Vector3 shoulderOffsetVelocity;
        private bool attacking;
        private bool restoringExploration;
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

            currentDistance = explorationDistance = Mathf.Clamp(initialDistance, minimumDistance, maximumDistance);
            follow.CameraDistance = currentDistance;
            follow.ShoulderOffset = explorationShoulderOffset;
            virtualCamera.m_Lens.FieldOfView = explorationFieldOfView;
            virtualCamera.Follow = viewController.transform;
            virtualCamera.LookAt = null;
            virtualCamera.PreviousStateIsValid = false;
            initialized = true;
        }

        private void BindPresenter()
        {
            if (presenter != null || viewController == null) return;
            presenter = viewController.Presenter;
            presenter?.RegisterCamera(this);
        }
        private void Update() { if (initialized) BindPresenter(); }
        private void LateUpdate()
        {
            if (!initialized) return;
            BindPresenter();
            presenter?.ApplyCamera(this, Time.unscaledDeltaTime);
        }
        internal void Apply(PlayerCameraTarget target, float deltaTime)
        {
            if (!initialized || !isActiveAndEnabled) return;
            explorationDistance = target.ExplorationDistance;
            bool wantsAttack = target.Attacking;
            if (attacking != wantsAttack)
            {
                attacking = wantsAttack;
                restoringExploration = !attacking;
            }
            if (deltaTime <= 0f) return;
            float distance = target.Distance;
            float smoothTime = attacking || restoringExploration ? transitionTime : zoomSmoothTime;
            // 仅平滑逻辑距离，绝不从 Cinemachine 的实际避障位置回写目标距离。
            currentDistance = smoothTime <= 0f ? distance : Mathf.SmoothDamp(
                currentDistance, distance, ref zoomVelocity, smoothTime, Mathf.Infinity, deltaTime);
            follow.CameraDistance = currentDistance;
            float fieldOfView = target.FieldOfView;
            virtualCamera.m_Lens.FieldOfView = transitionTime <= 0f ? fieldOfView : Mathf.SmoothDamp(
                virtualCamera.m_Lens.FieldOfView, fieldOfView, ref fieldOfViewVelocity,
                transitionTime, Mathf.Infinity, deltaTime);
            Vector3 shoulderOffset = attacking ? attackShoulderOffset : explorationShoulderOffset;
            follow.ShoulderOffset = transitionTime <= 0f ? shoulderOffset : Vector3.SmoothDamp(
                follow.ShoulderOffset, shoulderOffset, ref shoulderOffsetVelocity,
                transitionTime, Mathf.Infinity, deltaTime);
            if (restoringExploration && Mathf.Abs(currentDistance - explorationDistance) < 0.001f
                && Mathf.Abs(virtualCamera.m_Lens.FieldOfView - explorationFieldOfView) < 0.001f
                && (follow.ShoulderOffset - explorationShoulderOffset).sqrMagnitude < 0.000001f)
                restoringExploration = false;
        }

        private void OnDisable()
        {
            presenter?.UnregisterCamera(this);
            presenter = null;
            attacking = false;
            restoringExploration = false;
            zoomVelocity = 0f;
            fieldOfViewVelocity = 0f;
            shoulderOffsetVelocity = Vector3.zero;
            if (!initialized) return;
            currentDistance = explorationDistance > 0f ? explorationDistance
                : Mathf.Clamp(initialDistance, minimumDistance, maximumDistance);
            follow.CameraDistance = currentDistance;
            follow.ShoulderOffset = explorationShoulderOffset;
            virtualCamera.m_Lens.FieldOfView = explorationFieldOfView;
            virtualCamera.PreviousStateIsValid = false;
        }

        private void OnValidate()
        {
            minimumDistance = Mathf.Max(0.1f, minimumDistance);
            maximumDistance = Mathf.Max(minimumDistance, maximumDistance);
            initialDistance = Mathf.Clamp(initialDistance, minimumDistance, maximumDistance);
            explorationFieldOfView = Mathf.Clamp(explorationFieldOfView, 1f, 179f);
            attackDistance = Mathf.Max(0.1f, attackDistance);
            attackFieldOfView = Mathf.Clamp(attackFieldOfView, 1f, 179f);
            transitionTime = Mathf.Max(0f, transitionTime);
        }
    }
}
