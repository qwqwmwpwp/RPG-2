using RPG4.Input;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>双 HSM 宿主：上身输出需求，resolver 合并约束，下身消费计划。</summary>
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
        private LocomotionResolver resolver;
        private LowerBodyRootState lowerRoot;
        public PlayerInput Input => playerInput;
        public bool WeaponIKEnabled => isActiveAndEnabled && context != null && context.ShotPoseActive;

        internal bool CanControl => isActiveAndEnabled && context != null && context.CanControl;

        private void Awake()
        {
            if (playerInput == null) playerInput = GetComponent<PlayerInput>();
            stateData.Content.Initialize(this);
            context = new PlayerContext(playerInput, stateData);
            context.SetWeaponMode(false, false, false);
            upperStateMachine = new HierarchicalStateMachine();
            lowerStateMachine = new HierarchicalStateMachine();
            upperRoot = new UpperBodyRootState(upperStateMachine, context);
            resolver = new LocomotionResolver(context);
            upperStateMachine.Initialize(upperRoot);
            lowerRoot = new LowerBodyRootState(lowerStateMachine, context);
            lowerStateMachine.Initialize(lowerRoot);
        }
        private void OnEnable()
        {
            if (playerInput != null) playerInput.Clear();
        }
        private void Update()
        {
            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            upperRoot.PrepareFrame(deltaTime, Time.timeAsDouble);
            upperStateMachine.Tick(deltaTime);
            resolver.Resolve(lowerRoot.PhysicallyCrouched,
                !lowerRoot.PhysicallyCrouched || lowerRoot.CanStand());
            lowerStateMachine.Tick(deltaTime);
            upperRoot.SyncPresentation();
        }
        private void FixedUpdate()
        {
            if (Time.fixedDeltaTime > 0f) lowerStateMachine.FixedTick(Time.fixedDeltaTime);
        }
        private void LateUpdate()
        {
            if (context.CanUseWeapon && Time.deltaTime > 0f) return;
            CancelWeaponAction();
        }
        internal void CancelWeaponAction()
        {
            if (upperRoot == null) return;
            upperRoot.CancelWeaponAction();
            // 仅同步已激活的链；不准备本帧、不再次消费 fire/reload，也不启动停机状态。
            if (!isActiveAndEnabled || upperStateMachine.ActiveLeaf == null) return;
            upperStateMachine.Tick(0f);
            resolver.Resolve(lowerRoot.PhysicallyCrouched,
                !lowerRoot.PhysicallyCrouched || lowerRoot.CanStand());
            if (lowerStateMachine.ActiveLeaf != null) lowerStateMachine.Tick(0f);
            upperRoot.SyncPresentation();
        }
        internal void Stop()
        {
            if (upperRoot != null) upperRoot.CancelWeaponAction();
            if (upperStateMachine != null) upperStateMachine.Stop();
            if (lowerStateMachine != null) lowerStateMachine.Stop();
            if (playerInput != null) playerInput.Clear();
        }
        private void OnDisable() => Stop();
        private void OnApplicationFocus(bool focused) { if (!focused) Stop(); }
        private void OnApplicationPause(bool paused) { if (paused) Stop(); }
        private void OnValidate() => stateData?.Attack?.Validate();
    }
}
