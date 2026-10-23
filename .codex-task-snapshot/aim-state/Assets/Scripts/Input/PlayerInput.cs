using RPG4.UI;
using UnityEngine;

namespace RPG4.Input
{
    /// <summary>
    /// 角色输入快照：统一采样与输入占用判断，具体移动、镜头和交互效果由消费方执行。
    /// 后续输入延迟、缓冲和重复触发策略统一归本组件维护，目前尚未实现这些策略。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public sealed class PlayerInput : MonoBehaviour
    {
        private Vector2 move;
        private Vector2 look;
        private float zoom;
        private bool interactPressed;
        private int sampledFrame = -1;
        private bool lookReady;
        private bool cursorStateSaved;
        private CursorLockMode previousLockMode;
        private bool previousCursorVisible;

        /// <summary>实时检查占用，窗口在采样后打开或关闭时也不能泄漏本帧输入。</summary>
        public bool CanReadGameplayInput
        {
            get
            {
                bool allowed = isActiveAndEnabled && Application.isFocused && Time.timeScale > 0f
                    && InputManager.Instance != null
                    && (UIManager.Instance == null || !UIManager.Instance.BlocksGameplayInput);
                if (!allowed) Clear();
                return allowed;
            }
        }

        // 连续移动为物理帧保留最近采样值；观察增量和按键脉冲仍只在当前帧有效。
        public Vector2 Move => CanReadGameplayInput && sampledFrame >= 0 ? move : Vector2.zero;
        public Vector2 Look => CanReadLookInput ? look : Vector2.zero;
        public float Zoom => CanReadLookInput ? zoom : 0f;
        public bool InteractPressed => HasCurrentSnapshot && interactPressed;

        private bool HasCurrentSnapshot => CanReadGameplayInput && sampledFrame == Time.frameCount;

        private bool CanReadLookInput
        {
            get
            {
                if (HasCurrentSnapshot && Cursor.lockState == CursorLockMode.Locked) return true;
                lookReady = false;
                look = Vector2.zero;
                zoom = 0f;
                return false;
            }
        }

        private void OnEnable()
        {
            previousLockMode = Cursor.lockState;
            previousCursorVisible = Cursor.visible;
            cursorStateSaved = true;
            Clear();
            if (CanReadGameplayInput) SetCursorCaptured(true);
        }

        private void Update()
        {
            if (!CanReadGameplayInput) return;

            InputManager input = InputManager.Instance;
            InputActions.BackpackActions pointer = input.backpackInput;
            if (pointer.cancel.WasPressedThisFrame())
                SetCursorCaptured(false);
            else if (Cursor.lockState != CursorLockMode.Locked && pointer.leftButton.WasPressedThisFrame())
                SetCursorCaptured(true);

            InputActions.PlayerActions player = input.playerInput;
            sampledFrame = Time.frameCount;
            move = Vector2.ClampMagnitude(player.move.ReadValue<Vector2>(), 1f);
            interactPressed = player.interact.WasPressedThisFrame();
            look = Vector2.zero;
            zoom = 0f;

            if (Cursor.lockState != CursorLockMode.Locked)
            {
                lookReady = false;
                return;
            }
            // 重新捕获指针或解除窗口占用后，首个可读帧丢弃累积的位移和滚轮。
            if (!lookReady)
            {
                lookReady = true;
                return;
            }
            look = player.look.ReadValue<Vector2>();
            zoom = player.zoom.ReadValue<float>();
        }

        /// <summary>丢弃当前快照和观察就绪状态；恢复后不会重放被占用期间的输入。</summary>
        public void Clear()
        {
            sampledFrame = -1;
            move = Vector2.zero;
            look = Vector2.zero;
            zoom = 0f;
            interactPressed = false;
            lookReady = false;
        }

        private void SetCursorCaptured(bool captured)
        {
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
            lookReady = false;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (focused) return;
            Clear();
            SetCursorCaptured(false);
        }

        private void OnDisable()
        {
            Clear();
            if (!cursorStateSaved) return;
            Cursor.lockState = previousLockMode;
            Cursor.visible = previousCursorVisible;
            cursorStateSaved = false;
        }
    }
}
