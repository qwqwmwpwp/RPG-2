using RPG4.LoadoutContracts;
using RPG4.PlayerMovement;
using RPG4.Backpack.Items;
using RPG4.UI;
using RPG4.LoadoutTemplate;
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
        private RoleLoadoutController role;
        private bool dirty, visible;
        private string selectedType;

        public void Bind(Player source)
        {
            player = source;
            if (isActiveAndEnabled) RefreshRole();
            dirty = true;
        }

        public void Unbind()
        {
            player = null;
            SetRole(null);
            SetVisible(false);
        }

        private void OnDisable() => SetRole(null);
        private void OnEnable() { RefreshRole(); dirty = true; }

        private void RefreshRole() => SetRole(player != null ? player.Equipment?.Role : null);

        private void SetRole(RoleLoadoutController next)
        {
            if (ReferenceEquals(role, next)) return;
            if (!ReferenceEquals(role, null)) role.Changed -= OnInventoryChanged;
            role = next;
            if (role != null) role.Changed += OnInventoryChanged;
            dirty = true;
        }

        private void OnInventoryChanged() => dirty = true;

        private void SetVisible(bool value)
        {
            visible = value;
            if (contentRoot != null && contentRoot != gameObject && contentRoot.activeSelf != value)
                contentRoot.SetActive(value);
        }

        private void Update()
        {
            if (player == null || !player.isActiveAndEnabled || player.Consumables == null || role == null)
            {
                SetVisible(false);
                return;
            }
            string selected = PlayerConsumableUse.SelectedQuickUseType;
            if (dirty || selected != selectedType)
            {
                selectedType = selected;
                dirty = false;
                RefreshItem();
            }
            if (visible) RefreshProgress();
        }

        private void RefreshItem()
        {
            RuntimeItem item = player.Consumables.ResolveQuickUse(out long total);
            SetVisible(item != null && total > 0);
            if (!visible) return;
            var catalog = role.ManifestCatalog;
            string name = catalog != null && catalog.TryGet(item.ItemType, out ItemManifest manifest)
                && !string.IsNullOrWhiteSpace(manifest.name) ? manifest.name : "药品";
            if (itemName != null) itemName.text = name;
            if (count != null) count.text = "× " + total;
            if (keyLabel != null) keyLabel.text = "H · 使用快捷药品";
            if (typeLabel != null) typeLabel.text = "每次使用一份";
            if (visualTheme != null)
            {
                if (keyLabel != null) keyLabel.color = visualTheme.SecondaryTextColor;
                if (typeLabel != null) typeLabel.color = visualTheme.SecondaryTextColor;
                if (statusLabel != null) statusLabel.color = visualTheme.PrimaryTextColor;
            }
            if (icon != null)
            {
                Sprite sprite = item.GetFragment<ImageFragment>()?.Icon;
                icon.sprite = sprite;
                icon.enabled = sprite != null;
                icon.preserveAspect = true;
            }
        }

        private void RefreshProgress()
        {
            ConsumableUseViewState state = player.Consumables.GetViewState();
            if (statusLabel != null) statusLabel.text = state.Active ? "用药中…" : player.Consumables.Feedback;
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
