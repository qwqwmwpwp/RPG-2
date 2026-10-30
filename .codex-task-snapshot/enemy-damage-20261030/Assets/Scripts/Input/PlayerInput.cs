using System;
using RPG4.PlayerIntent;
using RPG4.UI;
using UnityEngine;

namespace RPG4.Input
{
    /// <summary>输入生命周期宿主；-200 明确采样并通知该角色的意图管理器。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public sealed class PlayerInput : MonoBehaviour
    {
        public InputIntentProcessor IntentProcessor { get; } = new InputIntentProcessor();
        public PlayerBlackboard Blackboard => IntentProcessor.Blackboard;
        private Action refreshIntent;

        // 兼容既有入口；读取快照没有门控检查或副作用。
        public bool CanReadGameplayInput => Blackboard.InputAllowed;
        public Vector2 Move => Blackboard.Move;
        public Vector2 Look => Blackboard.Look;
        public float Zoom => Blackboard.Zoom;
        public bool InteractPressed => Blackboard.InteractPressed;
        public bool CrouchHeld => Blackboard.CrouchHeld;
        public bool ReloadPressed => Blackboard.ReloadPressed;
        public bool AimHeld => Blackboard.AimHeld;
        public bool FirePressed => Blackboard.FirePressed;
        public bool FireHeld => Blackboard.FireHeld;

        private void Awake() => IntentProcessor.Attach(this);
        private void OnEnable()
        {
            IntentProcessor.Enable(this);
            RefreshIntentPermissions();
        }
        private void Update()
        {
            IntentProcessor.Sample();
            RefreshIntentPermissions();
        }
        private void LateUpdate() => RefreshIntentPermissions();

        /// <summary>单角色明确绑定；管理器拥有调用顺序，不使用全局注册或事件总线。</summary>
        public void BindIntentManager(Action refresh)
        {
            refreshIntent = refresh;
            RefreshIntentPermissions();
        }
        public void UnbindIntentManager(Action refresh)
        {
            if (refreshIntent == refresh) refreshIntent = null;
        }
        public void RefreshIntentPermissions()
        {
            IntentProcessor.RefreshPermissions();
            refreshIntent?.Invoke();
        }
        public void Clear()
        {
            IntentProcessor.Clear();
            RefreshIntentPermissions();
        }
        public void ClearWeaponInput() => IntentProcessor.ClearWeaponInput();
        public void ReportInteraction(MonoBehaviour target) => IntentProcessor.ReportInteraction(target);
        private void OnApplicationFocus(bool focused)
        {
            if (focused) return;
            IntentProcessor.Clear();
            IntentProcessor.SetCursorCaptured(false);
            RefreshIntentPermissions();
        }
        private void OnApplicationPause(bool paused) { if (paused) Clear(); }
        private void OnDisable()
        {
            IntentProcessor.Disable();
            RefreshIntentPermissions();
        }
    }

    /// <summary>采样、实时许可、帧脉冲与重按抑制；所有输入快照写入都集中在此。</summary>
    public sealed class InputIntentProcessor
    {
        public PlayerBlackboard Blackboard { get; } = new PlayerBlackboard();
        private PlayerInput host;
        private bool ownerReady;
        private bool bodyReady;
        private bool viewReady;
        private bool lifeReady = true;
        private bool aimArmed;
        private bool fireArmed;
        private int sampledFrame = -1;
        private bool lookReady;
        private bool cursorStateSaved;
        private CursorLockMode previousLockMode;
        private bool previousCursorVisible;

