using Mirror;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using RPG4.Interaction;
using UnityEngine;

namespace RPG4.Backpack.World
{
    /// <summary>本地世界物品：仅在权威库存整批接收成功后消耗，失败保留原实例与随机值。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/World Pickup")]
    public sealed class WorldPickup : MonoBehaviour, IInteractable, IInteractionPrompt
    {
        [SerializeField] private ItemManifest manifest;
        [Tooltip("必须在 1..物品单堆上限内；不可堆叠物品固定为 1。")]
        [SerializeField, Min(1)] private int quantity = 1;

        private RuntimeItem item;
        private bool receiving;
        private bool consumed;

        public ItemManifest Manifest => manifest;
        public int Quantity => quantity;
        public string InteractionPrompt => $"E / X 拾取 {(manifest != null ? manifest.name : name)} ×{quantity}";

        public bool CanInteract(PlayerInteraction player) =>
            isActiveAndEnabled && !consumed && !receiving && player != null;

        public void Interact(PlayerInteraction player)
        {
            if (!CanInteract(player)) return;
            receiving = true;
            try
            {
                // 本组件没有网络命令或世界物品的服务端身份。Host 同样拒绝，避免本地路径绕过服务器。
                if (NetworkClient.active || NetworkServer.active
                    || GetComponentInParent<NetworkIdentity>() != null
                    || player.GetComponentInParent<NetworkIdentity>() != null
                    || player.GetComponentInParent<BackpackNetworkInventory>() != null)
                {
                    Fail(player, "当前模式暂不支持拾取此物品。",
                        "本地拾取入口拒绝网络会话、网络身份或网络库存；联机拾取必须经过服务器验证。");
                    return;
                }

                BackpackInventory inventory = player.GetComponentInParent<BackpackInventory>();
                if (inventory == null || !inventory.isActiveAndEnabled || !inventory.IsInitialized)
                {
                    Fail(player, "背包尚未就绪。", "未找到已启用且完成初始化的本地 BackpackInventory。");
                    return;
                }
                if (!TryValidateConfiguration(out string configurationError))
                {
                    Fail(player, "此物品暂时无法拾取。", configurationError);
                    return;
                }
                if (item == null && !BackpackInventory.TryCreateUnownedItem(manifest, quantity, out item, out string createError))
                {
                    Fail(player, "此物品暂时无法拾取。", $"创建运行时物品失败：{createError}");
                    return;
                }

                if (!inventory.TryReceiveItem(item, out string receiveError))
                {
                    string message = receiveError != null && receiveError.Contains("空间不足")
                        ? "背包空间不足，无法拾取全部物品。" : "暂时无法拾取此物品。";
                    Fail(player, message, $"库存拒绝接收物品：{receiveError}");
                    return;
                }

                consumed = true;
                item = null; // 所有权已经交给库存；不能再次投递同一实例。
                player.ShowInteractionFeedback($"已拾取 {manifest.name} ×{quantity}", true);
                gameObject.SetActive(false);
            }
            finally
            {
                receiving = false;
            }
        }

        private bool TryValidateConfiguration(out string error)
        {
            if (manifest == null)
            {
                error = "WorldPickup 未配置 ItemManifest。";
                return false;
            }

            int maxStackSize = manifest.GetFragment<StackableFragment>()?.MaxStackSize ?? 1;
            if (quantity < 1 || quantity > maxStackSize)
            {
                error = $"WorldPickup 数量 {quantity} 超出 {manifest.name} 的合法单堆范围 1..{maxStackSize}。";
                return false;
            }

            error = null;
            return true;
        }

        private void Fail(PlayerInteraction player, string message, string detail)
        {
            Debug.LogWarning($"{name} 拾取失败：{detail}", this);
            player.ShowInteractionFeedback(message, false);
        }
    }
}
