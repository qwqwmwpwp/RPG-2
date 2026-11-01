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
    public sealed class Player : MonoBehaviour
    {
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private PlayerStateData stateData = new PlayerStateData();
        [SerializeField] private bool presentationEnabled = true;
        [SerializeField] private PlayerInputSource inputSource = PlayerInputSource.Local;
        private HierarchicalStateMachine upperStateMachine, lowerStateMachine;
        private PlayerContext context;
        private UpperBodyRootState upperRoot;
        private LowerBodyRootState lowerRoot;
        private PlayerIntentManager intents;
        private PlayerPresenter presenter;
        private CharacterAttributeComponent attributes;
        private double hurtUntil;
        private long simulationTick;
        private double simulationTime;
        private bool networkSimulation, networkAuthority, networkLocalInput;
        public long SimulationTick => simulationTick;
        public double SimulationTime => simulationTime;
        public bool IsNetworkSimulation => networkSimulation;
        public bool HasSimulationAuthority => !networkSimulation || networkAuthority;
        public PlayerViewController LocalView => stateData.Content.ViewController;
        public PlayerEquipment Equipment => context?.Equipment;
        public PlayerPublishedFrame PublishedFrame => intents?.Blackboard.Published;
        private readonly Vector2 pitchRange = new Vector2(-89f, 89f);
        public PlayerInput Input => playerInput;
        public CharacterAttributeComponent Attributes => attributes;
        public bool IsAlive => attributes != null && attributes.IsAlive;
        public bool IsHurt => IsAlive && SimulationTime < hurtUntil;
        public IPlayerReadState ReadState => context?.ReadState;
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
        public void ClearIntent() { intents?.ClearInput(); CancelWeaponAction(); }
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
                    context.Equipment != null ? context.Equipment.WeaponRig : GetComponent<PlayerWeaponRig>(), attributes);
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
            RefreshPresenter();
            presenter?.Present(Mathf.Max(0f, Time.deltaTime));
        }
        private void FixedUpdate()
        {
            if (networkSimulation) return;
            SimulateTick(++simulationTick, Time.fixedTimeAsDouble, Time.fixedDeltaTime);
        }
        private void SimulateTick(long tick, double now, float deltaTime)
        {
            if (deltaTime <= 0f) return;
            simulationTick = tick;
            simulationTime = now;
            intents.BeginFrame();
            intents.RefreshPermissions();
            try
            {
                if (!IsAlive)
                {
                    if (networkSimulation) context.World.Move(default, false, deltaTime);
                    else context.World.Stop();
                    return;
                }
                intents.ProcessLook(pitchRange);
                upperRoot.PrepareFrame(deltaTime, now);
                if (!IsAlive) return;
                upperStateMachine.Tick(deltaTime);
                if (!IsAlive) return;
                ResolveLocomotion();
                lowerStateMachine.Tick(deltaTime);
                lowerStateMachine.FixedTick(deltaTime);
            }
            finally
            {
                intents.Publish(simulationTick);
                intents.CompleteFrame();
            }
        }
        /// <summary>Network host owns scheduling and device ownership; offline hosts never call this.</summary>
        public void ConfigureNetworkSimulation(bool authoritative, bool localInput)
        {
            networkSimulation = true;
            networkAuthority = authoritative;
            networkLocalInput = localInput;
            InputSource = PlayerInputSource.External;
            if (intents == null) return;
            context.World.ConfigureNetworkMotor(true);
            playerInput.SetNetworkLocalInput(localInput);
            ClearIntent();
        }
        public void SimulateNetworkTick(PlayerInputFrame input, long tick, double now, float deltaTime)
        {
            if (!networkSimulation || !networkAuthority || !isActiveAndEnabled) return;
            SubmitExternalIntent(input);
            SimulateTick(tick, now, deltaTime);
        }
        public PlayerMotorState CaptureMotorState() => context.World.CaptureMotor();
        public void RestoreMotorState(PlayerMotorState state) => context.World.RestoreMotor(state);
        /// <summary>Collision queries use the current world. This replays movement only, never weapon/health/events.</summary>
        public void PredictNetworkMovement(PlayerInputFrame input, long tick, double now, float deltaTime)
        {
            if (!networkSimulation || networkAuthority || deltaTime <= 0f) return;
            simulationTick = tick;
            simulationTime = now;
            PlayerBlackboard board = intents.Blackboard;
            Vector2 view = input.ViewAngles ?? new Vector2(board.ViewYaw + input.Look.x, board.ViewPitch + input.Look.y);
            intents.RecordView(Mathf.DeltaAngle(0f, view.x), Mathf.Clamp(view.y, -89f, 89f));
            bool canMove = IsAlive && isActiveAndEnabled;
            bool crouch = canMove ? input.CrouchHeld : context.World.PhysicallyCrouched;
            if (canMove) context.World.ApplyPosture(crouch);
            crouch = context.World.PhysicallyCrouched;
            bool aiming = input.AimHeld && board.CanUseWeapon && !board.IsHurt;
            bool strafe = aiming || board.Reloading || board.WeaponAction.ShotActive;
            float speed = board.Reloading ? stateData.Reload.MoveSpeed : aiming
                ? stateData.Aim.MoveSpeed : board.WeaponAction.ShotActive || board.WeaponAction.FadingOut
                    ? stateData.HipFire.MoveSpeed : stateData.Exploration.MoveSpeed;
            if (crouch) speed = Mathf.Min(speed, stateData.Crouch.MoveSpeed);
            LocomotionPlan plan = new LocomotionPlan(canMove, crouch, strafe,
                canMove ? Vector2.ClampMagnitude(input.Move, 1f) : Vector2.zero, board.ViewYaw, speed, 1f);
            board.Plan = plan;
            board.PhysicallyCrouched = crouch;
            board.CanStand = !crouch || context.World.CanStand();
            board.CanMove = canMove;
            board.CanLook = IsAlive;
            board.InputAllowed = canMove;
            board.CrouchHeld = input.CrouchHeld;
            board.Move = plan.Move;
            board.WeaponDemand = new UpperBodyDemand(strafe, aiming, false, board.Reloading, speed, 1f);
            board.IsAiming = aiming;
            context.World.Move(plan, strafe, deltaTime);
            intents.Publish(tick);
        }
        public void ApplyNetworkPresentation(PlayerNetworkState state)
        {
            if (!networkSimulation || networkAuthority) return;
            simulationTick = state.Tick;
            simulationTime = state.Time;
            PlayerBlackboard board = intents.Blackboard;
            board.OwnerActive = isActiveAndEnabled;
            board.IsAlive = state.Alive;
            board.IsHurt = state.HurtUntil > state.Time;
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
            board.RecordEvent(PlayerEventKind.Shot, time, weaponId, sequence,
                success: damageApplied, obstructed: obstructed);
            board.ShotSequence = sequence;
            board.WeaponId = weaponId;
            board.ShotTime = time;
            board.WeaponAction = new WeaponActionSnapshot(true, false, true, false, automatic, 0f);
            board.Publish(simulationTick, simulationTime);
        }
        public void RefreshLocalPresentationBindings() => presenter?.RefreshHealthBindings(attributes);
        public void RefreshNetworkPublication() => intents?.Publish(simulationTick);
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
            if (upperRoot == null) return;
            upperRoot.CancelWeaponAction();
            // 零时长同步不消费新输入，也不启动已经停机的状态机。
            if (isActiveAndEnabled && upperStateMachine.ActiveLeaf != null)
            {
                upperStateMachine.Tick(0f);
                ResolveLocomotion();
                if (lowerStateMachine.ActiveLeaf != null) lowerStateMachine.Tick(0f);
            }
            PublishPresentation(0f, true);
        }
        internal void Stop()
        {
            upperRoot?.CancelWeaponAction();
            upperStateMachine?.Stop();
            lowerStateMachine?.Stop();
            context?.World?.Stop();
            intents?.ClearInput();
            intents?.RefreshPermissions();
            if (lowerRoot != null) ResolveLocomotion();
            PublishPresentation(0f, true);
        }
        private void OnDied()
        {
            hurtUntil = 0d;
            intents?.RecordEvent(PlayerEventKind.Died);
            intents?.RefreshPermissions();
            Stop();
        }
        private void OnRevived()
        {
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
            intents?.Dispose();
            presenter?.Dispose();
        }
        private void OnDisable()
        {
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
