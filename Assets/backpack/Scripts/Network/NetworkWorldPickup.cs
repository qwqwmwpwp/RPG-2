using Mirror;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Runtime;
using RPG4.Interaction;
using UnityEngine;

namespace RPG4.Backpack.Network
{
    /// <summary>服务器只生成一次的世界实例；竞争拾取经玩家库存事务队列串行结算。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkIdentity), typeof(Collider))]
    public sealed class NetworkWorldPickup : NetworkBehaviour, IInteractable, IInteractionPrompt
    {
        [SerializeField] private ItemManifest manifest;
        [SerializeField, Min(1)] private int quantity = 1;
        private RuntimeItem item;
        private bool receiving, consumed;
        public ItemManifest Manifest => manifest;
        public int Quantity => quantity;
        public string InteractionPrompt => $"E / X 拾取 {(manifest != null ? manifest.name : name)} ×{quantity}";
        public bool ValidateServerConfiguration(out string error)
        {
            if (manifest == null || quantity < 1 || quantity > (manifest.GetFragment<StackableFragment>()?.MaxStackSize ?? 1))
            { error = "世界物品模板或数量无效。"; return false; }
            try { manifest.ValidateConfiguration(); }
            catch (System.Exception) { error = "世界物品定义无效。"; return false; }
            error = null; return true;
        }
        public override void OnStartServer()
        {
            base.OnStartServer();
            if (!ValidateServerConfiguration(out string error)
                || !BackpackInventory.TryCreateUnownedItem(manifest, quantity, out item, out error))
                Debug.LogError($"{name} 无法生成权威世界物品：{error}", this);
        }
        public bool CanInteract(PlayerInteraction player) => isActiveAndEnabled && !consumed && !receiving
            && player != null && NetworkClient.active && netId != 0
            && player.GetComponentInParent<BackpackNetworkInventory>() != null;
        public void Interact(PlayerInteraction player)
        {
            if (!CanInteract(player)) return;
            player.GetComponentInParent<BackpackNetworkInventory>()?.RequestPickup(this);
        }
        internal bool TryServerReceive(BackpackNetworkInventory inventory, out string error)
        {
            error = "世界物品已不可拾取。";
            if (!isServer || !isActiveAndEnabled || consumed || receiving || item == null
                || inventory == null || !inventory.IsServerInventory || inventory.AuthorityCore == null
                || inventory.gameObject.scene != gameObject.scene
                || !NetworkServer.spawned.TryGetValue(netId, out NetworkIdentity identity) || identity != netIdentity)
                return false;
            if (Vector3.Distance(inventory.transform.position, transform.position) > 3f)
            { error = "物品超出拾取距离。"; return false; }
            Vector3 origin = inventory.transform.position + Vector3.up;
            Vector3 target = GetComponent<Collider>().bounds.center;
            Vector3 path = target - origin;
            foreach (RaycastHit hit in Physics.RaycastAll(origin, path.normalized, path.magnitude, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.IsChildOf(inventory.transform) || hit.collider.transform.IsChildOf(transform)) continue;
                error = "物品被障碍物遮挡。"; return false;
            }
            if (inventory.Catalog == null || !inventory.Catalog.TryGet(manifest.ItemType, out ItemManifest registered) || registered != manifest)
            { error = "此世界物品不在玩家物品目录中。"; return false; }
            receiving = true;
            try
            {
                // true 包含整堆合并成功；RootOwner 不能表示接收结果。
                if (!inventory.AuthorityCore.TryReceiveExistingItem(item, out error)) return false;
                consumed = true; item = null;
                InventoryTransactions.RunPostCommit(() => NetworkServer.Destroy(gameObject), "世界物品已接收，销毁拾取物失败");
                return true;
            }
            finally { receiving = false; }
        }
    }
}
