using Mirror;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using UnityEngine;

namespace RPG4.Backpack.UI
{
    /// <summary>查询已就绪的本地玩家背包：优先网络本地玩家，再回退到本地注册表。</summary>
    internal static class LocalBackpackSourceResolver
    {
        public static IBackpackView Resolve()
        {
            NetworkIdentity localIdentity = NetworkClient.localPlayer;
            if (localIdentity != null)
            {
                BackpackNetworkInventory network = localIdentity.GetComponent<BackpackNetworkInventory>();
                if (network != null) return network.IsReady ? network : null;
            }

            return BackpackRegistry.TryGetPrimary(out BackpackInventory backpack) && backpack.IsInitialized
                ? backpack
                : null;
        }

        /// <summary>装备始终来自当前背包拥有者，避免切换本地角色时串用上一角色的武器槽。</summary>
        public static IBackpackView ResolveEquipment(IBackpackView playerInventory)
        {
            if (!(playerInventory is Component owner) || owner == null) return null;
            EquipmentInventory equipment = owner.GetComponent<EquipmentInventory>();
            return equipment != null && equipment.isActiveAndEnabled && equipment.IsReady ? equipment : null;
        }
    }
}
