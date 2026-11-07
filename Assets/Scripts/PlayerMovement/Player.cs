using RPG4.Input;
using RPG4.CharacterAttributes;
using RPG4.PlayerIntent;
using RPG4.Backpack.Items;
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
    public sealed class Player : MonoBehaviour
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
        private bool networkSimulation, networkAuthority, networkLocalInput;
        private PlayerNetworkState confirmedNetworkState;
        private bool hasConfirmedNetworkState;
        private readonly PlayerLocalActionAnticipation localActions = new PlayerLocalActionAnticipation();
        public long SimulationTick => simulationTick;
        public double SimulationTime => simulationTime;
        public bool IsNetworkSimulation => networkSimulation;
        public bool HasSimulationAuthority => !networkSimulation || networkAuthority;
        public PlayerViewController LocalView => stateData.Content.ViewController;
        public PlayerEquipment Equipment => context?.Equipment;
        public PlayerConsumableUse Consumables { get; private set; }
        internal ConsumableActionSnapshot ConfirmedConsumableState => confirmedNetworkState.Consumable;
        public PlayerPublishedFrame PublishedFrame => intents?.Blackboard.Published;
        public Vector2 PreviewViewAngles => GetLocalRenderViewAngles();
        private readonly Vector2 pitchRange = new Vector2(-89f, 89f);
        public PlayerInput Input => playerInput;
        public CharacterAttributeComponent Attributes => attributes;
        public bool IsAlive => attributes != null && attributes.IsAlive;
        public bool IsHurt => IsAlive && SimulationTime < hurtUntil;
        public IPlayerReadState ReadState => context?.ReadState;
        public IPlayerMovementWorld MovementWorld => context?.World;
        public MovementRuleContext MovementConfiguration => PlayerMovementDemand.Configuration(stateData);
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
        public void ClearIntent() { localActions.Cancel(); intents?.ClearInput(); CancelWeaponAction(); }
        internal Ray GetAimRay() => context.World.GetAimRay(ReadState.View.ViewYaw, ReadState.View.ViewPitch);
        internal void GetShotOrigins(Vector3 direction, out Vector3 bodyOrigin, out Vector3 muzzleOrigin)
            => context.World.GetShotOrigins(direction, out bodyOrigin, out muzzleOrigin);
        public bool WeaponIKEnabled => presenter != null && presenter.WeaponIKEnabled;
        public bool PresentationEnabled
        {
            get => presentationEnabled;
            set { presentationEnabled = value; if (intents != null) RefreshPresenter(); }
        }
        internal PlayerPresenter Presenter => presenter;
        internal bool SimulationActive => isActiveAndEnabled && upperStateMachine?.ActiveLeaf != null
            && lowerStateMachine?.ActiveLeaf != null;
        internal double HurtUntil => hurtUntil;
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
            RefreshPresenter();
            intents.Bind();
            if (networkSimulation) ConfigureNetworkSimulation(networkAuthority, networkLocalInput);
        }
        private void RefreshPresenter()
        {
            if (presentationEnabled && presenter == null)
                presenter = new PlayerPresenter(transform, intents.Blackboard, stateData,
                    context.Equipment != null ? context.Equipment.WeaponRig : GetComponentInChildren<PlayerWeaponRig>(true), attributes);
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
            Consumables?.BindOffline();
            RefreshPresenter();
            presenter?.Present(Mathf.Max(0f, Time.deltaTime));
        }
        private void FixedUpdate()
        {
            if (networkSimulation) return;
            SimulateTick(++simulationTick, Time.fixedTimeAsDouble, Time.fixedDeltaTime);
        }
        private void SimulateTick(long tick, double now, float deltaTime, IPlayerNetworkMovementResolver resolver = null)
        {
            if (deltaTime <= 0f) return;
            simulationTick = tick;
            simulationTime = now;
            simulationInterrupted = false;
            simulating = true;
            try
            {
                intents.BeginFrame();
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
                ResolveLocomotion();
                PlayerMotorState startMotor = context.World.CaptureMotor();
                bool resetTurn = context.World.NeedsTurnReset(intents.Blackboard.Plan);
                lowerStateMachine.Tick(deltaTime);
                if (simulationInterrupted || !isActiveAndEnabled || !IsAlive) return;
                bool useAuthorityMotor = true;
                if (resolver != null)
                {
                    PlayerBlackboard board = intents.Blackboard;
                    var evaluation = new PlayerMovementEvaluation(startMotor,
                        new MovementRuleResult(board.Plan, board.WeaponDemand, board.IsAiming),
                        context.World.KinematicsSettings, board.ViewYaw, board.ViewPitch,
                        tick, now, deltaTime, resetTurn);
                    PlayerMovementCommit commit = resolver.Resolve(evaluation);
                    if (simulationInterrupted || !isActiveAndEnabled || !IsAlive) return;
                    useAuthorityMotor = commit.UseAuthorityMotor;
                    if (!useAuthorityMotor)
                    {
                        // State transitions already ran. Restoring after OnEnter preserves accepted turn velocity.
                        context.World.CommitMotor(commit.Motor, board.Plan);
                        intents.RecordPosture(commit.Motor.Crouched, board.CanStand);
                    }
                }
                if (useAuthorityMotor) lowerStateMachine.FixedTick(deltaTime);
            }
            finally
            {
                try
                {
                    if (!simulationInterrupted && !networkSimulation) Consumables?.CompleteDueUse(now);
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
        /// <summary>Network host owns scheduling and device ownership; offline hosts never call this.</summary>
        public void ConfigureNetworkSimulation(bool authoritative, bool localInput)
        {
            if (networkAuthority != authoritative || networkLocalInput != localInput)
            {
                ResetOwnerPresentation();
                hasConfirmedNetworkState = false;
            }
            networkSimulation = true;
            networkAuthority = authoritative;
            networkLocalInput = localInput;
            InputSource = PlayerInputSource.External;
            if (intents == null) return;
            context.World.ConfigureNetworkMotor(true);
            playerInput.SetNetworkLocalInput(localInput);
            ClearIntent();
        }
        /// <summary>Render-only look preview. It never consumes input or changes the simulation's angles.</summary>
        public Vector2 GetLocalRenderViewAngles()
        {
            PlayerPublishedFrame frame = PublishedFrame;
            Vector2 view = frame != null ? new Vector2(frame.ViewYaw, frame.ViewPitch) : Vector2.zero;
            if (IsAlive && isActiveAndEnabled && playerInput != null && playerInput.CanPreviewLocalWeaponInput
                && (!networkSimulation || networkLocalInput))
            {
                view += playerInput.UnconsumedLook;
                if (networkSimulation && !networkAuthority)
                    view += localActions.GetVisualRecoil(frame, Time.unscaledTimeAsDouble);
            }
            return new Vector2(Mathf.DeltaAngle(0f, view.x), Mathf.Clamp(view.y, -89f, 89f));
        }
        private bool CanAnticipateLocalActions => networkSimulation && !networkAuthority && networkLocalInput
            && IsAlive && isActiveAndEnabled && !IsHurt && playerInput != null && playerInput.CanPreviewLocalWeaponInput
            && PublishedFrame != null && PublishedFrame.CanUseWeapon && !PublishedFrame.Consumable.Active;
        public void UpdateLocalActionInput(PlayerInputFrame input, double now)
        {
            if (!CanAnticipateLocalActions) { localActions.Cancel(); return; }
            localActions.UpdateInput(input);
        }
        public void AnticipateLocalActions(PlayerInputFrame input, ulong requestId, double now)
        {
            if (!CanAnticipateLocalActions || Equipment == null
                || !Equipment.TryGetPresentationWeapon(out ulong weaponId, out WeaponDefinitionFragment definition)) return;
            localActions.Begin(input, requestId, now, weaponId, definition, PublishedFrame.Reloading,
                Equipment.CanAnticipateShot, Equipment.PresentationShotSequence);
        }
        public void ApplyLocalActionResult(ulong requestId, bool accepted, ulong weaponId, ulong shotSequence,
            double serverTime, uint ordinal = 0)
            => localActions.ApplyResult(requestId, accepted, weaponId, shotSequence, ordinal, Time.unscaledTimeAsDouble);
        public PlayerLocalWeaponPresentation GetLocalWeaponPresentation()
        {
            if (!CanAnticipateLocalActions || Equipment == null
                || !Equipment.TryGetPresentationWeapon(out ulong weaponId, out _))
            { localActions.Cancel(); return default; }
            return localActions.Get(PublishedFrame, stateData, Time.unscaledTimeAsDouble, weaponId,
                Equipment.CanAnticipateShot);
        }
        public bool TryDequeueAnticipatedShot(out PlayerAnticipatedShot shot)
        {
            if (!CanAnticipateLocalActions) { localActions.Cancel(); shot = default; return false; }
            return localActions.TryDequeue(Time.unscaledTimeAsDouble, out shot);
        }
        public void MarkAnticipatedShotPresented(PlayerAnticipatedShot shot) => localActions.MarkPresented(shot);
        public bool ShouldPresentConfirmedShot(ulong weaponId, ulong shotSequence)
            => localActions.ShouldPresentConfirmed(weaponId, shotSequence);
        public bool TryGetAnticipatedPresentationSequence(ulong weaponId, ulong serverShotSequence, out ulong cueSequence)
            => localActions.TryGetPresentationSequence(weaponId, serverShotSequence, out cueSequence);
        public bool ShouldDeferConfirmedShotPresentation(ulong weaponId, ulong serverShotSequence)
            => networkSimulation && !networkAuthority && networkLocalInput
                && localActions.ShouldDeferConfirmedShot(weaponId, serverShotSequence, Time.unscaledTimeAsDouble);
        public void BeginOwnerReconcile() => presenter?.BeforeOwnerReconcile();
        public void EndOwnerReconcile(bool discontinuity = false) => presenter?.AfterOwnerReconcile(discontinuity);
        public void ResetOwnerPresentation()
        {
            localActions.Reset();
            presenter?.ResetOwnerPresentation();
        }
        /// <summary>Server admission only; it does not begin actions or consume inventory.</summary>
        public bool CanAcceptNetworkAction(byte kind, ulong weaponId)
        {
            var weapon = Equipment?.CurrentWeapon;
            if (!networkAuthority || !IsAlive || !isActiveAndEnabled || IsHurt || weapon == null
                || weapon.ItemId.Value != weaponId || !Equipment.CanExecuteAuthority) return false;
            return kind == 2 ? !weapon.IsReloading : kind == 1 && !weapon.IsReloading && weapon.HasUsableRound;
        }
        public bool WasNetworkActionAccepted(byte kind)
        {
            var weapon = Equipment?.CurrentWeapon;
            if (!networkAuthority || !IsAlive || !isActiveAndEnabled || IsHurt || weapon == null) return false;
            return kind == 2 ? weapon.IsReloading : kind == 1 && (weapon.ShotStartedThisFrame
                || weapon.Definition.FireMode == WeaponFireMode.Automatic && ReadState.Input.FireHeld
                    && !weapon.IsReloading && weapon.HasUsableRound);
        }
        public void SimulateNetworkTick(PlayerInputFrame input, long tick, double now, float deltaTime)
            => SimulateNetworkTick(input, tick, now, deltaTime, null);
        public void SimulateNetworkTick(PlayerInputFrame input, long tick, double now, float deltaTime,
            IPlayerNetworkMovementResolver resolver)
        {
            if (!networkSimulation || !networkAuthority || !isActiveAndEnabled) return;
            SubmitExternalIntent(input);
            SimulateTick(tick, now, deltaTime, resolver);
        }
        public PlayerMotorState CaptureMotorState() => context.World.CaptureMotor();
        public void RestoreMotorState(PlayerMotorState state) => context.World.RestoreMotor(state);
        /// <summary>Reads the live business board, including changes made before this tick is published.</summary>
        public PlayerAuthoritySnapshot CaptureNetworkAuthorityState()
        {
            PlayerBlackboard board = intents.Blackboard;
            var weapon = context.Weapon;
            return new PlayerAuthoritySnapshot
            {
                Tick = simulationTick, Time = simulationTime, HurtUntil = hurtUntil,
                Alive = IsAlive, Health = attributes != null ? attributes.CurrentHealth : 0f,
                MaxHealth = attributes != null ? attributes.MaxHealth : 0f,
                WeaponAvailable = board.CanUseWeapon, Reloading = board.Reloading, ReloadPhase = board.ReloadPhase,
                WeaponId = weapon != null ? weapon.ItemId.Value : 0,
                ShotSequence = weapon != null ? weapon.LastShot.Sequence : 0,
                ShotTime = weapon != null ? weapon.LastShot.Time : 0d,
                ShotDuration = weapon != null ? weapon.Definition.ShotDuration : 0.01f,
                FadeDuration = weapon != null ? weapon.Definition.FadeOutDuration : 0f,
                ReloadDuration = weapon != null ? weapon.Definition.ReloadDuration : 1f,
                Action = board.WeaponAction, Consumable = Consumables != null ? Consumables.State : default
            };
        }
        /// <summary>Collision queries use the current world. This replays movement only, never weapon/health/events.</summary>
        public void PredictNetworkMovement(PlayerInputFrame input, long tick, double now, float deltaTime)
        {
            if (!networkSimulation || networkAuthority || deltaTime <= 0f) return;
            simulationTick = tick;
            simulationTime = now;
            PlayerBlackboard board = intents.Blackboard;
            Vector2 view = input.ViewAngles ?? new Vector2(board.ViewYaw + input.Look.x, board.ViewPitch + input.Look.y);
            intents.RecordView(Mathf.DeltaAngle(0f, view.x), Mathf.Clamp(view.y, -89f, 89f));
            MovementRuleResult rules = ResolvePredictedRules(input, now);
            if (rules.Plan.CanMove) context.World.ApplyPosture(rules.Plan.Crouched);
            intents.RecordPosture(context.World.PhysicallyCrouched,
                !context.World.PhysicallyCrouched || context.World.CanStand());
            context.World.Move(intents.Blackboard.Plan, rules.Plan.Strafe, deltaTime);
            intents.Publish(tick);
        }
        private MovementRuleResult ResolvePredictedRules(PlayerInputFrame input, double now)
        {
            PlayerBlackboard board = intents.Blackboard;
            MovementRuleContext rules = PlayerMovementDemand.Configuration(stateData);
            rules.Active = isActiveAndEnabled;
            rules.Alive = hasConfirmedNetworkState ? confirmedNetworkState.Alive : IsAlive;
            rules.CanUseWeapon = hasConfirmedNetworkState ? confirmedNetworkState.WeaponAvailable : board.CanUseWeapon;
            rules.IsHurt = hasConfirmedNetworkState ? now < confirmedNetworkState.HurtUntil : board.IsHurt;
            rules.PhysicallyCrouched = context.World.PhysicallyCrouched;
            rules.CanStand = !rules.PhysicallyCrouched || context.World.CanStand();
            rules.ViewYaw = board.ViewYaw;
            // Replays always derive business facts from authority, never from an earlier replay's demand.
            WeaponActionSnapshot action = hasConfirmedNetworkState ? confirmedNetworkState.Action : board.WeaponAction;
            rules.UsingConsumable = hasConfirmedNetworkState ? confirmedNetworkState.Consumable.Active : board.Consumable.Active;
            rules.Reloading = hasConfirmedNetworkState ? confirmedNetworkState.Reloading : board.Reloading;
            double elapsed = hasConfirmedNetworkState ? System.Math.Max(0d, now - confirmedNetworkState.ShotTime) : 0d;
            rules.ShotActive = action.ShotActive && (!hasConfirmedNetworkState || elapsed < confirmedNetworkState.ShotDuration);
            rules.FadingOut = (action.ShotActive || action.FadingOut) && !rules.ShotActive
                && (!hasConfirmedNetworkState || elapsed < confirmedNetworkState.ShotDuration + confirmedNetworkState.FadeDuration);
            rules.WaitingAutomatic = hasConfirmedNetworkState && confirmedNetworkState.Demand.AttackCamera
                && !action.ShotActive && !action.FadingOut && input.FireHeld && now - confirmedNetworkState.Time <= 1.5d;
            MovementRuleResult result = PlayerMovementRules.Evaluate(rules, input);
            bool canAct = rules.Active && rules.Alive && rules.CanUseWeapon && !rules.IsHurt;
            board.Plan = result.Plan;
            board.PhysicallyCrouched = rules.PhysicallyCrouched;
            board.CanStand = rules.CanStand;
            board.IsAlive = rules.Alive;
            board.IsHurt = rules.IsHurt;
            board.CanMove = result.Plan.CanMove;
            board.CanLook = rules.Active && rules.Alive;
            board.CanUseWeapon = canAct;
            board.CanInteract = rules.Active && rules.Alive;
            board.InputAllowed = result.Plan.CanMove;
            board.CrouchHeld = input.CrouchHeld;
            board.AimHeld = input.AimHeld;
            board.FireHeld = input.FireHeld;
            board.Move = Vector2.ClampMagnitude(input.Move, 1f);
            board.WeaponDemand = result.Demand;
            board.Reloading = result.Demand.Reloading;
            board.WeaponAction = new WeaponActionSnapshot(false, false, rules.ShotActive && canAct,
                rules.FadingOut && canAct, action.IsAutomatic && canAct && (rules.ShotActive || rules.FadingOut),
                rules.ShotActive && hasConfirmedNetworkState && confirmedNetworkState.ShotDuration > 0f
                    ? Mathf.Clamp01((float)(elapsed / confirmedNetworkState.ShotDuration)) : action.ShotPhase);
            board.IsAiming = result.Aiming;
            return result;
        }
        /// <summary>Installs business facts and re-evaluates permission without advancing weapons or motor.</summary>
        public void ApplyNetworkAuthorityState(in PlayerAuthoritySnapshot state)
        {
            if (!networkSimulation || networkAuthority || intents == null
                || hasConfirmedNetworkState && state.Tick < confirmedNetworkState.Tick) return;
            confirmedNetworkState.Tick = state.Tick;
            confirmedNetworkState.Time = state.Time;
            confirmedNetworkState.HurtUntil = state.HurtUntil;
            confirmedNetworkState.ShotTime = state.ShotTime;
            confirmedNetworkState.Health = state.Health;
            confirmedNetworkState.MaxHealth = state.MaxHealth;
            confirmedNetworkState.ReloadPhase = state.ReloadPhase;
            confirmedNetworkState.ShotDuration = state.ShotDuration;
            confirmedNetworkState.FadeDuration = state.FadeDuration;
            confirmedNetworkState.ReloadDuration = state.ReloadDuration;
            confirmedNetworkState.Alive = state.Alive;
            confirmedNetworkState.WeaponAvailable = state.WeaponAvailable;
            confirmedNetworkState.Reloading = state.Reloading;
            confirmedNetworkState.WeaponId = state.WeaponId;
            confirmedNetworkState.ShotSequence = state.ShotSequence;
            confirmedNetworkState.Action = state.Action;
            confirmedNetworkState.Consumable = state.Consumable;
            hasConfirmedNetworkState = true;
            PlayerBlackboard board = intents.Blackboard;
            board.Consumable = state.Consumable;
            board.CurrentHealth = state.Health;
            board.MaxHealth = state.MaxHealth;
            board.HurtUntil = state.HurtUntil;
            board.ReloadPhase = state.ReloadPhase;
            board.WeaponId = state.WeaponId;
            board.ShotSequence = state.ShotSequence;
            board.ShotTime = state.ShotTime;
            board.ShotDuration = state.ShotDuration;
            board.FadeDuration = state.FadeDuration;
            board.ReloadDuration = state.ReloadDuration;
            var input = new PlayerInputFrame(board.Move, crouchHeld: board.CrouchHeld,
                aimHeld: board.AimHeld, fireHeld: board.FireHeld);
            MovementRuleResult rules = ResolvePredictedRules(input, System.Math.Max(simulationTime, state.Time));
            confirmedNetworkState.Demand = rules.Demand;
            localActions.ObserveConfirmed(confirmedNetworkState, Time.unscaledTimeAsDouble);
            // Publish at the existing local simulation clock: this update has not simulated another step.
            board.Publish(simulationTick, simulationTime);
        }
        public void ApplyNetworkPresentation(PlayerNetworkState state)
        {
            if (!networkSimulation || networkAuthority) return;
            confirmedNetworkState = state;
            hasConfirmedNetworkState = true;
            localActions.ObserveConfirmed(state, Time.unscaledTimeAsDouble);
            simulationTick = state.Tick;
            simulationTime = state.Time;
            PlayerBlackboard board = intents.Blackboard;
            board.OwnerActive = isActiveAndEnabled;
            board.IsAlive = state.Alive;
            board.IsHurt = state.HurtUntil > state.Time;
            board.Consumable = state.Consumable;
            board.CurrentHealth = state.Health;
            board.MaxHealth = state.MaxHealth;
            board.HurtUntil = state.HurtUntil;
            board.ViewYaw = state.ViewYaw;
            board.ViewPitch = state.ViewPitch;
            board.BodyYaw = state.Motor.Yaw;
            board.PlanarVelocity = state.Motor.Velocity;
            board.PhysicallyCrouched = state.Motor.Crouched;
            board.CanStand = state.CanStand;
            board.CanMove = state.Alive;
            board.CanLook = state.Alive;
            board.CanUseWeapon = state.WeaponAvailable;
            board.CanInteract = state.Alive;
            board.IsAiming = state.Aiming;
            board.Reloading = state.Reloading;
            board.ReloadPhase = state.ReloadPhase;
            board.Plan = state.Plan;
            board.WeaponDemand = state.Demand;
            board.WeaponAction = state.Action;
            board.WeaponId = state.WeaponId;
            board.ShotSequence = state.ShotSequence;
            board.ShotTime = state.ShotTime;
            board.ShotDuration = state.ShotDuration;
            board.FadeDuration = state.FadeDuration;
            board.ReloadDuration = state.ReloadDuration;
            board.Publish(state.Tick, state.Time);
        }
        public void ApplyNetworkShot(ulong weaponId, ulong sequence, double time, bool automatic,
            bool damageApplied, bool obstructed)
        {
            if (!networkSimulation || networkAuthority) return;
            PlayerBlackboard board = intents.Blackboard;
            // Transport already applies the network-clock lifetime. Presentation ages this cue from
            // receipt on its own current clock, never by subtracting server time from predicted time.
            board.RecordEvent(PlayerEventKind.Shot, simulationTime, weaponId, sequence,
                success: damageApplied, obstructed: obstructed);
            board.Publish(simulationTick, simulationTime);
        }
        public void RefreshLocalPresentationBindings() => presenter?.RefreshHealthBindings(attributes);
        public void RefreshNetworkPublication() => intents?.Publish(simulationTick);
        public void CancelConsumableUse()
        {
            Consumables?.Cancel();
            if (!HasSimulationAuthority)
            {
                confirmedNetworkState.Consumable = default;
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
            ResolveLocomotion();
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
        // 位移由世界执行适配控制；不采用 Animator 根运动。
        private void OnAnimatorMove() { }
        private void LateUpdate()
        {
            if (networkSimulation) { presenter?.Present(0f); return; }
            intents.RefreshPermissions();
            // 固定模拟已处理常规中断；这里只同步动画求值后新增的取消请求，避免重复零时长求值 Rig。
            if (IsAlive && ReadState.Weapon.WeaponCancellationRequested)
                CancelWeaponAction();
            else PublishPresentation(0f);
        }
        private void ResolveLocomotion() => intents.ResolveLocomotion(lowerRoot.PhysicallyCrouched,
            !lowerRoot.PhysicallyCrouched || lowerRoot.CanStand());
        private void PublishPresentation(float deltaTime, bool reevaluate = false)
        {
            if (intents == null) return;
            intents.Publish(simulationTick);
            presenter?.Present(deltaTime, reevaluate);
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
                ResolveLocomotion();
                if (!simulationInterrupted && lowerStateMachine.ActiveLeaf != null) lowerStateMachine.Tick(0f);
            }
            PublishPresentation(0f, true);
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
                if (lowerRoot != null) ResolveLocomotion();
                consumablePublicationPending = false;
                PublishPresentation(0f, true);
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
                PublishPresentation(0f, true);
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
            ResolveLocomotion();
            PublishPresentation(0f, true);
        }
        private void OnDamaged(DamageInfo damage, float appliedAmount)
        {
            Vector3 direction = damage.Direction;
            if (direction.sqrMagnitude < 0.0001f && damage.Source != null)
                direction = transform.position - damage.Source.transform.position;
            intents.RecordEvent(PlayerEventKind.Hurt, amount: appliedAmount, direction: direction);
            if (damage.Kind == DamageKind.Periodic)
            { PublishPresentation(0f); return; }
            Consumables?.Cancel();
            if (!IsAlive || stateData.Hurt.Duration <= 0f)
            {
                PublishPresentation(0f);
                return;
            }
            hurtUntil = SimulationTime + stateData.Hurt.Duration;
            intents.RefreshPermissions();
            CancelWeaponAction();
        }
        private void OnDestroy()
        {
            Consumables?.Cancel();
            Consumables?.UnbindOffline();
            intents?.Dispose();
            presenter?.Dispose();
        }
        private void OnDisable()
        {
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
        private void OnValidate() => stateData?.Attack?.Validate();
    }
}
