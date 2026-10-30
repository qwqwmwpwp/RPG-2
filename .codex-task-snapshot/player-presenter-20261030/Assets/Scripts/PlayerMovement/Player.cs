using RPG4.Input;
using RPG4.CharacterAttributes;
using RPG4.PlayerIntent;
using UnityEngine;
using CharacterAttributeComponent = RPG4.CharacterAttributes.CharacterAttributes;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>双 HSM 宿主：意图管理器组织共享黑板，上身输出需求，下身执行计划。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(150)]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider), typeof(PlayerInput))]
    [RequireComponent(typeof(CharacterAttributeComponent))]
    public sealed class Player : MonoBehaviour
    {
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private PlayerStateData stateData = new PlayerStateData();
        private HierarchicalStateMachine upperStateMachine;
        private HierarchicalStateMachine lowerStateMachine;
        private PlayerContext context;
        private UpperBodyRootState upperRoot;
        private PlayerIntentManager intents;
        private LowerBodyRootState lowerRoot;
        private CharacterAttributeComponent attributes;
        private double hurtUntil;
        private int hurtLayer = -1;
        private static readonly int HurtAnimation = Animator.StringToHash("Damage Reaction.Hit");
        private static readonly int DeathAnimation = Animator.StringToHash("Base Layer.Dead");
        public PlayerInput Input => playerInput;
        public CharacterAttributeComponent Attributes => attributes;
        public bool IsAlive => attributes != null && attributes.IsAlive;
        public bool IsHurt => IsAlive && Time.timeAsDouble < hurtUntil;
        public IPlayerReadState ReadState => playerInput != null ? playerInput.ReadState : null;
        public bool WeaponIKEnabled => ReadState != null && ReadState.Pose.WeaponIKEnabled;
        internal PlayerIntentManager Intents => intents;
        internal void ApplyShotRecoil(float pitch, float yaw)
            => stateData.Content.ViewController?.ApplyRecoil(pitch, yaw);

        private void Awake()
        {
            if (playerInput == null) playerInput = GetComponent<PlayerInput>();
            attributes = GetComponent<CharacterAttributeComponent>();
            context = new PlayerContext(playerInput.ReadState, stateData);
            context.Equipment = GetComponent<PlayerEquipment>();
            intents = new PlayerIntentManager(this, playerInput, context);
            context.Intents = intents;
            stateData.Content.Initialize(this);
            if (stateData.Content.Animator != null)
                hurtLayer = stateData.Content.Animator.GetLayerIndex("Damage Reaction");
            upperStateMachine = new HierarchicalStateMachine();
            lowerStateMachine = new HierarchicalStateMachine();
            upperRoot = new UpperBodyRootState(upperStateMachine, context);
            upperStateMachine.Initialize(upperRoot);
            lowerRoot = new LowerBodyRootState(lowerStateMachine, context);
            lowerStateMachine.Initialize(lowerRoot);
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
            if (playerInput != null) playerInput.Clear();
            if (!IsAlive) OnDied();
        }
        private void Update()
        {
            UpdateHurtPresentation();
            intents.RefreshPermissions();
            if (!IsAlive) return;
            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            upperRoot.PrepareFrame(deltaTime, Time.timeAsDouble);
            if (!IsAlive) return;
            upperStateMachine.Tick(deltaTime);
            if (!IsAlive) return;
            intents.ResolveLocomotion(lowerRoot.PhysicallyCrouched,
                !lowerRoot.PhysicallyCrouched || lowerRoot.CanStand());
            lowerStateMachine.Tick(deltaTime);
            ApplyIntentPresentation();
            upperRoot.SyncPresentation();
        }
        private void FixedUpdate()
        {
            intents.RefreshPermissions();
            if (!IsAlive)
            {
                ClearPlanarMotion();
                return;
            }
            if (Time.fixedDeltaTime > 0f) lowerStateMachine.FixedTick(Time.fixedDeltaTime);
        }
        // 角色位移与转向由下身状态机驱动刚体；显式接管 Animator / Rig 的根运动输出。
        private void OnAnimatorMove() { }
        private void LateUpdate()
        {
            intents.RefreshPermissions();
            if (!IsAlive) return;
            if (context.CanUseWeapon && Time.deltaTime > 0f && !ReadState.Weapon.WeaponCancellationRequested) return;
            bool hadPresentation = HasWeaponPresentation();
            CancelWeaponAction();
            if (hadPresentation) stateData.Content.AimPose?.Reevaluate();
        }
        internal void CancelWeaponAction()
        {
            if (upperRoot == null) return;
            upperRoot.CancelWeaponAction();
            ApplyIntentPresentation();
            // 仅同步已激活的链；不准备本帧、不再次消费 fire/reload，也不启动停机状态。
            if (!isActiveAndEnabled || upperStateMachine.ActiveLeaf == null) return;
            upperStateMachine.Tick(0f);
            intents.ResolveLocomotion(lowerRoot.PhysicallyCrouched,
                !lowerRoot.PhysicallyCrouched || lowerRoot.CanStand());
            if (lowerStateMachine.ActiveLeaf != null) lowerStateMachine.Tick(0f);
            ApplyIntentPresentation();
            upperRoot.SyncPresentation();
        }
        internal void Stop()
        {
            bool hadPresentation = HasWeaponPresentation();
            if (upperRoot != null) upperRoot.CancelWeaponAction();
            if (upperStateMachine != null) upperStateMachine.Stop();
            if (lowerStateMachine != null) lowerStateMachine.Stop();
            ClearPlanarMotion();
            if (playerInput != null) playerInput.Clear();
            ApplyIntentPresentation();
            upperRoot?.ApplyLayerWeight();
            if (hadPresentation) stateData.Content.AimPose?.Reevaluate();
        }
        private void ClearPlanarMotion()
        {
            Rigidbody body = stateData.Content.Body;
            if (body == null || body.isKinematic) return;
            body.velocity = new Vector3(0f, body.velocity.y, 0f);
            body.angularVelocity = Vector3.zero;
        }
        private void OnDied()
        {
            ClearHurt();
            intents?.RefreshPermissions();
            Stop();
            Animator animator = stateData.Content.Animator;
            if (context.AnimatorIsAvailable && animator.HasState(0, DeathAnimation))
                animator.CrossFadeInFixedTime(DeathAnimation, stateData.Hurt.TransitionDuration, 0, 0f);
        }
        private void OnRevived()
        {
            ClearHurt();
            stateData.Content.Animator?.gameObject.SendMessage("SwitchSocketByString",
                "To_Hand_R_Socket,IK_ON_Left_Handle", SendMessageOptions.DontRequireReceiver);
            // 恢复许可前清空按键会话，持续按住开火/瞄准不会自动恢复动作。
            if (playerInput != null) playerInput.Clear();
        }
        private void OnDamaged(DamageInfo damage, float appliedAmount)
        {
            if (!IsAlive || stateData.Hurt.Duration <= 0f) return;
            hurtUntil = Time.timeAsDouble + stateData.Hurt.Duration;
            intents.RefreshPermissions();
            CancelWeaponAction();
            Animator animator = stateData.Content.Animator;
            if (!context.AnimatorIsAvailable || hurtLayer < 0 || !animator.HasState(hurtLayer, HurtAnimation)) return;
            animator.SetLayerWeight(hurtLayer, 1f);
            animator.CrossFadeInFixedTime(HurtAnimation, stateData.Hurt.TransitionDuration, hurtLayer, 0f);
        }
        private void UpdateHurtPresentation()
        {
            Animator animator = stateData.Content.Animator;
            if (animator == null || hurtLayer < 0) return;
            float remaining = Mathf.Max(0f, (float)(hurtUntil - Time.timeAsDouble));
            float fade = stateData.Hurt.TransitionDuration;
            animator.SetLayerWeight(hurtLayer, fade > 0f ? Mathf.Clamp01(remaining / fade) : remaining > 0f ? 1f : 0f);
        }
        private void ClearHurt()
        {
            hurtUntil = 0d;
            Animator animator = stateData.Content.Animator;
            if (animator != null && hurtLayer >= 0) animator.SetLayerWeight(hurtLayer, 0f);
        }
        private bool HasWeaponPresentation()
        {
            Animator animator = stateData.Content.Animator;
            return stateData.Content.AimPose != null && stateData.Content.AimPose.HasActivePose
                || animator != null && animator.layerCount > 1 && animator.GetLayerWeight(1) > 0f;
        }
        private void ApplyIntentPresentation()
        {
            if (stateData.Content.AimPose != null) stateData.Content.AimPose.ApplyPresentation();
        }
        private void OnDestroy() => intents?.Dispose();
        private void OnDisable()
        {
            if (attributes != null)
            {
                attributes.Died -= OnDied;
                attributes.Revived -= OnRevived;
                attributes.Damaged -= OnDamaged;
            }
            ClearHurt();
            Stop();
        }
        private void OnApplicationFocus(bool focused) { if (!focused) Stop(); }
        private void OnApplicationPause(bool paused) { if (paused) Stop(); }
        private void OnValidate() => stateData?.Attack?.Validate();
    }
}
