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
        private HierarchicalStateMachine upperStateMachine, lowerStateMachine;
        private PlayerContext context;
        private UpperBodyRootState upperRoot;
        private LowerBodyRootState lowerRoot;
        private PlayerIntentManager intents;
        private PlayerPresenter presenter;
        private CharacterAttributeComponent attributes;
        private double hurtUntil;
        private long simulationTick;
        private Vector2 lookSensitivity = new Vector2(0.15f, 0.12f);
        private Vector2 pitchRange = new Vector2(-35f, 65f);
        private float aimSensitivityMultiplier = 0.65f;
        public PlayerInput Input => playerInput;
        public CharacterAttributeComponent Attributes => attributes;
        public bool IsAlive => attributes != null && attributes.IsAlive;
        public bool IsHurt => IsAlive && Time.timeAsDouble < hurtUntil;
        public IPlayerReadState ReadState => playerInput != null ? playerInput.ReadState : null;
        public bool WeaponIKEnabled => presenter != null && presenter.WeaponIKEnabled;
        public bool PresentationEnabled
        {
            get => presentationEnabled;
            set { presentationEnabled = value; presenter?.SetEnabled(value); }
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
            context = new PlayerContext(playerInput.ReadState, stateData);
            context.Equipment = GetComponent<PlayerEquipment>();
            context.World = new PlayerWorldAdapter(stateData);
            intents = new PlayerIntentManager(this, playerInput, context);
            context.Intents = intents;
            PlayerViewController view = stateData.Content.ViewController;
            if (view != null)
            {
                lookSensitivity = view.LookSensitivity;
                pitchRange = view.PitchRange;
                aimSensitivityMultiplier = view.AimSensitivityMultiplier;
                Vector2 angles = view.InitialAngles;
                intents.RecordView(angles.x, angles.y);
            }
            else intents.RecordView(transform.eulerAngles.y, 0f);
            upperStateMachine = new HierarchicalStateMachine();
            lowerStateMachine = new HierarchicalStateMachine();
            upperRoot = new UpperBodyRootState(upperStateMachine, context);
            lowerRoot = new LowerBodyRootState(lowerStateMachine, context);
            upperStateMachine.Initialize(upperRoot);
            lowerStateMachine.Initialize(lowerRoot);
            presenter = new PlayerPresenter(transform, intents.Blackboard, stateData,
                context.Equipment != null ? context.Equipment.WeaponRig : GetComponent<PlayerWeaponRig>(), attributes);
            presenter.SetEnabled(presentationEnabled);
            intents.Bind();
        }
        private void OnEnable()
        {
            if (attributes != null)
            {
                attributes.Died += OnDied;
                attributes.Revived += OnRevived;
                attributes.Damaged += OnDamaged;
            }
            presenter?.SetEnabled(presentationEnabled);
            playerInput?.Clear();
            if (!IsAlive) OnDied();
        }
        private void Update()
        {
            presenter.SetEnabled(presentationEnabled);
            intents.RefreshPermissions();
            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            simulationTick++;
            try
            {
                if (!IsAlive) return;
                intents.ProcessLook(lookSensitivity, pitchRange, aimSensitivityMultiplier);
                upperRoot.PrepareFrame(deltaTime, Time.timeAsDouble);
                if (!IsAlive) return;
                upperStateMachine.Tick(deltaTime);
                if (!IsAlive) return;
                ResolveLocomotion();
                lowerStateMachine.Tick(deltaTime);
            }
            finally { PublishPresentation(deltaTime); }
        }
        private void FixedUpdate()
        {
            intents.RefreshPermissions();
            if (!IsAlive) context.World.Stop();
            else if (Time.fixedDeltaTime > 0f) lowerStateMachine.FixedTick(Time.fixedDeltaTime);
            intents.Publish(simulationTick);
        }
        // 位移由世界执行适配控制；不采用 Animator 根运动。
        private void OnAnimatorMove() { }
        private void LateUpdate()
        {
            intents.RefreshPermissions();
            // Update 已处理常规中断；这里只同步动画求值后新增的取消请求，避免重复零时长求值 Rig。
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
            playerInput?.Clear();
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
            playerInput?.Clear();
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
            hurtUntil = Time.timeAsDouble + stateData.Hurt.Duration;
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
        private void OnApplicationFocus(bool focused) { if (!focused) Stop(); }
        private void OnApplicationPause(bool paused) { if (paused) Stop(); }
        private void OnValidate() => stateData?.Attack?.Validate();
    }
}