        internal void Attach(PlayerInput source) => host = source;
        internal void Enable(PlayerInput source)
        {
            Attach(source);
            previousLockMode = Cursor.lockState;
            previousCursorVisible = Cursor.visible;
            cursorStateSaved = true;
            Clear();
            RefreshPermissions();
            if (Blackboard.InputAllowed) SetCursorCaptured(true);
        }
        public void SetControlAvailability(bool ownerEnabled, bool bodyAvailable, bool viewAvailable,
            bool alive = true)
        {
            ownerReady = ownerEnabled;
            bodyReady = bodyAvailable;
            viewReady = viewAvailable;
            lifeReady = alive;
            RefreshPermissions();
        }
        public void RefreshPermissions()
        {
            bool allowed = host != null && host.isActiveAndEnabled && Application.isFocused
                && Time.timeScale > 0f && InputManager.Instance != null
                && (UIManager.Instance == null || !UIManager.Instance.BlocksGameplayInput);
            if (!allowed || !lifeReady) Clear();
            ExpireFrameInput();
            Blackboard.InputAllowed = allowed;
            Blackboard.CanMove = allowed && lifeReady && ownerReady && bodyReady && viewReady;
            Blackboard.CanLook = Blackboard.CanMove;
            Blackboard.CanUseWeapon = Blackboard.CanMove && Cursor.lockState == CursorLockMode.Locked;
            Blackboard.CanInteract = allowed && lifeReady;
            if (!Blackboard.CanUseWeapon) ClearWeaponInput();
            if (!Blackboard.CanLook || Cursor.lockState != CursorLockMode.Locked)
            {
                lookReady = false;
                Blackboard.Look = Vector2.zero;
                Blackboard.Zoom = 0f;
            }
        }
        private void ExpireFrameInput()
        {
            if (sampledFrame == Time.frameCount) return;
            Blackboard.Look = Vector2.zero;
            Blackboard.Zoom = 0f;
            Blackboard.InteractPressed = false;
            Blackboard.ReloadPressed = false;
            Blackboard.FirePressed = false;
        }
        internal void Sample()
        {
            RefreshPermissions();
            if (!Blackboard.InputAllowed || !lifeReady) return;
            InputManager input = InputManager.Instance;
            InputActions.BackpackActions pointer = input.backpackInput;
            bool cursorCapturedThisFrame = false;
            if (pointer.cancel.WasPressedThisFrame()) SetCursorCaptured(false);
            else if (Cursor.lockState != CursorLockMode.Locked && pointer.leftButton.WasPressedThisFrame())
            {
                SetCursorCaptured(true);
                cursorCapturedThisFrame = true;
            }
            InputActions.PlayerActions player = input.playerInput;
            sampledFrame = Time.frameCount;
            Blackboard.Move = Vector2.ClampMagnitude(player.move.ReadValue<Vector2>(), 1f);
            Blackboard.InteractPressed = player.interact.WasPressedThisFrame();
            Blackboard.CrouchHeld = player.crouch.IsPressed();
            Blackboard.ReloadPressed = !cursorCapturedThisFrame && Cursor.lockState == CursorLockMode.Locked
                && player.reload.WasPressedThisFrame();
            if (Cursor.lockState == CursorLockMode.Locked && !player.aim.IsPressed()) aimArmed = true;
            Blackboard.AimHeld = Cursor.lockState == CursorLockMode.Locked && aimArmed && player.aim.IsPressed();
            bool fireDown = player.fire.IsPressed();
            if (cursorCapturedThisFrame) ClearFire();
            else
            {
                if (!fireDown) fireArmed = true;
                Blackboard.FirePressed = fireArmed && player.fire.WasPressedThisFrame();
                Blackboard.FireHeld = fireArmed && fireDown;
            }
            Blackboard.Look = Vector2.zero;
            Blackboard.Zoom = 0f;
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                lookReady = false;
                return;
            }
            // 重新捕获指针或解除窗口占用后首帧丢弃累积位移与滚轮。
            if (!lookReady)
            {
                lookReady = true;
                return;
            }
            Blackboard.Look = player.look.ReadValue<Vector2>();
            Blackboard.Zoom = player.zoom.ReadValue<float>();
        }
        public void Clear()
        {
            sampledFrame = -1;
            Blackboard.InputAllowed = false;
            Blackboard.CanMove = false;
            Blackboard.CanLook = false;
            Blackboard.CanUseWeapon = false;
            Blackboard.CanInteract = false;
            Blackboard.Move = Vector2.zero;
            Blackboard.Look = Vector2.zero;
            Blackboard.Zoom = 0f;
            Blackboard.InteractPressed = false;
            Blackboard.CrouchHeld = false;
            Blackboard.InteractionTarget = null;
            ClearWeaponInput();
            lookReady = false;
        }
        public void ClearWeaponInput()
        {
            Blackboard.AimHeld = false;
            aimArmed = false;
            Blackboard.ReloadPressed = false;
            ClearFire();
        }
        private void ClearFire()
        {
            Blackboard.FirePressed = false;
            Blackboard.FireHeld = false;
            fireArmed = false;
        }
        internal void SetCursorCaptured(bool captured)
        {
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
            ClearWeaponInput();
            lookReady = false;
        }
        public void ReportInteraction(MonoBehaviour target) => Blackboard.InteractionTarget = target;
        internal void Disable()
        {
            Clear();
            if (!cursorStateSaved) return;
            Cursor.lockState = previousLockMode;
            Cursor.visible = previousCursorVisible;
            cursorStateSaved = false;
        }
    }
}
