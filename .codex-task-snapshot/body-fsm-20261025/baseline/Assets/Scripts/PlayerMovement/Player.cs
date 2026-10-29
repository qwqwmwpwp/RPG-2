using RPG4.Input;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色状态机宿主，通过 Unity 生命周期维护状态机的运行。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(150)]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider), typeof(PlayerInput))]
    public sealed class Player : MonoBehaviour
    {
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private PlayerStateData stateData = new PlayerStateData();

        private HierarchicalStateMachine stateMachine;
        private PlayerContext context;
        private PlayerRootState root;

        public PlayerInput Input => playerInput;

        /// <summary>仅在可操作的实际射击动作中启用武器手部 IK，包含末发与淡出收尾。</summary>
        public bool WeaponIKEnabled => isActiveAndEnabled && context != null
            && context.ShotPoseActive;

        private void Awake()
        {
            if (playerInput == null) playerInput = GetComponent<PlayerInput>();
            stateData.Content.Initialize(this);

            context = new PlayerContext(playerInput, stateData.Content, stateData.Exploration,
                stateData.Attack, stateData.Aim, stateData.HipFire);
            context.SetWeaponMode(false, false);
            stateMachine = new HierarchicalStateMachine();
            root = new PlayerRootState(stateMachine, context);
            stateMachine.Initialize(root);
        }

        private void Update()
        {
            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            // HSM 检查转换前，由状态统一准备本帧动作和模式。
            root.PrepareFrame(deltaTime, Time.timeAsDouble);
            stateMachine.Tick(deltaTime);
        }

        private void OnEnable()
        {
            // Input 可在 Player 禁用期间继续采样；恢复时重新要求松开再按。
            if (playerInput != null) playerInput.Clear();
        }

        private void LateUpdate()
        {
            // 交互窗口可在 LateUpdate 打开；只取消和同步模式，不再消费本帧开火。
            if (context.CanUseWeapon && Time.deltaTime > 0f) return;
            CancelWeaponAction();
        }

        private void FixedUpdate()
        {
            float deltaTime = Time.fixedDeltaTime;
            if (deltaTime <= 0f) return;

            stateMachine.FixedTick(deltaTime);
        }

        private void OnDisable()
        {
            Stop();
        }

        internal void Stop()
        {
            if (root != null) root.CancelWeaponAction();
            if (stateMachine != null) stateMachine.Stop();
            if (playerInput != null) playerInput.Clear();
        }

        internal void CancelWeaponAction()
        {
            if (root != null) root.CancelWeaponAction();
            if (context == null) return;
            // 只同步已活动的 HSM；清理不能重新启动停机或尚未启动的状态链。
            if (isActiveAndEnabled && stateMachine != null && stateMachine.ActiveLeaf != null)
                stateMachine.Tick(0f);
            else context.SetWeaponMode(false, false);
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) Stop();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) Stop();
        }

        private void OnValidate()
        {
            stateData?.Attack?.Validate();
        }
    }
}
