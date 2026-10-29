using RPG4.Input;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.Interaction
{
    /// <summary>本机玩家交互：从相机视口中心检测最近物体，按键后调用目标的交互入口。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public sealed class PlayerInteraction : MonoBehaviour
    {
        [SerializeField] private PlayerInput playerInput;
        [Tooltip("留空时使用 Camera.main；仅在本机玩家上启用本组件。")]
        [SerializeField] private Camera interactionCamera;
        [Tooltip("玩家根节点到命中点的最大距离，支持相机位于玩家身后的第三人称视角。")]
        [SerializeField, Min(0.1f)] private float interactionDistance = 3f;
        [Tooltip("同时包含可交互物体与墙壁等遮挡物；默认排除 Ignore Raycast 层。")]
        [SerializeField] private LayerMask raycastLayers = Physics.DefaultRaycastLayers;
        [Tooltip("已配置好层级和引用的准心预制体，每个玩家只创建一次。")]
        [SerializeField] private CrosshairUI crosshairPrefab;

        private PlayerBlackboard blackboard;
        private readonly RaycastHit[] hitBuffer = new RaycastHit[16];
        private MonoBehaviour targetComponent;
        private CrosshairUI crosshairUI;

        public IInteractable CurrentTarget => targetComponent != null && targetComponent.isActiveAndEnabled
            ? targetComponent as IInteractable : null;

        /// <summary>向本机玩家显示当前交互的结果；不创建额外 UI。</summary>
        public void ShowInteractionFeedback(string message, bool success)
        {
            if (crosshairUI != null) crosshairUI.ShowFeedback(message, success);
        }

        private bool CanUseInteraction => isActiveAndEnabled
            && blackboard != null && blackboard.CanInteract
            && interactionCamera != null && interactionCamera.isActiveAndEnabled;

        private void Awake()
        {
            if (playerInput == null) playerInput = GetComponent<PlayerInput>();
            if (playerInput != null) blackboard = playerInput.Blackboard;
            if (interactionCamera == null) interactionCamera = Camera.main;

            if (crosshairPrefab != null)
            {
                crosshairUI = Instantiate(crosshairPrefab, transform, false);
                crosshairUI.SetState(false, false);
            }
        }

        private void LateUpdate()
        {
            if (interactionCamera == null) interactionCamera = Camera.main;
            if (playerInput != null) playerInput.RefreshIntentPermissions();
            targetComponent = CanUseInteraction ? FindTarget() : null;

            IInteractable target = CurrentTarget;
            if (playerInput != null) playerInput.RefreshIntentPermissions();
            if (target != null && CanUseInteraction && blackboard.InteractPressed) target.Interact(this);

            // 交互事件可能刚打开窗口或禁用了物品，立即刷新共享许可并撤销姿态。
            if (playerInput != null) playerInput.RefreshIntentPermissions();
            if (!CanUseInteraction) targetComponent = null;
            if (playerInput != null) playerInput.ReportInteraction(targetComponent);
            if (crosshairUI != null)
                crosshairUI.SetState(CanUseInteraction, CurrentTarget != null,
                    (CurrentTarget as IInteractionPrompt)?.InteractionPrompt);
        }

        private MonoBehaviour FindTarget()
        {
            Ray ray = interactionCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            float reach = Mathf.Max(0.1f, interactionDistance);
            float rayDistance = Vector3.Distance(ray.origin, transform.position) + reach;
            int hitCount = Physics.RaycastNonAlloc(ray, hitBuffer, rayDistance, raycastLayers,
                QueryTriggerInteraction.Ignore);
            RaycastHit[] hits = hitBuffer;
            // 缓冲区满时查询完整结果，避免被漏掉的近处墙壁导致穿墙交互。
            if (hitCount == hitBuffer.Length)
            {
                hits = Physics.RaycastAll(ray, rayDistance, raycastLayers, QueryTriggerInteraction.Ignore);
                hitCount = hits.Length;
            }

            int closestIndex = -1;
            float closestDistance = float.PositiveInfinity;
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider.transform.IsChildOf(transform) || hit.distance >= closestDistance) continue;
                closestIndex = i;
                closestDistance = hit.distance;
            }

            if (closestIndex < 0) return null;
            RaycastHit closest = hits[closestIndex];
            if ((closest.point - transform.position).sqrMagnitude > reach * reach) return null;

            // 只解析最近碰撞体，非交互物体也会遮挡；允许 Collider 位于物品子节点。
            IInteractable target = closest.collider.GetComponentInParent<IInteractable>();
            MonoBehaviour component = target as MonoBehaviour;
            return component != null && component.isActiveAndEnabled && target.CanInteract(this)
                ? component : null;
        }

        private void OnDisable()
        {
            targetComponent = null;
            if (playerInput != null) playerInput.ReportInteraction(null);
            if (crosshairUI != null) crosshairUI.SetState(false, false);
        }

        private void OnDestroy()
        {
            if (crosshairUI != null) Destroy(crosshairUI.gameObject);
        }
    }
}
