using System.Collections.Generic;
using Mirror;
using RPG4.Raid.Application;
using RPG4.Raid.Domain;
using UnityEngine;

namespace RPG4.AI
{
    /// <summary>
    /// Scene adapter for the match-level AI request. The match domain owns the
    /// count and timing, while this component owns Unity prefab instantiation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ServerMatchAiSpawner : MonoBehaviour
    {
        [SerializeField] private GameObject aiPrefab;
        [SerializeField] private Transform[] spawnPoints;
        [SerializeField, Min(1)] private int fallbackSpawnCount = 30;

        private readonly List<GameObject> spawned = new List<GameObject>();
        private ServerMatchRuntime runtime;

        private void Start()
        {
            TryBindRuntime();
        }

        private void Update()
        {
            if (runtime == null) TryBindRuntime();
        }

        private void TryBindRuntime()
        {
            if (runtime != null || !NetworkServer.active) return;
            ServerMatchRuntime candidate = ServerMatchRuntimeLocator.Current;
            if (candidate == null) return;
            runtime = candidate;
            runtime.AiSpawnRequested += Spawn;
            runtime.Changed += OnMatchChanged;
        }

        private void OnDestroy()
        {
            if (runtime == null) return;
            runtime.AiSpawnRequested -= Spawn;
            runtime.Changed -= OnMatchChanged;
            DespawnAll();
        }

        private void Spawn(int maximum, int decisionIntervalMilliseconds)
        {
            // Combat/AI spawning remains disabled during the role loadout migration.
        }

        private void OnMatchChanged(MatchRunEvent value)
        {
            if (value.Kind == MatchRunEventKind.Completed || value.Kind == MatchRunEventKind.Cancelled)
                DespawnAll();
        }

        private void DespawnAll()
        {
            for (int index = spawned.Count - 1; index >= 0; index--)
            {
                GameObject instance = spawned[index];
                if (instance == null) continue;
                if (NetworkServer.active && instance.TryGetComponent(out NetworkIdentity identity) && identity.netId != 0)
                    NetworkServer.Destroy(instance);
                else Destroy(instance);
            }
            spawned.Clear();
        }
    }
}
