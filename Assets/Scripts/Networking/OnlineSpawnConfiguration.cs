using System;
using Mirror;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking
{
    /// <summary>Shared authored v1 loadout. Runs synchronously after Awake and before Mirror publishes the player.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GameplaySceneBindings))]
    public sealed class OnlineSpawnConfiguration : MonoBehaviour, IServerGameplaySpawnConfiguration, IServerPlayerSpawnInitializer
    {
        public const uint Version = 1;
        [Serializable]
        private sealed class InventorySeed
        {
            public ItemManifest Manifest;
            [Min(1)] public int Count = 1;
        }
        [SerializeField] private ItemManifestCatalog catalog;
        [SerializeField] private ItemManifest initialWeapon;
        [SerializeField] private InventorySeed[] inventoryItems = Array.Empty<InventorySeed>();

        public bool ValidateServerConfiguration(out string reason)
        {
            if (!isActiveAndEnabled || catalog == null || initialWeapon == null
                || initialWeapon.GetFragment<WeaponDefinitionFragment>() == null
                || initialWeapon.GetFragment<EquipmentFragment>() == null || inventoryItems == null)
            { reason = "Trusted online loadout is incomplete."; return false; }
            try
            {
                if (!ValidateManifest(initialWeapon, 1))
                { reason = "Initial weapon is not a valid catalog entry."; return false; }
                foreach (InventorySeed seed in inventoryItems)
                    if (seed == null || !ValidateManifest(seed.Manifest, seed.Count))
                    { reason = "An inventory seed is invalid or absent from the catalog."; return false; }
            }
            catch (Exception)
            { reason = "Trusted online loadout validation failed."; return false; }
            reason = null;
            return true;
        }

        private bool ValidateManifest(ItemManifest manifest, int count)
        {
            if (manifest == null || count < 1 || !catalog.TryGet(manifest.ItemType, out ItemManifest registered)
                || registered != manifest) return false;
            manifest.ValidateConfiguration();
            return count <= (manifest.GetFragment<StackableFragment>()?.MaxStackSize ?? 1);
        }

        public bool TryInitializeBeforeSpawn(GameObject playerObject, TrustedPlayerSpawnContext context, out string reason)
        {
            reason = "Trusted online player initialization failed.";
            if (!NetworkServer.active || playerObject == null || context.Identity == null || context.ItemIds == null
                || playerObject.scene != gameObject.scene
                || context.Identity.SpawnConfigVersion != Version || context.Bindings == null
                || context.Bindings.TrustedSpawnConfiguration != this || context.Bindings.gameObject.scene != gameObject.scene
                || context.SpawnPoint == null || context.SpawnPoint.gameObject.scene != gameObject.scene
                || !(NetworkManager.singleton is AuthoritativeNetworkManager manager)
                || manager.ServerWorldGeneration != context.ServerWorldGeneration
                || !ValidateServerConfiguration(out reason)) return false;
            reason = "Trusted online player initialization failed.";
            NetworkIdentity networkIdentity = playerObject.GetComponent<NetworkIdentity>();
            OnlinePlayerIdentity identity = playerObject.GetComponent<OnlinePlayerIdentity>();
            Player player = playerObject.GetComponent<Player>();
            BackpackInventory inventory = playerObject.GetComponent<BackpackInventory>();
            EquipmentInventory equipment = playerObject.GetComponent<EquipmentInventory>();
            BackpackNetworkInventory networkInventory = playerObject.GetComponent<BackpackNetworkInventory>();
            if (networkIdentity == null || networkIdentity.netId != 0 || identity == null || player == null
                || player.Equipment == null || inventory == null || equipment == null || networkInventory == null
                || inventory.AuthorityCore == null || equipment.AuthorityCore == null
                || inventory.ItemCount != 0 || equipment.EquippedItem != null) return false;
            try
            {
                // Both local cores already exist from Awake. Keep the exact same global generator later used
                // by BackpackNetworkManager/BackpackNetworkInventory; no private/offline ID sequence is created.
                inventory.SetNetworkAuthority(true);
                equipment.SetNetworkAuthority(true);
                inventory.Catalog = catalog;
                networkInventory.UseCatalog(catalog);
                ItemIdGenerator itemIds = context.ItemIds;
                inventory.AuthorityCore.IdGeneratorProvider = () => itemIds;
                equipment.AuthorityCore.IdGeneratorProvider = () => itemIds;
                equipment.AuthorityCore.Catalog = catalog;
                player.Equipment.ConfigureAuthority(true, true);
                if (!identity.InitializeBeforeSpawn(context.Identity, context.ServerWorldGeneration)) return false;
                if (!inventory.TryAddItem(initialWeapon, 1, out RuntimeItem weapon, out _)) return false;
                foreach (InventorySeed seed in inventoryItems)
                    if (!inventory.TryAddItem(seed.Manifest, seed.Count, out _, out _)) return false;
                if (!player.Equipment.TryEquip(weapon.ItemId, out _)
                    || equipment.EquippedItem != weapon || player.Equipment.CurrentItemId != weapon.ItemId) return false;
                // Start never supplies the initial loadout. OnStartServer will bind network projections to these
                // same populated cores before the first spawn serialization.
                reason = null;
                return true;
            }
            catch (Exception)
            {
                reason = "Trusted online player initialization failed.";
                return false;
            }
        }
    }
}
