using System;
using System.Runtime.CompilerServices;
using RPG4.PlayerIntent;
using RPG4.UI;
using UnityEngine;

[assembly: InternalsVisibleTo("RPG4.PlayerMovement")]

namespace RPG4.Input
{
    /// <summary>本机设备宿主；只提交输入，不拥有角色黑板或业务权限。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public sealed class PlayerInput : MonoBehaviour
    {
        public InputIntentProcessor IntentProcessor { get; } = new InputIntentProcessor();
        public IPlayerReadState ReadState { get; private set; }
        public bool UsesLocalInput { get; private set; } = true;
        public bool LocalGameplayInputAllowed { get; private set; } = true;
        private int localInputBlockedThroughFrame = -1;
        private bool captureCursorWhenAllowed;
        internal bool CanSampleLocalGameplayInput => LocalGameplayInputAllowed && Time.frameCount > localInputBlockedThroughFrame;
        private Action refreshIntent;
        private PlayerInputFrame simulationInput;
        private float presentationZoom;
        private int interactionConsumedFrame = -1;
        private Action<MonoBehaviour, bool, string> reportInteraction;
        private Action<string, bool> reportFeedback;
        private Vector2 sensitivity = new Vector2(0.15f, 0.12f);
        private Vector2 localPitchRange = new Vector2(-35f, 65f);
        private float aimMultiplier = 0.65f;
        internal Vector2? InitialViewAngles { get; private set; }
        internal PlayerInputFrame Frame => IntentProcessor.Frame;
        internal bool LocalInputAllowed => IntentProcessor.InputAllowed;
        internal bool LocalWeaponInputAllowed => IntentProcessor.WeaponInputAllowed;

        // 兼容消费者：角色绑定后读取同一只读逻辑状态。
        public bool CanReadGameplayInput => UsesLocalInput && LocalInputAllowed;
        public Vector2 Move => ReadState?.Input.Move ?? Vector2.zero;
        public Vector2 Look => ReadState?.Input.Look ?? Vector2.zero;
        public float Zoom => ReadState?.Input.Zoom ?? 0f;
        public bool InteractPressed => ReadState?.Interaction.InteractPressed ?? false;
        public bool CrouchHeld => ReadState?.Input.CrouchHeld ?? false;
        public bool ReloadPressed => ReadState?.Input.ReloadPressed ?? false;
        public bool AimHeld => ReadState?.Input.AimHeld ?? false;
        public bool FirePressed => ReadState?.Input.FirePressed ?? false;
        public bool FireHeld => ReadState?.Input.FireHeld ?? false;

        private void Awake() => IntentProcessor.Attach(this);
        private void OnEnable() { if (UsesLocalInput && ReadState != null) IntentProcessor.Enable(this); RefreshIntentPermissions(); }
        private void Update()
        {
            if (!UsesLocalInput || ReadState == null) return;
            RefreshIntentPermissions();
            if (captureCursorWhenAllowed && LocalInputAllowed)
            {
                IntentProcessor.SetCursorCaptured(true);
                captureCursorWhenAllowed = false;
            }
            IntentProcessor.Sample(sensitivity, localPitchRange, aimMultiplier);
            AccumulateSimulationInput(IntentProcessor.Frame);
            if (LocalInputAllowed) presentationZoom += IntentProcessor.Frame.Zoom;
            else presentationZoom = 0f;
            RefreshIntentPermissions();
        }
        private void LateUpdate() => RefreshIntentPermissions();

        // Render samples accumulate once; fixed ticks consume edges/look exactly once.
        private void AccumulateSimulationInput(PlayerInputFrame frame)
        {
            if (!LocalInputAllowed) { simulationInput = default; return; }
            simulationInput = new PlayerInputFrame(frame.Move, simulationInput.Look + frame.Look,
                simulationInput.Zoom + frame.Zoom, frame.CrouchHeld, frame.AimHeld,
                simulationInput.FirePressed || frame.FirePressed, frame.FireHeld,
                simulationInput.ReloadPressed || frame.ReloadPressed,
                simulationInput.InteractPressed || frame.InteractPressed);
        }
        public PlayerInputFrame ConsumeSimulationInput()
        {
            RefreshIntentPermissions();
            if (!UsesLocalInput || !LocalInputAllowed) { simulationInput = default; return default; }
            if (!LocalWeaponInputAllowed) ClearPendingWeaponInput();
            PlayerInputFrame result = simulationInput;
            simulationInput = new PlayerInputFrame(result.Move, crouchHeld: result.CrouchHeld,
                aimHeld: result.AimHeld, fireHeld: result.FireHeld);
            return result;
        }
        public bool ConsumeLocalInteractionPressed()
        {
            RefreshIntentPermissions();
            if (!CanReadGameplayInput || ReadState?.Interaction.CanInteract != true || interactionConsumedFrame == Time.frameCount
                || !IntentProcessor.Frame.InteractPressed) return false;
            interactionConsumedFrame = Time.frameCount;
            return true;
        }
        public float ConsumePresentationZoom()
        {
            RefreshIntentPermissions();
            float result = CanReadGameplayInput ? presentationZoom : 0f;
            presentationZoom = 0f;
            return result;
        }
        public void SetNetworkLocalInput(bool local) => SetLocalInput(local);
        /// <summary>Only gates this device's samples; never changes simulation authority or global action maps.</summary>
        public void SetLocalGameplayInputAllowed(bool allowed)
        {
            if (LocalGameplayInputAllowed == allowed) return;
            LocalGameplayInputAllowed = allowed;
            captureCursorWhenAllowed = allowed && UsesLocalInput;
            if (!allowed && UsesLocalInput && ReadState != null) IntentProcessor.SetCursorCaptured(false);
            ClearLocalInputEdges();
        }
        /// <summary>Discard local held/edge samples and suppress the rest of this frame when restoring control.</summary>
        public void ClearLocalInputEdges()
        {
            localInputBlockedThroughFrame = Time.frameCount;
            simulationInput = default;
            presentationZoom = 0f;
            interactionConsumedFrame = Time.frameCount;
            IntentProcessor.Clear();
            RefreshIntentPermissions();
        }
        // 本地视角组件可提供设备倍率和初始采样；服务器逻辑从不读取该组件。
        internal void ConfigureLocalView(Vector2 lookSensitivity, Vector2 pitchRange, float multiplier, Vector2 initialAngles)
        {
            sensitivity = lookSensitivity;
            localPitchRange = pitchRange;
            aimMultiplier = multiplier;
            InitialViewAngles = initialAngles;
        }
        internal void BindReadState(IPlayerReadState state, Action<MonoBehaviour, bool, string> interaction,
            Action<string, bool> feedback)
        {
            ReadState = state;
            reportInteraction = interaction;
            reportFeedback = feedback;
            if (UsesLocalInput && isActiveAndEnabled) IntentProcessor.Enable(this);
        }
        internal void SetLocalInput(bool local)
        {
            if (UsesLocalInput == local) return;
            simulationInput = default;
            presentationZoom = 0f;
            if (UsesLocalInput) IntentProcessor.Disable();
            UsesLocalInput = local;
            if (!local) captureCursorWhenAllowed = false;
            if (local && isActiveAndEnabled && ReadState != null) IntentProcessor.Enable(this);
            else IntentProcessor.Clear();
        }
        public void BindIntentManager(Action refresh) { refreshIntent = refresh; RefreshIntentPermissions(); }
        public void UnbindIntentManager(Action refresh)
        {
            if (refreshIntent != refresh) return;
            refreshIntent = null;
            ReadState = null;
            reportInteraction = null;
            reportFeedback = null;
        }
        public void RefreshIntentPermissions()
        {
            if (!UsesLocalInput || ReadState == null) return;
            IntentProcessor.RefreshPermissions();
            refreshIntent?.Invoke();
        }
        public void Clear()
        {
            simulationInput = default;
            presentationZoom = 0f;
            if (!UsesLocalInput) return;
            IntentProcessor.Clear();
            RefreshIntentPermissions();
        }
        private void ClearPendingWeaponInput()
        {
            simulationInput = new PlayerInputFrame(simulationInput.Move, simulationInput.Look,
                simulationInput.Zoom, simulationInput.CrouchHeld, interactPressed: simulationInput.InteractPressed);
        }
        public void ClearWeaponInput()
        {
            ClearPendingWeaponInput();
            if (UsesLocalInput) IntentProcessor.ClearWeaponInput();
        }
        internal void ResetLocalInput() { simulationInput = default; presentationZoom = 0f; IntentProcessor.Clear(); }
        public void ReportInteraction(MonoBehaviour target, bool visible = false, string prompt = null)
        { if (UsesLocalInput) reportInteraction?.Invoke(target, visible, prompt); }
        public void ReportInteractionFeedback(string message, bool success)
        { if (UsesLocalInput) reportFeedback?.Invoke(message, success); }
        private void OnApplicationFocus(bool focused)
        {
            if (focused || !UsesLocalInput || ReadState == null) return;
            IntentProcessor.Clear();
            IntentProcessor.SetCursorCaptured(false);
            RefreshIntentPermissions();
        }
        private void OnApplicationPause(bool paused) { if (paused) Clear(); }
        private void OnDisable()
        {
            if (!UsesLocalInput) return;
            IntentProcessor.Disable();
            RefreshIntentPermissions();
        }
    }

