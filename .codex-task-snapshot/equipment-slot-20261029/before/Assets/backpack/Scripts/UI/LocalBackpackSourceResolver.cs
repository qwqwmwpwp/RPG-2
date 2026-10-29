using Mirror;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;

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
    }
}
