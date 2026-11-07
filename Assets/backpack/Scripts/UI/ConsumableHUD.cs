using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>只读本地药品 HUD；动作进度直接读取当前玩家宿主的权威状态。</summary>
    [DisallowMultipleComponent]
    public sealed class ConsumableHUD : MonoBehaviour
    {
        public GameObject contentRoot;
        public TMP_Text itemName;
        public TMP_Text count;
        public TMP_Text keyLabel;
        public TMP_Text typeLabel;
        public TMP_Text statusLabel;
        public Image icon;
        public Slider progress;
        private IBackpackView inventory;
        public void Bind(IBackpackView source) => inventory = source;
        public void Unbind() { inventory = null; if (contentRoot != null) contentRoot.SetActive(false); }
        private void OnDisable() => Unbind();
        private void Update()
        {
            bool ready = inventory != null && inventory.IsReady && ReferenceEquals(inventory, LocalBackpackSourceResolver.Resolve());
            if (contentRoot != null) contentRoot.SetActive(ready);
            if (!ready) return;
            RuntimeItem item = ConsumableQuickUse.Resolve(inventory, out long total);
            InventoryHostCore.TryGetCommandHost(inventory.EventSource, out InventoryHostCore host);
            ConsumableUseViewState state = host != null ? host.GetConsumableUseState() : default;
            string name = item != null && host?.Catalog != null && host.Catalog.TryGet(item.ItemType, out ItemManifest manifest)
                ? manifest.name : ConsumableQuickUse.SelectedType.Substring(ConsumableQuickUse.SelectedType.LastIndexOf('.') + 1);
            if (itemName != null) itemName.text = name;
            if (count != null) count.text = "× " + total;
            if (keyLabel != null) keyLabel.text = "H · 使用快捷药品";
            if (typeLabel != null) typeLabel.text = total == 0 ? "药品已用完 · 背包中设置" : "每次使用一份";
            if (statusLabel != null) statusLabel.text = state.Active ? "用药中…"
                : InventoryUIInputRouter.ConsumableFeedback;
            if (icon != null)
            {
                Sprite sprite = item?.GetFragment<ImageFragment>()?.Icon;
                if (sprite == null && host?.Catalog != null
                    && host.Catalog.TryGet(new BackpackTag(ConsumableQuickUse.SelectedType), out ItemManifest selected))
                    sprite = selected.GetFragment<ImageFragment>()?.Icon;
                icon.sprite = sprite;
                icon.enabled = sprite != null;
                icon.preserveAspect = true;
            }
            if (progress != null)
            {
                progress.gameObject.SetActive(state.Active);
                progress.interactable = false;
                progress.normalizedValue = state.Active && state.Duration > 0f ? Mathf.Clamp01(state.Elapsed / state.Duration) : 0f;
            }
        }
    }
}
