using Cinemachine;
using RPG4.Input;
using RPG4.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RPG4.PlayerMovement
{
    /// <summary>为 FreeLook 提供共享输入和轨道缩放；相机跟随、环绕与避障由 Cinemachine 完成。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CinemachineFreeLook))]
    public sealed class PlayerCameraController : MonoBehaviour, AxisState.IInputAxisProvider
    {
        [SerializeField] private CinemachineFreeLook freeLook;
        [SerializeField, Min(0.1f)] private float minimumDistance = 2f;
        [SerializeField, Min(0.1f)] private float maximumDistance = 6f;
        [Tooltip("每个滚轮刻度改变的轨道距离（米）。")]
        [SerializeField, Min(0.01f)] private float zoomSpeed = 0.5f;

        private CinemachineFreeLook.Orbit[] originalOrbits;
        private float originalDistance;
        private float distance;
        private CursorLockMode previousLockMode;
        private bool previousCursorVisible;

        private void Awake()
        {
            if (freeLook == null) freeLook = GetComponent<CinemachineFreeLook>();
            originalOrbits = (CinemachineFreeLook.Orbit[])freeLook.m_Orbits.Clone();
            originalDistance = Mathf.Max(0.01f, originalOrbits[1].m_Radius);
            distance = Mathf.Clamp(originalDistance, minimumDistance, maximumDistance);
            ApplyZoom();
        }

        private void OnEnable()
        {
            previousLockMode = Cursor.lockState;
            previousCursorVisible = Cursor.visible;
            SetCursorCaptured(true);
            freeLook.UpdateInputAxisProvider();
        }

        private void Update()
        {
            if (!Application.isFocused || (UIManager.Instance != null && UIManager.Instance.HasOpenWindows)) return;
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                SetCursorCaptured(false);
            else if (Cursor.lockState != CursorLockMode.Locked
                && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                SetCursorCaptured(true);

            InputManager input = InputManager.Instance;
            if (!CanLook || input == null) return;
            // 鼠标滚轮已在输入资产中换算为刻度，不再乘 deltaTime。
            float scroll = input.playerInput.zoom.ReadValue<float>();
            if (Mathf.Abs(scroll) < 0.001f) return;
            distance = Mathf.Clamp(distance - scroll * zoomSpeed, minimumDistance, maximumDistance);
            ApplyZoom();
        }

        public float GetAxisValue(int axis)
        {
            InputManager input = InputManager.Instance;
            if (!isActiveAndEnabled || !CanLook || input == null) return 0f;
            Vector2 look = input.playerInput.look.ReadValue<Vector2>();
            return axis == 0 ? look.x : axis == 1 ? look.y : 0f;
        }

        private bool CanLook => Application.isFocused && Time.timeScale > 0f
            && (UIManager.Instance == null || !UIManager.Instance.HasOpenWindows)
            && Cursor.lockState == CursorLockMode.Locked;

        private void ApplyZoom()
        {
            float scale = distance / originalDistance;
            for (int i = 0; i < originalOrbits.Length; i++)
                freeLook.m_Orbits[i] = new CinemachineFreeLook.Orbit(
                    originalOrbits[i].m_Height * scale, originalOrbits[i].m_Radius * scale);
        }

        private static void SetCursorCaptured(bool captured)
        {
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) SetCursorCaptured(false);
        }

        private void OnDisable()
        {
            Cursor.lockState = previousLockMode;
            Cursor.visible = previousCursorVisible;
        }

        private void OnValidate()
        {
            maximumDistance = Mathf.Max(minimumDistance, maximumDistance);
        }
    }
}
