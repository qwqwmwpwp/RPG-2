using RPG4.Input;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>每角色逻辑管理器；选择输入来源、写业务权限并明确调度逻辑处理器。</summary>
    internal sealed class PlayerIntentManager
    {
        private readonly Player owner;
        private readonly PlayerInput source;
        private readonly PlayerContext context;
        private readonly LocomotionIntentProcessor locomotion;
        private readonly PlayerIntentProcessor decisions;
        private readonly PlayerBlackboard blackboard;
        internal IPlayerReadState ReadState => context.ReadState;
        internal PlayerBlackboard Blackboard => blackboard;
        internal PlayerInputSource InputSource { get; private set; }
        private PlayerInputFrame pendingExternalInput;
        private bool hasPendingExternalInput;
        internal PlayerIntentManager(Player owner, PlayerInput source, PlayerContext context)
        {
            this.owner = owner;
            this.source = source;
            this.context = context;
            InputSource = owner.InputSource;
            source.SetLocalInput(InputSource == PlayerInputSource.Local);
            blackboard = context.Blackboard;
            locomotion = new LocomotionIntentProcessor(context, blackboard);
            decisions = new PlayerIntentProcessor(context, blackboard);
        }
        internal void Bind()
        {
            source.BindReadState(ReadState, ReportInteraction, ReportInteractionFeedback);
            source.BindIntentManager(RefreshLocalInput);
        }
        internal void Dispose()
        {
            if (source != null) source.UnbindIntentManager(RefreshLocalInput);
        }
        internal void RefreshPermissions()
        {
            PlayerContentData content = context.Content;
            blackboard.IsAlive = owner != null && owner.IsAlive;
            blackboard.IsHurt = owner != null && owner.IsHurt;
            // 业务权限仅由逻辑事实产生；不引用本机设备、窗口、相机或表现状态。
            bool active = owner != null && owner.isActiveAndEnabled && blackboard.IsAlive;
            bool bodyAvailable = content.Body != null && (!content.Body.isKinematic || owner.IsNetworkSimulation);
            blackboard.CanMove = active && bodyAvailable;
            blackboard.CanLook = active;
            blackboard.CanUseWeapon = active && bodyAvailable && !blackboard.IsHurt && context.Weapon != null;
            blackboard.CanInteract = active;
            if (!active) ClearInput();
            locomotion.RefreshAvailability();
            decisions.Refresh();
        }
        private void RefreshLocalInput()
        {
            if (InputSource != PlayerInputSource.Local)
            {
                // Local interaction is a request to the server, separate from the simulation input mailbox.
                if (source.UsesLocalInput) blackboard.CanInteract = owner.IsAlive && owner.isActiveAndEnabled;
                return;
            }
            ApplyInput(source.Frame, source.LocalInputAllowed);
            // 撤回本地武器输入也取消进行中的换弹/瞄准；不改变服务器业务许可。
            if (!source.LocalWeaponInputAllowed) blackboard.WeaponCancellationRequested = true;
            RefreshPermissions();
        }
        internal void SetInputSource(PlayerInputSource value)
        {
            if (InputSource == value) return;
            ClearInput();
            InputSource = value;
            source.SetLocalInput(value == PlayerInputSource.Local);
            blackboard.WeaponCancellationRequested = true;
            RefreshPermissions();
        }
        internal bool SubmitExternalIntent(PlayerInputFrame frame)
        {
            if (InputSource != PlayerInputSource.External || owner == null || !owner.isActiveAndEnabled || !owner.IsAlive)
                return false;
            if (!Finite(frame.Move) || !Finite(frame.Look) || !Finite(frame.Zoom)
                || frame.ViewAngles.HasValue && !Finite(frame.ViewAngles.Value)) return false;
            if (hasPendingExternalInput)
            {
                PlayerInputFrame previous = pendingExternalInput;
                // 连续包保留未消费的边沿，持续量取最新；新绝对角使旧角增量失效。
                frame = new PlayerInputFrame(frame.Move,
                    frame.ViewAngles.HasValue ? frame.Look : previous.Look + frame.Look,
                    previous.Zoom + frame.Zoom, frame.CrouchHeld, frame.AimHeld,
                    previous.FirePressed || frame.FirePressed, frame.FireHeld,
                    previous.ReloadPressed || frame.ReloadPressed,
                    previous.InteractPressed || frame.InteractPressed,
                    frame.ViewAngles ?? previous.ViewAngles);
                // 有限的单包相加仍可能溢出；失败时保留此前尚未消费的输入。
                if (!Finite(frame.Look) || !Finite(frame.Zoom)) return false;
            }
            // 无论调用在 Player.Update 前后，待消费输入都由下一次逻辑更新消费一次。
            pendingExternalInput = frame;
            hasPendingExternalInput = true;
            return true;
        }
        internal void BeginFrame()
        {
            if (InputSource == PlayerInputSource.Local)
            {
                ApplyInput(source.ConsumeSimulationInput(), source.LocalInputAllowed);
                return;
            }
            if (InputSource != PlayerInputSource.External || !hasPendingExternalInput) return;
            PlayerInputFrame frame = pendingExternalInput;
            hasPendingExternalInput = false;
            pendingExternalInput = default;
            ApplyInput(frame, true);
            if (frame.ViewAngles.HasValue)
                decisions.RecordView(Mathf.DeltaAngle(0f, frame.ViewAngles.Value.x),
                    Mathf.Clamp(frame.ViewAngles.Value.y, -89f, 89f));
        }
        internal void CompleteFrame()
        {
            blackboard.Look = Vector2.zero;
            blackboard.Zoom = 0f;
            blackboard.FirePressed = blackboard.ReloadPressed = blackboard.InteractPressed = false;
        }
        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private void ApplyInput(PlayerInputFrame frame, bool allowed)
        {
            blackboard.InputAllowed = allowed;
            blackboard.Move = allowed ? Vector2.ClampMagnitude(frame.Move, 1f) : Vector2.zero;
            blackboard.Look = allowed ? frame.Look : Vector2.zero;
            blackboard.Zoom = allowed ? frame.Zoom : 0f;
            blackboard.CrouchHeld = allowed && frame.CrouchHeld;
            blackboard.AimHeld = allowed && frame.AimHeld;
            blackboard.FirePressed = allowed && frame.FirePressed;
            blackboard.FireHeld = allowed && frame.FireHeld;
            blackboard.ReloadPressed = allowed && frame.ReloadPressed;
            blackboard.InteractPressed = allowed && frame.InteractPressed;
        }
        internal void ClearInput()
        {
            pendingExternalInput = default;
            hasPendingExternalInput = false;
            ApplyInput(default, false);
            source.ResetLocalInput();
            ReportInteraction(null, false, null);
        }
        private void ReportInteraction(MonoBehaviour target, bool visible, string prompt)
        {
            blackboard.InteractionTarget = target;
            blackboard.HasInteractionTarget = target != null;
            blackboard.InteractionVisible = visible;
            blackboard.InteractionPrompt = prompt;
        }
        private void ReportInteractionFeedback(string message, bool success)
            => blackboard.RecordEvent(PlayerEventKind.InteractionFeedback, owner.SimulationTime, message: message, success: success);
        internal void ResolveLocomotion(bool crouched, bool canStand) => locomotion.Resolve(crouched, canStand);
        internal void RecordPosture(bool crouched, bool canStand) => locomotion.RecordPosture(crouched, canStand);
        internal void RecordView(float yaw, float pitch) => decisions.RecordView(yaw, pitch);
        internal void ProcessLook(Vector2 pitchRange) => decisions.ProcessLook(pitchRange);
        internal void ApplyRecoil(float pitch, float yaw, Vector2 pitchRange)
            => decisions.ApplyRecoil(pitch, yaw, pitchRange);
        internal void RecordWeapon(UpperBodyDemand demand, WeaponActionSnapshot frame, float reloadPhase)
            => decisions.RecordWeapon(demand, frame, reloadPhase);
        internal void ClearWeaponInput()
        {
            blackboard.AimHeld = blackboard.FirePressed = blackboard.FireHeld = blackboard.ReloadPressed = false;
            if (hasPendingExternalInput)
                pendingExternalInput = new PlayerInputFrame(pendingExternalInput.Move, pendingExternalInput.Look,
                    pendingExternalInput.Zoom, pendingExternalInput.CrouchHeld,
                    interactPressed: pendingExternalInput.InteractPressed, viewAngles: pendingExternalInput.ViewAngles);
            source.ClearWeaponInput();
        }
        internal void AcknowledgeWeaponCancellation() => decisions.AcknowledgeWeaponCancellation();
        internal void RecordEvent(PlayerEventKind kind, ulong weaponId = 0, ulong shotSequence = 0, float amount = 0f,
            Vector3 direction = default, bool success = false, bool obstructed = false)
            => blackboard.RecordEvent(kind, owner.SimulationTime, weaponId, shotSequence, amount,
                success: success, direction: direction, obstructed: obstructed);
        internal void Publish(long tick)
        {
            blackboard.OwnerActive = owner != null && (owner.IsNetworkSimulation ? owner.isActiveAndEnabled : owner.SimulationActive);
            if (owner.HasSimulationAuthority)
            {
            blackboard.HurtUntil = owner != null ? owner.HurtUntil : 0d;
            blackboard.CurrentHealth = owner != null && owner.Attributes != null ? owner.Attributes.CurrentHealth : 0f;
            blackboard.MaxHealth = owner != null && owner.Attributes != null ? owner.Attributes.MaxHealth : 0f;
            var weapon = context.Weapon;
            blackboard.WeaponId = weapon != null ? weapon.ItemId.Value : 0;
            blackboard.ShotSequence = weapon != null ? weapon.LastShot.Sequence : 0;
            blackboard.ShotTime = weapon != null ? weapon.LastShot.Time : 0d;
            blackboard.ShotDuration = weapon != null ? weapon.Definition.ShotDuration : 0.01f;
            blackboard.FadeDuration = weapon != null ? weapon.Definition.FadeOutDuration : 0f;
            blackboard.ReloadDuration = weapon != null ? weapon.Definition.ReloadDuration : 1f;
            }
            blackboard.PlanarVelocity = context.World.PlanarVelocity;
            blackboard.BodyYaw = context.World.BodyYaw;
            blackboard.Publish(tick, owner.SimulationTime);
        }
    }
}
