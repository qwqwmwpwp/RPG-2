using RPG4.Input;
using RPG4.UI;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>本机第三人称移动：相机相对方向、平滑转身、重力，以及实际移动速度驱动的动画。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerMovement : MonoBehaviour
    {
        private static readonly int SpeedId = Animator.StringToHash("speed");

        [Tooltip("移动参考相机；留空时使用 Main Camera。")]
        [SerializeField] private Transform cameraTransform;
        [SerializeField] private Animator animator;
        [SerializeField, Min(0.1f)] private float moveSpeed = 4f;
        [SerializeField, Min(0.01f)] private float turnSmoothTime = 0.12f;
        [SerializeField, Range(-50f, -1f)] private float gravity = -25f;

        private CharacterController controller;
        private float verticalSpeed;
        private float turnVelocity;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            if (animator == null) animator = GetComponentInChildren<Animator>();
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f || !controller.enabled) return;

            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            InputManager input = InputManager.Instance;
            Vector2 move = input != null && Application.isFocused && cameraTransform != null
                && (UIManager.Instance == null || !UIManager.Instance.HasOpenWindows)
                ? Vector2.ClampMagnitude(input.playerInput.move.ReadValue<Vector2>(), 1f)
                : Vector2.zero;
            Quaternion cameraYaw = cameraTransform != null
                ? Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f) : Quaternion.identity;
            Vector3 direction = cameraYaw * new Vector3(move.x, 0f, move.y);

            if (direction.sqrMagnitude > 0.0001f)
            {
                float targetYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
                float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetYaw,
                    ref turnVelocity, turnSmoothTime);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }
            else turnVelocity = 0f;

            if (controller.isGrounded && verticalSpeed < 0f) verticalSpeed = -2f;
            verticalSpeed = Mathf.Max(verticalSpeed + gravity * deltaTime, -50f);
            Vector3 previousPosition = transform.position;
            CollisionFlags collisions = controller.Move(
                (direction * moveSpeed + Vector3.up * verticalSpeed) * deltaTime);
            if ((collisions & CollisionFlags.Below) != 0 && verticalSpeed < 0f) verticalSpeed = -2f;

            if (animator != null)
            {
                Vector3 displacement = transform.position - previousPosition;
                displacement.y = 0f;
                float speed = Mathf.Clamp01(displacement.magnitude / (deltaTime * moveSpeed));
                animator.SetFloat(SpeedId, speed, 0.1f, deltaTime);
            }
        }

        private void OnDisable()
        {
            verticalSpeed = 0f;
            turnVelocity = 0f;
            if (animator != null) animator.SetFloat(SpeedId, 0f);
        }
    }
}
