using RPG4.Input;
using RPG4.CharacterAttributes;
using RPG4.PlayerIntent;
using UnityEngine;
using CharacterAttributeComponent = RPG4.CharacterAttributes.CharacterAttributes;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色组合与调度宿主；逻辑只写核心黑板，表现统一交给桥接层。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(150)]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider), typeof(PlayerInput))]
    [RequireComponent(typeof(CharacterAttributeComponent))]
    public sealed partial class Player : MonoBehaviour
    {
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private PlayerStateData stateData = new PlayerStateData();
        [SerializeField] private bool presentationEnabled = true;
        [SerializeField] private PlayerInputSource inputSource = PlayerInputSource.Local;
        private HierarchicalStateMachine upperStateMachine, lowerStateMachine;
        private PlayerContext context;
        private UpperBodyRootState upperRoot;
        private bool upperStateMachineBusy, consumablePublicationPending;
        private bool simulating, simulationInterrupted, stopRequested, stopping, weaponCancellationBusy;
        private LowerBodyRootState lowerRoot;
        private PlayerIntentManager intents;
        private PlayerPresenter presenter;
        private CharacterAttributeComponent attributes;
        private double hurtUntil;
        private long simulationTick;
        private double simulationTime;
        public long SimulationTick => simulationTick;
        public double SimulationTime => simulationTime;
        public bool IsNetworkSimulation => networkSimulation;
        public bool HasSimulationAuthority => !networkSimulation || networkAuthority;
        // 联机首轮只开放移动、持枪瞄准和库存事务；离线保留原有战斗规则。
        public CombatCapabilities Combat => networkSimulation ? CombatCapabilities.Online : CombatCapabilities.Offline;
        public bool CanExecuteCombat => Combat.WeaponActions;
        public const string NetworkCombatPausedMessage = "联机战斗暂未开放，射击、战斗换弹和消耗品使用已暂停。";
        public PlayerViewController LocalView => stateData.Content.ViewController;
        public PlayerEquipment Equipment => context?.Equipment;
        public PlayerLocalLoadout LocalLoadout { get; } = new PlayerLocalLoadout();
        public PlayerConsumableUse Consumables { get; private set; }
        internal ConsumableActionSnapshot ConfirmedConsumableState => confirmedAuthority.Consumable;
        public PlayerPublishedFrame PublishedFrame => intents?.Blackboard.Published;
        public Vector2 PreviewViewAngles => GetLocalRenderViewAngles();
        private readonly Vector2 pitchRange = new Vector2(-89f, 89f);
        public PlayerInput Input => playerInput;
        internal PlayerInputCoordinator InputEvents => intents.Inputs;
        public CharacterAttributeComponent Attributes => attributes;
        public bool IsAlive => attributes != null && attributes.IsAlive;
        public bool IsHurt => IsAlive && SimulationTime < hurtUntil;
        public IPlayerReadState ReadState => context?.ReadState;
        public IPlayerMovementWorld MovementWorld => context?.World;
        public MovementRuleContext MovementConfiguration => PlayerMovementDemand.Configuration(stateData);
        /// <summary>The frozen movement rules actually used by the most recent motor step.</summary>
        public MovementRuleResult ExecutedMovementRules { get; private set; }
        public PlayerInputSource InputSource
        {
            get => inputSource;
            set
            {
                if (inputSource == value) return;
                inputSource = value;
                intents?.SetInputSource(value);
                CancelWeaponAction();
                upperRoot?.BeginInputSession();
            }
        }
        /// <summary>供服务器/AI 提交设备无关意图；需先选择 External 来源。held 持续到下次提交或清理。</summary>
        public bool SubmitExternalIntent(PlayerInputFrame frame) => intents != null && intents.SubmitExternalIntent(frame);
        public void ClearIntent()
        {
            localActions.Cancel();
            intents?.ClearInput();
            if (!Combat.WeaponActions) Equipment?.DiscardPendingShots();
            CancelWeaponAction();
        }
        /// <summary>仅投影执行输入，不修改冻结快照、移动报告或序列回执。</summary>
        private PlayerInputFrame FilterCombatInput(PlayerInputFrame input)
        {
            var capabilities = Combat;
            if (capabilities.WeaponActions && capabilities.Consumables) return input;
            return new PlayerInputFrame(input.Move, input.Look, input.Zoom, input.CrouchHeld, input.AimHeld,
                capabilities.WeaponActions && input.FirePressed, capabilities.WeaponActions && input.FireHeld,
                capabilities.WeaponActions && input.ReloadPressed, input.InteractPressed, input.ViewAngles,
                capabilities.Consumables && input.QuickUsePressed);
        }
        internal Ray GetAimRay() => context.World.GetAimRay(ReadState.View.ViewYaw, ReadState.View.ViewPitch);
        internal void GetShotOrigins(Vector3 direction, out Vector3 bodyOrigin, out Vector3 muzzleOrigin)
            => context.World.GetShotOrigins(direction, out bodyOrigin, out muzzleOrigin);
        public bool PresentationEnabled
        {
            get => presentationEnabled;
            set { presentationEnabled = value; if (intents != null) RefreshPresenter(); }
        }
        internal PlayerPresenter Presenter => presenter;
        internal bool SimulationActive => isActiveAndEnabled && upperStateMachine?.ActiveLeaf != null
            && lowerStateMachine?.ActiveLeaf != null;
        internal double HurtUntil => hurtUntil;
        internal float HurtPresentationDuration => stateData.Hurt.Duration;
        internal bool SimulationInterrupted => simulationInterrupted;
        internal void ApplyShotRecoil(float pitch, float yaw) => intents.ApplyRecoil(pitch, yaw, pitchRange);
        internal void RecordShot(ulong weaponId, ulong sequence, bool damageApplied, bool obstructed)
            => intents.RecordEvent(PlayerEventKind.Shot, weaponId, sequence, success: damageApplied, obstructed: obstructed);

        private void Awake()
        {
            if (playerInput == null) playerInput = GetComponent<PlayerInput>();
            attributes = GetComponent<CharacterAttributeComponent>();
            stateData.Content.Initialize(this);
            simulationTime = Time.timeAsDouble;
            context = new PlayerContext(stateData) { Owner = this };
            context.Equipment = GetComponent<PlayerEquipment>();
            Consumables = new PlayerConsumableUse(this);
            context.World = new PlayerWorldAdapter(stateData);
            intents = new PlayerIntentManager(this, playerInput, context);
            context.Intents = intents;
            Vector2 angles = inputSource == PlayerInputSource.Local && playerInput.InitialViewAngles.HasValue
                ? playerInput.InitialViewAngles.Value : new Vector2(transform.eulerAngles.y, 0f);
            intents.RecordView(angles.x, angles.y);
            upperStateMachine = new HierarchicalStateMachine();
            lowerStateMachine = new HierarchicalStateMachine();
            upperRoot = new UpperBodyRootState(upperStateMachine, context);
            lowerRoot = new LowerBodyRootState(lowerStateMachine, context);
            upperStateMachine.Initialize(upperRoot);
            lowerStateMachine.Initialize(lowerRoot);
            intents.RefreshPermissions();
            intents.SampleMotorPosture();
            intents.Publish(simulationTick);
            intents.CommitPublication();
            RefreshPresenter();
            intents.Bind();
            if (networkSimulation) ConfigureNetworkSimulation(networkAuthority, networkLocalInput);
        }
        private void RefreshPresenter()
        {
            if (presentationEnabled && presenter == null)
                presenter = new PlayerPresenter(transform, intents.Blackboard, stateData, attributes);
            presenter?.SetEnabled(presentationEnabled);
        }
        private void OnEnable()
        {
            if (attributes != null)
            {
                attributes.Died += OnDied;
                attributes.Revived += OnRevived;
                attributes.Damaged += OnDamaged;
            }
            RefreshPresenter();
            intents?.ClearInput();
            if (!IsAlive) OnDied();
        }
        private void Update()
        {
            LocalLoadout.Refresh(this);
            Consumables?.UpdateRequestTimeout();
            ProcessLocalInputCommands();
            RefreshPresenter();
            presenter?.AdvanceFrame(Time.deltaTime);
        }
        private void ProcessLocalInputCommands()
        {
            if (intents == null || !intents.Inputs.ConsumeQuickUsePressed()) return;
            // Always consume within this render frame, even if ownership or loadout is not ready.
            if (!IsAlive || !LocalLoadout.Available) return;
            var item = Consumables?.ResolveQuickUse(out _);
            if (item != null) Consumables.RequestUse(item.ItemId);
        }
        private void FixedUpdate()
        {
            if (networkSimulation) return;
            SimulateTick(++simulationTick, Time.fixedTimeAsDouble, Time.fixedDeltaTime);
        }
        private void SimulateTick(long tick, double now, float deltaTime, IPlayerNetworkMovementResolver resolver = null,
            PlayerInputFrame? preparedInput = null)
        {
            if (deltaTime <= 0f) return;
            simulationTick = tick;
            simulationTime = now;
            simulationInterrupted = false;
            simulating = true;
            try
            {
                if (preparedInput.HasValue) intents.BeginPreparedFrame(preparedInput.Value);
                else intents.BeginFrame();
                intents.RefreshPermissions();
                if (simulationInterrupted || !isActiveAndEnabled) return;
                if (!IsAlive)
                {
                    if (networkSimulation) context.World.Move(default, false, deltaTime);
                    else context.World.Stop();
                    return;
                }
                intents.ProcessLook(pitchRange);
                TickUpperBody(deltaTime, true);
                if (simulationInterrupted || !isActiveAndEnabled || !IsAlive) return;
                context.World.BeginNetworkQueryTick(tick);
                if (resolver != null) Physics.SyncTransforms();
                intents.ResolveLocomotion();
                PlayerMotorState startMotor = context.World.CaptureMotor();
                LocomotionPlan executedPlan = ReadState.Locomotion.Plan;
                ExecutedMovementRules = new MovementRuleResult(executedPlan, ReadState.Weapon.WeaponDemand,
                    ReadState.Weapon.IsAiming);
                bool resetTurn = context.World.NeedsTurnReset(executedPlan);
                lowerStateMachine.Tick(deltaTime);
                if (simulationInterrupted || !isActiveAndEnabled || !IsAlive) return;
                bool useAuthorityMotor = true;
                if (resolver != null)
                {
                    var evaluation = new PlayerMovementEvaluation(startMotor,
                        new MovementRuleResult(executedPlan, ReadState.Weapon.WeaponDemand, ReadState.Weapon.IsAiming),
                        context.World.KinematicsSettings, ReadState.View.ViewYaw, ReadState.View.ViewPitch,
                        tick, now, deltaTime, resetTurn);
                    PlayerMovementCommit commit = resolver.Resolve(evaluation);
                    if (simulationInterrupted || !isActiveAndEnabled || !IsAlive) return;
                    useAuthorityMotor = commit.UseAuthorityMotor;
                    if (!useAuthorityMotor)
                    {
                        // State transitions already ran. Restoring after OnEnter preserves accepted turn velocity.
                        context.World.CommitMotor(commit.Motor, executedPlan);
                    }
                }
                if (useAuthorityMotor) lowerStateMachine.FixedTick(deltaTime);
                intents.SampleMotorPosture();
            }
            finally
            {
                try
                {
                    if (!simulationInterrupted && !networkSimulation && Combat.Consumables) Consumables?.CompleteDueUse(now);
                }
                finally
                {
                    simulating = false;
                    CompleteRequestedStop();
                    intents.Publish(simulationTick);
                    intents.CompleteFrame();
                }
            }
        }
        public void RefreshLocalPresentationBindings() => presenter?.RefreshHealthBindings(attributes);
        public void CancelConsumableUse()
        {
            Consumables?.Cancel();
            if (!HasSimulationAuthority)
            {
                confirmedAuthority.Consumable = default;
                if (intents != null) { intents.Blackboard.Consumable = default; intents.Publish(simulationTick); }
            }
        }
        internal void RefreshConsumablePublication()
        {
            if (intents == null) return;
            if (upperStateMachineBusy || weaponCancellationBusy)
            {
                consumablePublicationPending = true;
                return;
            }
            RefreshConsumablePublication(true);
        }
        private void RefreshConsumablePublication(bool synchronizeState)
        {
            if (synchronizeState)
            {
                upperRoot.PreparePublication();
                if (isActiveAndEnabled && upperStateMachine.ActiveLeaf != null) TickUpperBody(0f);
                else
                {
                    if (Consumables != null && Consumables.Active) upperRoot.CancelWeaponAction();
                    upperRoot.RecordInactiveDemand();
                }
            }
            intents.Blackboard.Consumable = Consumables != null ? Consumables.State : default;
            intents.ResolveLocomotion();
            intents.Publish(simulationTick);
        }
        private void TickUpperBody(float deltaTime, bool prepareFrame = false)
        {
            if (upperStateMachineBusy || stopping) return;
            upperStateMachineBusy = true;
            try
            {
                if (prepareFrame) upperRoot.PrepareFrame(deltaTime, simulationTime);
                if (!simulationInterrupted && !stopRequested && isActiveAndEnabled
                    && (!prepareFrame || IsAlive)) upperStateMachine.Tick(deltaTime);
            }
            finally
            {
                upperStateMachineBusy = false;
                if (stopRequested) CompleteRequestedStop();
                else if (consumablePublicationPending)
                {
                    consumablePublicationPending = false;
                    // 通常已切换完成；仅补同步 Tick 回调中新开始/结束的动作，保留刚接受的新武器输入。
                    RefreshConsumablePublication(upperRoot.UsingConsumable != upperRoot.ConsumableStateActive);
                }
            }
        }
        private void LateUpdate()
        {
            if (networkSimulation)
            {
                presenter?.RefreshPreparedFrame();
                presenter?.CommitFrame();
                intents.CommitPublication();
                return;
            }
            intents.RefreshPermissions();
            // 固定模拟已处理常规中断；这里只同步动画求值后新增的取消请求，避免重复零时长求值 Rig。
            if (IsAlive && ReadState.Weapon.WeaponCancellationRequested)
                CancelWeaponAction();
            else PublishPresentation();
            presenter?.CommitFrame();
            intents.CommitPublication();
        }
        private void PublishPresentation(bool reevaluate = false)
        {
            if (intents == null) return;
            intents.Publish(simulationTick);
            presenter?.RefreshPreparedFrame(reevaluate);
        }
        internal void CancelWeaponAction()
        {
            if (upperRoot == null || stopping || weaponCancellationBusy) return;
            weaponCancellationBusy = true;
            try { upperRoot.CancelWeaponAction(); }
            finally { weaponCancellationBusy = false; }
            if (stopRequested)
            {
                CompleteRequestedStop();
                return;
            }
            // 零时长同步不消费新输入，也不启动已经停机的状态机。
            if (!upperStateMachineBusy && !simulationInterrupted && isActiveAndEnabled && upperStateMachine.ActiveLeaf != null)
            {
                upperRoot.PreparePublication();
                TickUpperBody(0f);
                intents.ResolveLocomotion();
                if (!simulationInterrupted && lowerStateMachine.ActiveLeaf != null) lowerStateMachine.Tick(0f);
            }
            PublishPresentation(true);
        }
        internal void Stop()
        {
            simulationInterrupted = true;
            stopRequested = true;
            CompleteRequestedStop();
        }
        private void CompleteRequestedStop()
        {
            // Exit/CancelAction 的同步回调只能请求停机；等外层模拟或状态调用退栈后再退出状态链。
            if (!stopRequested || simulating || upperStateMachineBusy || weaponCancellationBusy || stopping) return;
            stopping = upperStateMachineBusy = true;
            try
            {
                Consumables?.Cancel();
                upperRoot?.CancelWeaponAction();
                upperStateMachine?.Stop();
                lowerStateMachine?.Stop();
                context?.World?.Stop();
                intents?.ClearInput();
                intents?.RefreshPermissions();
                upperRoot?.RecordInactiveDemand();
                if (lowerRoot != null) intents.ResolveLocomotion();
                consumablePublicationPending = false;
                PublishPresentation(true);
                // Disable/death may have no subsequent Update/LateUpdate. The stopped motor and
                // revoked pending state close here, unless a containing simulation owns the boundary.
                intents?.CommitPublication();
                stopRequested = false;
            }
            finally { stopping = upperStateMachineBusy = false; }
        }
        private void OnDied()
        {
            Consumables?.Cancel();
            ResetOwnerPresentation();
            hurtUntil = 0d;
            intents?.RecordEvent(PlayerEventKind.Died);
            intents?.RefreshPermissions();
            if (networkSimulation && !networkAuthority)
            {
                // Replicated health may arrive independently of the movement baseline.
                // Its death/UI events revoke input, but cannot rewind or clear the predicted motor.
                PublishPresentation(true);
                return;
            }
            Stop();
        }
        private void OnRevived()
        {
            ResetOwnerPresentation();
            hurtUntil = 0d;
            intents.RecordEvent(PlayerEventKind.Revived);
            // 持续按住开火/瞄准时，不自动恢复动作。
            intents?.ClearInput();
            intents.RefreshPermissions();
            intents.ResolveLocomotion();
            PublishPresentation(true);
        }
        private void OnDamaged(DamageInfo damage, float appliedAmount)
        {
            Vector3 direction = damage.Direction;
            if (direction.sqrMagnitude < 0.0001f && damage.Source != null)
                direction = transform.position - damage.Source.transform.position;
            intents.RecordEvent(PlayerEventKind.Hurt, amount: appliedAmount, direction: direction);
            if (damage.Kind == DamageKind.Periodic)
            { PublishPresentation(); return; }
            Consumables?.Cancel();
            if (!IsAlive || stateData.Hurt.Duration <= 0f)
            {
                PublishPresentation();
                return;
            }
            hurtUntil = SimulationTime + stateData.Hurt.Duration;
            intents.RefreshPermissions();
            CancelWeaponAction();
        }
        private void OnDestroy()
        {
            Consumables?.Cancel();
            Consumables?.ClearNetworkRequest();
            intents?.Dispose();
            presenter?.Dispose();
        }
        private void OnDisable()
        {
            LocalLoadout.SetAvailable(false);
            ResetOwnerPresentation();
            if (attributes != null)
            {
                attributes.Died -= OnDied;
                attributes.Revived -= OnRevived;
                attributes.Damaged -= OnDamaged;
            }
            hurtUntil = 0d;
            Stop();
            presenter?.SetEnabled(false);
        }
        private void OnApplicationFocus(bool focused) { if (!focused && !networkSimulation && inputSource == PlayerInputSource.Local) Stop(); }
        private void OnApplicationPause(bool paused) { if (paused && !networkSimulation && inputSource == PlayerInputSource.Local) Stop(); }
    }
}
