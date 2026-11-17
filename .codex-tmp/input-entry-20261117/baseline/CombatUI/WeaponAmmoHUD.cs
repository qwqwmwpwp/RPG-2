using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Mirror;
using RPG4.LoadoutContracts;
using RPG4.PlayerMovement;
using RPG4.PlayerIntent;
using RPG4.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>经角色装备控制器读取新库存投影，HUD不持有权威物品。</summary>
    [DisallowMultipleComponent]
    public sealed class WeaponAmmoHUD : MonoBehaviour
    {
        [SerializeField] private bool explicitSessionSource;
        public GameObject contentRoot;
        public TMP_Text weaponName, rounds, reserve, durability, actionLabel;
        public Image icon;
        public Slider reloadProgress;
        [SerializeField] private UiVisualTheme visualTheme;
        private PlayerEquipment source;
        private WeaponHudSnapshot snapshot;
        private bool publishingSessionBindingChanged;
        private float nextBindingCheck;
        private long bindingGeneration;
        public event System.Action<WeaponAmmoHUD> SessionBindingChanged;
        public bool BindSessionSource(PlayerEquipment equipment)
        {
            if (!isActiveAndEnabled || equipment == null || !equipment.isActiveAndEnabled || equipment.Role == null)
            { UnbindSessionSource(); return false; }
            if (source != equipment)
            {
                long expectedGeneration = bindingGeneration + 1;
                UnbindSessionSource();
                if (this == null || !isActiveAndEnabled || bindingGeneration != expectedGeneration) return false;
                source = equipment; source.WeaponHudChanged += OnHud;
            }
            if (source == null) return false;
            OnHud(source.CaptureWeaponHud());
            return source == equipment && snapshot != null;
        }
        public bool IsSessionBoundTo(PlayerEquipment equipment) => isActiveAndEnabled && source == equipment
            && source != null && source.isActiveAndEnabled && snapshot != null;
        public void UnbindSessionSource()
        {
            bindingGeneration++;
            if (!ReferenceEquals(source, null)) source.WeaponHudChanged -= OnHud;
            source = null; Render(null); PublishSessionBindingChanged();
        }
        private void OnEnable() { nextBindingCheck = 0f; Render(null); }
        private void OnDisable() => UnbindSessionSource();
        private void OnDestroy() => UnbindSessionSource();
        private void Update()
        {
            if (!explicitSessionSource && Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + 0.25f;
                PlayerEquipment local = ResolveLocalEquipment();
                if (local != source) { if (local != null) BindSessionSource(local); else UnbindSessionSource(); }
            }
            if (source != null && !source.isActiveAndEnabled) UnbindSessionSource();
            RefreshProgress();
        }
        private static PlayerEquipment ResolveLocalEquipment()
        {
            if (NetworkClient.active) return NetworkClient.localPlayer != null
                ? NetworkClient.localPlayer.GetComponent<PlayerEquipment>() : null;
            if (NetworkServer.active) return null;
            foreach (Player player in FindObjectsOfType<Player>())
                if (player.InputSource == PlayerInputSource.Local && player.Input != null && player.Input.UsesLocalInput)
                    return player.Equipment;
            return null;
        }
        private void OnHud(WeaponHudSnapshot value)
        {
            if (source == null || !isActiveAndEnabled) return;
            Render(value); PublishSessionBindingChanged();
        }
        private void PublishSessionBindingChanged()
        {
            if (publishingSessionBindingChanged || SessionBindingChanged == null) return;
            publishingSessionBindingChanged = true;
            try
            {
                foreach (System.Delegate handler in SessionBindingChanged.GetInvocationList())
                    try { ((System.Action<WeaponAmmoHUD>)handler)(this); }
                    catch (System.Exception exception) { Debug.LogException(exception, this); }
            }
            finally { publishingSessionBindingChanged = false; }
        }
        private void Render(WeaponHudSnapshot value)
        {
            snapshot = value;
            bool visible = value?.Item != null;
            if (contentRoot != null && contentRoot != gameObject) contentRoot.SetActive(visible);
            if (weaponName != null) weaponName.text = visible ? value.Item.Name + FireModeLabel(value.FireMode) : string.Empty;
            if (rounds != null) rounds.text = visible ? (value.MagazineRounds + value.ChamberCount).ToString() : string.Empty;
            if (reserve != null) reserve.text = visible
                ? $"备用弹匣 {value.NonEmptyCompatibleMagazineCount} · 散装弹 {value.LooseAmmoCount}" : string.Empty;
            if (durability != null) durability.text = visible ? $"耐久 {value.Durability:0.#} / {value.MaxDurability:0.#}" : string.Empty;
            if (icon != null)
            {
                icon.sprite = visible ? value.Item.Icon : null;
                icon.enabled = icon.sprite != null;
                icon.preserveAspect = true;
            }
            RefreshProgress();
        }

        private void RefreshProgress()
        {
            bool active = snapshot?.Item != null && (snapshot.ActionStage == WeaponUiActionStage.Started
                || snapshot.ActionStage == WeaponUiActionStage.MagazineCommitted || snapshot.ActionStage == WeaponUiActionStage.Chambered);
            if (reloadProgress != null)
            {
                reloadProgress.gameObject.SetActive(active);
                reloadProgress.interactable = false;
                reloadProgress.normalizedValue = active && snapshot.ActionDuration > 0f
                    ? UiMotionPolicy.ReduceMotion ? 1f
                        : Mathf.Clamp01((float)(((source != null ? source.SimulationTime : Time.timeAsDouble) - snapshot.ActionStartTime) / snapshot.ActionDuration)) : 0f;
            }
            if (actionLabel != null) actionLabel.text = !active ? IdleStatus()
                : snapshot.ActionStage == WeaponUiActionStage.Started ? "换弹中…"
                : snapshot.ActionStage == WeaponUiActionStage.MagazineCommitted ? "弹匣已装入 · 上膛中…" : "已上膛 · 完成换弹中…";
            if (visualTheme != null)
            {
                if (weaponName != null) weaponName.color = visualTheme.PrimaryTextColor;
                if (rounds != null) rounds.color = visualTheme.PrimaryTextColor;
                if (reserve != null) reserve.color = visualTheme.SecondaryTextColor;
                if (durability != null) durability.color = visualTheme.SecondaryTextColor;
                if (actionLabel != null) actionLabel.color = visualTheme.SecondaryTextColor;
            }
        }

        private string IdleStatus()
        {
            if (snapshot?.Item == null) return string.Empty;
            if (!string.IsNullOrEmpty(snapshot.StatusSummary)) return snapshot.StatusSummary;
            if (snapshot.Durability <= 0f) return "武器已损坏";
            if (snapshot.ChamberCount == 0 && snapshot.MagazineRounds > 0) return "未上膛";
            if (snapshot.MagazineCapacity == 0) return snapshot.ChamberCount > 0 ? "无弹匣 · 膛内剩余 1 发" : "无弹匣";
            if (snapshot.MagazineRounds == 0) return snapshot.ChamberCount > 0 ? "空弹匣 · 膛内剩余 1 发" : "弹药耗尽";
            return string.Empty;
        }

        private static string FireModeLabel(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return " · " + (value == "Automatic" ? "自动" : value == "SemiAutomatic" ? "半自动" : value);
        }
    }
}
