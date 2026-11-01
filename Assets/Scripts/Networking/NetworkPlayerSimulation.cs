using System.Collections.Generic;
using Mirror;
using RPG4.Combat;
using UnityEngine;

namespace RPG4.Networking
{
    /// <summary>One simulation budget per Unity fixed step, independent of packet or player count.</summary>
    public static class NetworkPlayerSimulation
    {
        private static readonly List<NetworkPlayerController> players = new List<NetworkPlayerController>();
        private static readonly List<NetworkPlayerController> stepPlayers = new List<NetworkPlayerController>();
        private static readonly List<Collider> targets = new List<Collider>();
        private static readonly ServerCombatHistory history = new ServerCombatHistory();
        private static Collider[] extraTargets;
        private static double lastFixedTime = double.NegativeInfinity;
        private static bool playerOrderDirty;
        public static long Tick { get; private set; }
        public static double Time { get; private set; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Reset();
        public static void SetAdditionalCombatTargets(Collider[] colliders)
        {
            if (ReferenceEquals(extraTargets, colliders)) return;
            extraTargets = colliders;
            // Unloading must drop old collider references even without another player FixedStep.
            // Keep player registration and the simulation clock intact.
            targets.Clear();
            history.Clear();
        }
        internal static void Register(NetworkPlayerController player)
        { if (!players.Contains(player)) { players.Add(player); playerOrderDirty = true; } }
        internal static void Unregister(NetworkPlayerController player)
        {
            if (players.Remove(player)) playerOrderDirty = true;
            if (player != null) history.Remove(player.gameObject);
        }
        public static void Reset()
        {
            players.Clear(); stepPlayers.Clear(); targets.Clear(); history.Clear(); extraTargets = null;
            Tick = 0; Time = 0d; lastFixedTime = double.NegativeInfinity; playerOrderDirty = false;
        }
        internal static void FixedStep()
        {
            double fixedTime = UnityEngine.Time.fixedTimeAsDouble;
            if (fixedTime == lastFixedTime || UnityEngine.Time.fixedDeltaTime <= 0f) return;
            lastFixedTime = fixedTime;
            float deltaTime = UnityEngine.Time.fixedDeltaTime;
            Tick++;
            // The clock uses simulation time, never the wall clock supplied by a client packet.
            Time = fixedTime;
            if (playerOrderDirty)
            {
                players.Sort((a, b) => (a != null ? a.netId : 0u).CompareTo(b != null ? b.netId : 0u));
                playerOrderDirty = false;
            }
            stepPlayers.Clear();
            foreach (NetworkPlayerController player in players)
                if (player != null && player.isActiveAndEnabled) stepPlayers.Add(player);
            foreach (NetworkPlayerController player in stepPlayers) player.SampleOwner(Tick, deltaTime);
            if (!NetworkServer.active) return;
            foreach (NetworkPlayerController player in stepPlayers) player.DrainOperations();
            foreach (NetworkPlayerController player in stepPlayers) player.MoveServer(Tick, Time, deltaTime);
            Physics.SyncTransforms();
            targets.Clear();
            foreach (NetworkPlayerController player in stepPlayers)
            {
                Collider capsule = player.GetComponent<CapsuleCollider>();
                if (capsule != null && player.isServer) targets.Add(capsule);
            }
            if (extraTargets != null)
                foreach (Collider target in extraTargets) if (target != null && !targets.Contains(target)) targets.Add(target);
            history.Capture(Time, targets);
            // All accepted shots have already consumed ammunition. Later deaths cannot revoke them.
            foreach (NetworkPlayerController player in stepPlayers) player.FlushCombat(history, Tick, Time, deltaTime);
            foreach (NetworkPlayerController player in stepPlayers) player.PublishServer(Tick);
        }
    }
}
