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
        public bool CanPreviewLocalWeaponInput => CanReadGameplayInput && isActiveAndEnabled
            && CanSampleLocalGameplayInput && Application.isFocused && Time.timeScale > 0f
            && LocalWeaponInputAllowed && ReadState?.View.CanLook == true;
        private int localInputBlockedThroughFrame = -1;
        private bool captureCursorWhenAllowed;
        internal bool CanSampleLocalGameplayInput => LocalGameplayInputAllowed && Time.frameCount > localInputBlockedThroughFrame;
        private Action refreshIntent;
        private IPlayerInputSink inputSink;
        private Vector2 sensitivity = new Vector2(0.15f, 0.12f);
        private Vector2 localPitchRange = new Vector2(-35f, 65f);
        private float aimMultiplier = 0.65f;
        internal Vector2? InitialViewAngles { get; private set; }
        internal bool LocalInputAllowed => IntentProcessor.InputAllowed;
        internal bool LocalWeaponInputAllowed => IntentProcessor.WeaponInputAllowed;

        // 兼容消费者：角色绑定后读取同一只读逻辑状态。
        public bool CanReadGameplayInput => UsesLocalInput && Snapshot.InputAllowed;
        public Vector2 Move => Snapshot.Frame.Move;
        public Vector2 Look => Snapshot.Frame.Look;
        public float Zoom => Snapshot.Frame.Zoom;
        public PlayerInputSnapshot Snapshot => ReadState?.Input.Snapshot ?? default;
        public bool InteractPressed => Snapshot.Frame.InteractPressed;
        public bool CrouchHeld => Snapshot.Frame.CrouchHeld;
        public bool ReloadPressed => Snapshot.Frame.ReloadPressed;
        public bool AimHeld => Snapshot.Frame.AimHeld;
        public bool FirePressed => Snapshot.Frame.FirePressed;
        public bool FireHeld => Snapshot.Frame.FireHeld;

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
            if (LocalInputAllowed) inputSink?.SubmitLocalInput(IntentProcessor.Frame);
            RefreshIntentPermissions();
        }
        private void LateUpdate() => RefreshIntentPermissions();

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
            inputSink?.ClearInput();
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
        internal void BindReadState(IPlayerReadState state, IPlayerInputSink sink)
        {
            ReadState = state;
            inputSink = sink;
            if (UsesLocalInput && isActiveAndEnabled) IntentProcessor.Enable(this);
        }
        internal void SetLocalInput(bool local)
        {
            if (UsesLocalInput == local) return;
            inputSink?.ClearInput();
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
            inputSink = null;
        }
        public void RefreshIntentPermissions()
        {
            if (!UsesLocalInput || ReadState == null) return;
            IntentProcessor.RefreshPermissions();
            if (!LocalInputAllowed)
            {
                inputSink?.ClearInput();
            }
            else if (!LocalWeaponInputAllowed) inputSink?.ClearWeaponInput();
            refreshIntent?.Invoke();
        }
        public void Clear()
        {
            inputSink?.ClearInput();
            if (!UsesLocalInput) return;
            IntentProcessor.Clear();
            RefreshIntentPermissions();
        }
        public void ClearWeaponInput()
        {
            inputSink?.ClearWeaponInput();
            if (UsesLocalInput) IntentProcessor.ClearWeaponInput();
        }
        internal void ResetLocalInput()
        {
            IntentProcessor.Clear();
        }
        private void OnApplicationFocus(bool focused)
        {
            if (focused || !UsesLocalInput || ReadState == null) return;
            ClearLocalInputEdges();
            IntentProcessor.SetCursorCaptured(false);
            RefreshIntentPermissions();
        }
        private void OnApplicationPause(bool paused) { if (paused) Clear(); }
        private void OnDisable()
        {
            if (!UsesLocalInput) return;
            ResetLocalInput();
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
                Frame = new PlayerInputFrame(Frame.Move, crouchHeld: Frame.CrouchHeld, interactPressed: Frame.InteractPressed, quickUsePressed: Frame.QuickUsePressed);
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
                weaponAllowed && player.reload.WasPressedThisFrame(), player.interact.WasPressedThisFrame(),
                quickUsePressed: pointer.useConsumable.WasPressedThisFrame());
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
                interactPressed: Frame.InteractPressed, quickUsePressed: Frame.QuickUsePressed);
            aimArmed = fireArmed = false;
        }
        internal void SetCursorCaptured(bool captured)
        {
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
            ClearWeaponInput();
            Frame = new PlayerInputFrame(Frame.Move, crouchHeld: Frame.CrouchHeld, interactPressed: Frame.InteractPressed, quickUsePressed: Frame.QuickUsePressed);
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