    /// <summary>只维护本地采样和按键重按抑制；不能访问可写业务黑板。</summary>
    public sealed class InputIntentProcessor
    {
        internal PlayerInputFrame Frame { get; private set; }
        internal bool InputAllowed { get; private set; }
        internal bool WeaponInputAllowed => InputAllowed && Cursor.lockState == CursorLockMode.Locked;
        private PlayerInput host;
        private bool aimArmed, fireArmed, lookReady, cursorStateSaved;
        private int sampledFrame = -1;
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
            if (InputAllowed) SetCursorCaptured(true);
        }
        public void RefreshPermissions()
        {
            bool allowed = host != null && host.UsesLocalInput && host.CanSampleLocalGameplayInput
                && host.isActiveAndEnabled && Application.isFocused
                && Time.timeScale > 0f && InputManager.Instance != null
                && (UIManager.Instance == null || !UIManager.Instance.BlocksGameplayInput);
            if (!allowed) Clear();
            InputAllowed = allowed;
            if (sampledFrame != Time.frameCount)
                Frame = new PlayerInputFrame(Frame.Move, crouchHeld: Frame.CrouchHeld,
                    aimHeld: Frame.AimHeld, fireHeld: Frame.FireHeld);
            if (!WeaponInputAllowed)
            {
                ClearWeaponInput();
                Frame = new PlayerInputFrame(Frame.Move, crouchHeld: Frame.CrouchHeld, interactPressed: Frame.InteractPressed);
                lookReady = false;
            }
        }
        internal void Sample(Vector2 sensitivity, Vector2 pitchRange, float aimMultiplier)
        {
            RefreshPermissions();
            if (!InputAllowed) return;
            InputManager input = InputManager.Instance;
            InputActions.BackpackActions pointer = input.backpackInput;
            bool captured = false;
            if (pointer.cancel.WasPressedThisFrame()) SetCursorCaptured(false);
            else if (Cursor.lockState != CursorLockMode.Locked && pointer.leftButton.WasPressedThisFrame())
            { SetCursorCaptured(true); captured = true; }
            InputActions.PlayerActions player = input.playerInput;
            bool weaponAllowed = WeaponInputAllowed && !captured;
            if (weaponAllowed && !player.aim.IsPressed()) aimArmed = true;
            if (weaponAllowed && !player.fire.IsPressed()) fireArmed = true;
            Vector2 look = Vector2.zero;
            float zoom = 0f;
            if (WeaponInputAllowed)
            {
                if (lookReady && !captured)
                {
                    Vector2 raw = player.look.ReadValue<Vector2>();
                    float multiplier = host.ReadState?.Weapon.IsAiming == true ? aimMultiplier : 1f;
                    look = new Vector2(raw.x * sensitivity.x * multiplier, -raw.y * sensitivity.y * multiplier);
                    float pitch = host.ReadState?.View.ViewPitch ?? 0f;
                    look.y = Mathf.Clamp(pitch + look.y, pitchRange.x, pitchRange.y) - pitch;
                    zoom = player.zoom.ReadValue<float>();
                }
                lookReady = true;
            }
            else lookReady = false;
            sampledFrame = Time.frameCount;
            Frame = new PlayerInputFrame(Vector2.ClampMagnitude(player.move.ReadValue<Vector2>(), 1f), look, zoom,
                player.crouch.IsPressed(), weaponAllowed && aimArmed && player.aim.IsPressed(),
                weaponAllowed && fireArmed && player.fire.WasPressedThisFrame(),
                weaponAllowed && fireArmed && player.fire.IsPressed(),
                weaponAllowed && player.reload.WasPressedThisFrame(), player.interact.WasPressedThisFrame());
        }
        public void Clear()
        {
            sampledFrame = -1;
            InputAllowed = false;
            Frame = default;
            aimArmed = fireArmed = lookReady = false;
        }
        public void ClearWeaponInput()
        {
            Frame = new PlayerInputFrame(Frame.Move, Frame.Look, Frame.Zoom, Frame.CrouchHeld,
                interactPressed: Frame.InteractPressed);
            aimArmed = fireArmed = false;
        }
        internal void SetCursorCaptured(bool captured)
        {
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
            ClearWeaponInput();
            Frame = new PlayerInputFrame(Frame.Move, crouchHeld: Frame.CrouchHeld, interactPressed: Frame.InteractPressed);
            lookReady = false;
        }
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
