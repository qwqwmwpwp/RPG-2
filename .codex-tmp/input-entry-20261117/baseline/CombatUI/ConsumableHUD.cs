using RPG4.LoadoutContracts;
using RPG4.PlayerMovement;
using RPG4.Backpack.Items;
using RPG4.UI;
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
        [Tooltip("Optional presentation theme. Gameplay state is never read from the theme.")]
        [SerializeField] private UiVisualTheme visualTheme;
        private Player player;
        public void Bind(Player source) => player = source;
        public void Unbind() { player = null; if (contentRoot != null) contentRoot.SetActive(false); }
        private void OnDisable() => Unbind();
        private void Update()
        {
            bool ready = player != null && player.isActiveAndEnabled && player.Consumables != null && player.Equipment?.Role != null;
            if (contentRoot != null && contentRoot != gameObject) contentRoot.SetActive(ready);
            if (!ready) return;
            RuntimeItem item = player.Consumables.ResolveQuickUse(out long total);
            var catalog = player.Equipment.Role.ManifestCatalog;
            ConsumableUseViewState state = player.Consumables.GetViewState();
            string name = item != null && catalog != null && catalog.TryGet(item.ItemType, out ItemManifest manifest)
                && !string.IsNullOrWhiteSpace(manifest.name) ? manifest.name : "药品";
            // An empty selection is not a persistent card. The owner keeps the binding alive so a later
            // inventory update can show it again without changing command or network semantics.
            bool visible = item != null && total > 0;
            if (contentRoot != null && contentRoot != gameObject) contentRoot.SetActive(visible);
            if (!visible) return;
            if (itemName != null) itemName.text = name;
            if (count != null) count.text = "× " + total;
            if (keyLabel != null) keyLabel.text = "H · 使用快捷药品";
            if (typeLabel != null) typeLabel.text = total == 0 ? "药品已用完 · 背包中设置" : "每次使用一份";
            if (visualTheme != null)
            {
                if (keyLabel != null) keyLabel.color = visualTheme.SecondaryTextColor;
                if (typeLabel != null) typeLabel.color = visualTheme.SecondaryTextColor;
                if (statusLabel != null) statusLabel.color = visualTheme.PrimaryTextColor;
            }
            if (statusLabel != null) statusLabel.text = state.Active ? "用药中…"
                : player.Consumables.Feedback;
            if (icon != null)
            {
                Sprite sprite = item?.GetFragment<ImageFragment>()?.Icon;
                icon.sprite = sprite;
                icon.enabled = sprite != null;
                icon.preserveAspect = true;
            }
            if (progress != null)
            {
                progress.gameObject.SetActive(state.Active);
                progress.interactable = false;
                progress.normalizedValue = state.Active && state.Duration > 0f
                    ? UiMotionPolicy.ReduceMotion ? 1f : Mathf.Clamp01(state.Elapsed / state.Duration) : 0f;
            }
        }
    }
}
