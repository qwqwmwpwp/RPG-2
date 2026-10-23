using RPG4.Input;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色状态机宿主，通过 Unity 生命周期维护状态机的运行。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider), typeof(PlayerInput))]
    public sealed class Player : MonoBehaviour
    {
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private PlayerStateData stateData = new PlayerStateData();

        private HierarchicalStateMachine stateMachine;

        public PlayerInput Input => playerInput;

        private void Awake()
        {
            if (playerInput == null) playerInput = GetComponent<PlayerInput>();
            stateData.Content.Initialize(this);

            var context = new PlayerContext(playerInput, stateData.Content, stateData.Exploration);
            stateMachine = new HierarchicalStateMachine();
            stateMachine.Initialize(new ExplorationState(stateMachine, context));
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;

            stateMachine.Tick(deltaTime);
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
            if (stateMachine != null) stateMachine.Stop();
            if (playerInput != null) playerInput.Clear();
        }
    }
}
