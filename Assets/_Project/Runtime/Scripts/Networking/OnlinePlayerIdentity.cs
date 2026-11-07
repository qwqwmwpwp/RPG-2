using Mirror;
using RPG4.Networking.Contracts;
using UnityEngine;

namespace RPG4.Networking
{
    /// <summary>Only the trusted pre-spawn path writes this identity. Account/session ownership stays on the server.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkIdentity))]
    public sealed class OnlinePlayerIdentity : NetworkBehaviour
    {
        [SyncVar] private ulong characterId;
        [SyncVar] private string displayName;
        [SyncVar] private uint spawnConfigVersion;
        public ulong CharacterId => characterId;
        public string DisplayName => displayName ?? string.Empty;
        public uint SpawnConfigVersion => spawnConfigVersion;
        public bool HasInitialProfile => isActiveAndEnabled && characterId != 0
            && spawnConfigVersion == OnlineSpawnConfiguration.Version
            && EntryProtocol.IsUtf8(displayName, 1, EntryProtocol.MaxDisplayNameBytes);
        public AuthenticatedIdentity ServerIdentity { get; private set; }
        public long ServerWorldGeneration { get; private set; }

        internal bool InitializeBeforeSpawn(AuthenticatedIdentity identity, long worldGeneration)
        {
            if (!NetworkServer.active || netId != 0 || identity == null || ServerIdentity != null
                || identity.SpawnConfigVersion != OnlineSpawnConfiguration.Version || worldGeneration <= 0) return false;
            ServerIdentity = identity;
            ServerWorldGeneration = worldGeneration;
            characterId = identity.CharacterId;
            displayName = identity.DisplayName;
            spawnConfigVersion = identity.SpawnConfigVersion;
            return true;
        }

        public override void OnStopServer()
        {
            ClearProfile();
            base.OnStopServer();
        }

        public override void OnStopClient()
        {
            if (!isServer) ClearProfile();
            base.OnStopClient();
        }

        internal bool MatchesServerIdentity(AuthenticatedIdentity identity, long generation) => identity != null && HasInitialProfile
            && ReferenceEquals(ServerIdentity, identity) && ServerWorldGeneration == generation
            && characterId == identity.CharacterId && displayName == identity.DisplayName && spawnConfigVersion == identity.SpawnConfigVersion;

        private void ClearProfile()
        {
            ServerIdentity = null; ServerWorldGeneration = 0;
            characterId = 0; displayName = string.Empty; spawnConfigVersion = 0;
        }
    }
}
