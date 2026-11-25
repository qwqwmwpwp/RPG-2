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
        private bool frameActive;
        internal PlayerInputCoordinator Inputs { get; }
        private PlayerInputBuffer InputBuffer => Inputs.Buffer;
        internal PlayerIntentManager(Player owner, PlayerInput source, PlayerContext context)
        {
            this.owner = owner;
            this.source = source;
            this.context = context;
            InputSource = owner.InputSource;
            source.SetLocalInput(InputSource == PlayerInputSource.Local);
            blackboard = context.Blackboard;
            Inputs = new PlayerInputCoordinator(ReadState,
                () => owner != null && owner.isActiveAndEnabled && owner.IsAlive
                    && source != null && source.UsesLocalInput && source.LocalInputAllowed,
                ReportInteraction, ReportInteractionFeedback);
            locomotion = new LocomotionIntentProcessor(context, blackboard);
            decisions = new PlayerIntentProcessor(context, blackboard);
        }
        internal void Bind()
        {
            source.BindReadState(ReadState, Inputs);
            owner.GetComponent<RPG4.Interaction.PlayerInteraction>()?.BindInput(ReadState, Inputs);
            source.BindIntentManager(RefreshLocalInput);
        }
        internal void Dispose()
        {
            owner.GetComponent<RPG4.Interaction.PlayerInteraction>()?.BindInput(null, null);
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
            // Device permission changes affect future snapshots and cancellation, never current input.
            if (source.UsesLocalInput && !source.LocalWeaponInputAllowed)
                blackboard.WeaponCancellationRequested = true;
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
            return InputSource == PlayerInputSource.External && owner != null && owner.isActiveAndEnabled
                && owner.IsAlive && InputBuffer.Accumulate(frame);
        }
        internal PlayerInputFrame BeginLocalCycle(bool deferPublication)
        {
            InputBuffer.BeginCycle();
            if (!deferPublication) BeginFrame();
            return deferPublication
                ? new PlayerInputSnapshot(InputBuffer.Current, InputBuffer.Version,
                    InputBuffer.CurrentAllowed && owner.IsAlive && owner.isActiveAndEnabled).Frame
                : ReadState.Input.Snapshot.Frame;
        }
        internal void BeginFrame()
        {
            InputBuffer.BeginCycle();
            frameActive = true;
            ApplyInput(InputBuffer.Current, InputBuffer.CurrentAllowed && owner.IsAlive && owner.isActiveAndEnabled);
            blackboard.BeginCycle();
            context.BeginCycle();
            Inputs.Observe(ReadState.Input.Snapshot);
        }
        /// <summary>Network scheduling already selected this frame during its previous character cycle.</summary>
        internal void BeginPreparedFrame(PlayerInputFrame frame, bool replay = false)
        {
            // Owner packet generation already accepted a complete snapshot. Its network projection
            // omits local-only values and action edges, so prediction must retain that exact input.
            PlayerInputSnapshot? accepted = !replay && frameActive && InputBuffer.CycleActive
                && source.UsesLocalInput ? ReadState.Input.Snapshot : (PlayerInputSnapshot?)null;
            blackboard.EndCycle();
            context.EndCycle();
            if (replay)
            {
                // History already records the executed input; current device permission is irrelevant.
                blackboard.SetInput(frame, true, blackboard.Version);
                blackboard.BeginCycle();
                context.BeginCycle();
                return;
            }
            bool allowed = accepted?.InputAllowed ?? (owner.IsAlive && owner.isActiveAndEnabled
                && (!source.UsesLocalInput || (InputBuffer.CycleActive ? InputBuffer.CurrentAllowed : source.LocalInputAllowed)));
            if (accepted.HasValue) frame = accepted.Value.Frame;
            if (!InputBuffer.CycleActive) InputBuffer.Publish(frame, allowed);
            frameActive = true;
            ApplyInput(frame, allowed);
            blackboard.BeginCycle();
            context.BeginCycle();
            Inputs.Observe(ReadState.Input.Snapshot);
        }
        internal void CompleteFrame()
        {
            source.RefreshIntentPermissions();
            frameActive = false;
            blackboard.EndCycle();
            context.EndCycle();
            InputBuffer.CompleteCycle(owner.IsAlive && owner.isActiveAndEnabled
                && (!source.UsesLocalInput || source.LocalInputAllowed));
        }
        internal void CompleteSimulationFrame() { blackboard.EndCycle(); context.EndCycle(); }
        private void ApplyInput(PlayerInputFrame frame, bool allowed)
        {
            blackboard.SetInput(allowed ? frame : default, allowed, InputBuffer.Version);
            if (allowed && frame.ViewAngles.HasValue)
                decisions.RecordView(Mathf.DeltaAngle(0f, frame.ViewAngles.Value.x),
                    Mathf.Clamp(frame.ViewAngles.Value.y, -89f, 89f));
        }
        internal void ClearInput()
        {
            Inputs.ClearInput();
            source.ResetLocalInput();
            if (!frameActive) blackboard.SetInput(default, false, InputBuffer.Version);
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
        internal void ResolveLocomotion() => locomotion.Resolve();
        internal void SampleMotorPosture() => locomotion.SampleMotorPosture();
        internal void RecordView(float yaw, float pitch) => decisions.RecordView(yaw, pitch);
        internal void ProcessLook(Vector2 pitchRange) => decisions.ProcessLook(pitchRange);
        internal void ApplyRecoil(float pitch, float yaw, Vector2 pitchRange)
            => decisions.ApplyRecoil(pitch, yaw, pitchRange);
        internal void RecordWeapon(UpperBodyDemand demand, WeaponActionSnapshot frame, float reloadPhase)
            => decisions.RecordWeapon(demand, frame, reloadPhase);
        internal void ClearWeaponInput() => source.ClearWeaponInput();
        internal void AcknowledgeWeaponCancellation() => decisions.AcknowledgeWeaponCancellation();
        internal void RecordEvent(PlayerEventKind kind, ulong weaponId = 0, ulong shotSequence = 0, float amount = 0f,
            Vector3 direction = default, bool success = false, bool obstructed = false)
            => blackboard.RecordEvent(kind, owner.SimulationTime, weaponId, shotSequence, amount,
                success: success, direction: direction, obstructed: obstructed);
        internal void Publish(long tick, bool reuseUnchanged = false)
        {
            blackboard.OwnerActive = owner != null && (owner.IsNetworkSimulation ? owner.isActiveAndEnabled : owner.SimulationActive);
            if (owner.HasSimulationAuthority)
            {
                blackboard.Consumable = owner.Consumables != null ? owner.Consumables.State : default;
                blackboard.HurtUntil = owner != null ? owner.HurtUntil : 0d;
                blackboard.CurrentHealth = owner != null && owner.Attributes != null ? owner.Attributes.CurrentHealth : 0f;
                blackboard.MaxHealth = owner != null && owner.Attributes != null ? owner.Attributes.MaxHealth : 0f;
                var weapon = context.Equipment != null ? context.Equipment.CurrentWeapon : null;
                blackboard.WeaponId = weapon != null ? weapon.ItemId.Value : 0;
                blackboard.ShotSequence = weapon != null ? weapon.LastShot.Sequence : 0;
                blackboard.ShotTime = weapon != null ? weapon.LastShot.Time : 0d;
                blackboard.ShotDuration = weapon != null ? weapon.Definition.ShotDuration : 0.01f;
                blackboard.FadeDuration = weapon != null ? weapon.Definition.FadeOutDuration : 0f;
                blackboard.ReloadDuration = weapon != null ? weapon.Definition.ReloadDuration : 1f;
            }
            blackboard.PlanarVelocity = context.World.PlanarVelocity;
            blackboard.WorldPosition = context.World.CaptureMotor().Position;
            blackboard.BodyYaw = context.World.BodyYaw;
            blackboard.Publish(tick, owner.SimulationTime, reuseUnchanged);
        }
        internal void CommitPublication() => blackboard.CommitPending();
    }
}
