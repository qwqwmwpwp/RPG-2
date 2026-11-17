using Mirror;
using RPG4.LoadoutTemplate;
using RPG4.Networking.Contracts;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking
{
    /// <summary>Initializes the authenticated identity and this raid's configured role loadout.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GameplaySceneBindings))]
    public sealed class OnlineSpawnConfiguration : MonoBehaviour, IServerGameplaySpawnConfiguration, IServerPlayerSpawnInitializer
    {
        public const uint Version = 1;
        public bool ValidateServerConfiguration(out string reason)
        {
            reason = isActiveAndEnabled ? null : "Trusted online spawn configuration is disabled.";
            return reason == null;
        }
        public bool TryInitializeBeforeSpawn(GameObject playerObject, TrustedPlayerSpawnContext context, out string reason)
        {
            reason = "Trusted role loadout initialization failed.";
            if (!NetworkServer.active || playerObject == null || context.Identity == null
                || playerObject.scene != gameObject.scene || context.Identity.SpawnConfigVersion != Version
                || context.Bindings == null || context.Bindings.TrustedSpawnConfiguration != this
                || context.Bindings.gameObject.scene != gameObject.scene || context.SpawnPoint == null
                || context.SpawnPoint.gameObject.scene != gameObject.scene
                || !(NetworkManager.singleton is AuthoritativeNetworkManager manager)
                || manager.ServerWorldGeneration != context.ServerWorldGeneration
                || !ValidateServerConfiguration(out reason)) return false;
            reason = "Trusted role loadout initialization failed.";
            if (!playerObject.TryGetComponent(out NetworkIdentity networkIdentity) || networkIdentity.netId != 0
                || !playerObject.TryGetComponent(out OnlinePlayerIdentity identity)
                || !playerObject.TryGetComponent(out Player player) || !player.isActiveAndEnabled
                || !playerObject.TryGetComponent(out NetworkRoleLoadout loadout) || !loadout.isActiveAndEnabled
                || context.Reservation == null || context.Reservation.AccountId != context.Identity.AccountId
                || context.Reservation.CharacterId != context.Identity.CharacterId) return false;
            if (!identity.InitializeBeforeSpawn(context.Identity, context.ServerWorldGeneration)) return false;
            return loadout.InitializeServer(out reason);
        }
    }
}
