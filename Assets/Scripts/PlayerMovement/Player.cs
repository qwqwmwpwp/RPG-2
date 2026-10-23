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
        private PlayerShootSimulation shooting;

        public PlayerInput Input => playerInput;

        private void Awake()
        {
            if (playerInput == null) playerInput = GetComponent<PlayerInput>();
            stateData.Content.Initialize(this);

            shooting = GetComponent<PlayerShootSimulation>();
            if (shooting != null) shooting.Initialize(this, stateData.Content.Animator);
            context = new PlayerContext(playerInput, stateData.Content, stateData.Exploration,
                stateData.Aim, stateData.HipFire, shooting);
            stateMachine = new HierarchicalStateMachine();
            stateMachine.Initialize(new PlayerRootState(stateMachine, context));
        }

        private void Update()
        {
            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            // 明确的输入→射击请求→模式→动画顺序，不依赖独立组件的 Update 顺序。
            if (shooting != null) shooting.PrepareInput(context.CanUseWeapon, deltaTime);
            stateMachine.Tick(deltaTime);
            if (shooting != null) shooting.TickAnimation();
        }

        private void OnEnable()
        {
            // Input 可在 Player 禁用期间继续采样；恢复时重新要求松开再按。
            if (playerInput != null) playerInput.Clear();
        }

        private void LateUpdate()
        {
            // 交互窗口可在 LateUpdate 打开；补充取消后续射击并恢复持枪姿态。
            if (context.CanUseWeapon) return;
            if (shooting != null) shooting.Stop();
            context.SetWeaponMode(false, false);
            if (!context.CanControl) context.SetPlanarVelocity(Vector3.zero);
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
            if (shooting != null) shooting.Stop();
            if (stateMachine != null) stateMachine.Stop();
            if (playerInput != null) playerInput.Clear();
        }
    }
}
