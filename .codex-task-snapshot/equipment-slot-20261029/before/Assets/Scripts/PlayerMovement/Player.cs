using RPG4.Input;
using RPG4.PlayerIntent;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>双 HSM 宿主：意图管理器组织共享黑板，上身输出需求，下身执行计划。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(150)]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider), typeof(PlayerInput))]
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
        public PlayerInput Input => playerInput;
        public PlayerBlackboard Blackboard => playerInput != null ? playerInput.Blackboard : null;
        public bool WeaponIKEnabled => Blackboard != null && Blackboard.WeaponIKEnabled;
        internal PlayerIntentManager Intents => intents;

        private void Awake()
        {
            if (playerInput == null) playerInput = GetComponent<PlayerInput>();
            context = new PlayerContext(playerInput.Blackboard, stateData);
            context.Equipment = GetComponent<PlayerEquipment>();
            intents = new PlayerIntentManager(this, playerInput, context);
            context.Intents = intents;
            stateData.Content.Initialize(this);
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
            if (playerInput != null) playerInput.Clear();
        }
        private void Update()
        {
            intents.RefreshPermissions();
            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            upperRoot.PrepareFrame(deltaTime, Time.timeAsDouble);
            upperStateMachine.Tick(deltaTime);
            intents.ResolveLocomotion(lowerRoot.PhysicallyCrouched,
                !lowerRoot.PhysicallyCrouched || lowerRoot.CanStand());
            lowerStateMachine.Tick(deltaTime);
            ApplyIntentPresentation();
            upperRoot.SyncPresentation();
        }
        private void FixedUpdate()
        {
            intents.RefreshPermissions();
            if (Time.fixedDeltaTime > 0f) lowerStateMachine.FixedTick(Time.fixedDeltaTime);
        }
        // 角色位移与转向由下身状态机驱动刚体；显式接管 Animator / Rig 的根运动输出。
        private void OnAnimatorMove() { }
        private void LateUpdate()
        {
            intents.RefreshPermissions();
            if (context.CanUseWeapon && Time.deltaTime > 0f && !Blackboard.WeaponCancellationRequested) return;
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
            if (playerInput != null) playerInput.Clear();
            ApplyIntentPresentation();
            upperRoot?.ApplyLayerWeight();
            if (hadPresentation) stateData.Content.AimPose?.Reevaluate();
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
        private void OnDisable() => Stop();
        private void OnApplicationFocus(bool focused) { if (!focused) Stop(); }
        private void OnApplicationPause(bool paused) { if (paused) Stop(); }
        private void OnValidate() => stateData?.Attack?.Validate();
    }
}
