using System;
using Mirror;
using RPG4.LoadoutTemplate;
using UnityEngine;
namespace RPG4.Networking
{
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity), typeof(NetworkLootSource))]
    public sealed class RaidLootBag : MonoBehaviour
    {
        public string BagId { get; private set; }
        public void PrepareRoleLoot(string bagId, LootSource source)
        {
            if (string.IsNullOrEmpty(bagId) || source == null || BagId != null)
                throw new InvalidOperationException("Invalid role loot crate.");
            NetworkLootSource network = GetComponent<NetworkLootSource>();
            if (network == null) throw new InvalidOperationException("The role loot network source is missing.");
            if (!network.InitializeServer(source, out string error)) throw new InvalidOperationException(error);
            BagId = bagId;
        }
    }
}
