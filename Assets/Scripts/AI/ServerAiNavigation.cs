using UnityEngine;
using UnityEngine.AI;
using Mirror;

namespace RPG4.AI
{
    /// <summary>
    /// Navigation output consumed by a future server-authoritative motor.
    /// The adapter never writes the owner's Transform.  The NavMeshAgent is only
    /// used to calculate a path and a desired velocity.
    /// </summary>
    public readonly struct ServerAiNavigationIntent
    {
        public readonly bool HasPath;
        public readonly bool ShouldMove;
        public readonly Vector3 DesiredVelocity;
        public readonly Vector3 DesiredDirection;
        public readonly float DesiredSpeed;
        public readonly float RemainingDistance;

        public ServerAiNavigationIntent(bool hasPath, bool shouldMove, Vector3 desiredVelocity,
            Vector3 desiredDirection, float desiredSpeed, float remainingDistance)
        {
            HasPath = hasPath;
            ShouldMove = shouldMove;
            DesiredVelocity = desiredVelocity;
            DesiredDirection = desiredDirection;
            DesiredSpeed = desiredSpeed;
            RemainingDistance = remainingDistance;
        }

        public static ServerAiNavigationIntent Empty => new ServerAiNavigationIntent(
            false, false, Vector3.zero, Vector3.zero, 0f, float.PositiveInfinity);
    }

    /// <summary>
    /// Server-only NavMesh adapter.  It calculates movement and turn intent,
    /// leaving position and rotation ownership to a character motor.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ServerAiNavigation : MonoBehaviour
    {
        [SerializeField] private NavMeshAgent agent;
        [SerializeField, Min(0f)] private float maxSpeed = 3.5f;
        [SerializeField, Min(0f)] private float acceleration = 8f;
        [SerializeField, Min(0f)] private float stoppingDistance = 0.5f;
        [SerializeField, Min(0f)] private float turnSpeed = 720f;

        private ServerAiNavigationIntent currentIntent = ServerAiNavigationIntent.Empty;
        private Vector3 destination;
        private bool hasDestination;

        public ServerAiNavigationIntent CurrentIntent => currentIntent;
        public NavMeshAgent Agent => agent;
        public float TurnSpeed => turnSpeed;
        public bool HasDestination => hasDestination;

        private void Awake()
        {
            EnsureAgent();
        }

        private void OnEnable()
        {
            EnsureAgent();
            currentIntent = ServerAiNavigationIntent.Empty;
        }

        private void OnDisable()
        {
            if (agent != null && agent.enabled && NetworkServer.active && agent.isOnNavMesh)
                agent.ResetPath();
            currentIntent = ServerAiNavigationIntent.Empty;
            hasDestination = false;
        }

        private void Update()
        {
            if (!NetworkServer.active) return;
            TickServer(Time.deltaTime);
        }

        /// <summary>Sets a server path target; it does not move the GameObject.</summary>
        public bool SetDestination(Vector3 worldPosition)
        {
            if (!NetworkServer.active || !EnsureAgent() || !agent.isOnNavMesh)
                return false;

            SyncAgentPosition();
            destination = worldPosition;
            hasDestination = agent.SetDestination(worldPosition);
            return hasDestination;
        }

        /// <summary>Stops path calculation and clears the published movement intent.</summary>
        public void ClearDestination()
        {
            if (!NetworkServer.active) return;
            hasDestination = false;
            if (EnsureAgent() && agent.isOnNavMesh) agent.ResetPath();
            currentIntent = ServerAiNavigationIntent.Empty;
        }

        /// <summary>
        /// Samples the agent's path.  Calling this more than once per frame is
        /// safe and still never writes Transform.position or Transform.rotation.
        /// </summary>
        public ServerAiNavigationIntent TickServer(float deltaTime)
        {
            if (!NetworkServer.active || !EnsureAgent() || !agent.isOnNavMesh)
            {
                currentIntent = ServerAiNavigationIntent.Empty;
                return currentIntent;
            }

            SyncAgentPosition();
            if (!hasDestination || !agent.hasPath)
            {
                currentIntent = ServerAiNavigationIntent.Empty;
                return currentIntent;
            }

            Vector3 velocity = agent.desiredVelocity;
            Vector3 planarVelocity = Vector3.ProjectOnPlane(velocity, Vector3.up);
            float speed = planarVelocity.magnitude;
            Vector3 direction = speed > 0.0001f ? planarVelocity / speed : Vector3.zero;
            float remaining = agent.remainingDistance;
            bool shouldMove = remaining > Mathf.Max(stoppingDistance, agent.stoppingDistance)
                && speed > 0.0001f;

            currentIntent = new ServerAiNavigationIntent(agent.hasPath, shouldMove, planarVelocity,
                direction, speed, remaining);
            return currentIntent;
        }

        private bool EnsureAgent()
        {
            if (agent == null) agent = GetComponent<NavMeshAgent>();
            if (agent == null) return false;

            // The agent is a path planner.  A future motor owns the Transform.
            agent.updatePosition = false;
            agent.updateRotation = false;
            if (maxSpeed > 0f) agent.speed = maxSpeed;
            if (acceleration > 0f) agent.acceleration = acceleration;
            agent.stoppingDistance = Mathf.Max(0f, stoppingDistance);
            return true;
        }

        private void SyncAgentPosition()
        {
            // This writes only NavMeshAgent.nextPosition, not the owner's Transform.
            if (agent != null && agent.enabled && agent.isOnNavMesh)
                agent.nextPosition = transform.position;
        }
    }
}
