using TMPro;
using UnityEngine;
using UnityEngine.UI;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.UI
{
    /// <summary>当前武器只读HUD。换弹进度只反映权威动作时间轴。</summary>
    [DisallowMultipleComponent]
    public sealed class WeaponAmmoHUD : MonoBehaviour
    {
        public GameObject contentRoot;
        public TMP_Text weaponName;
        public TMP_Text rounds;
        public TMP_Text reserve;
        public TMP_Text durability;
        public TMP_Text actionLabel;
        public Image icon;
        public Slider reloadProgress;
        private object source;
        private EventSubscription hudSubscription;
        private WeaponHudSnapshot snapshot;
        private float nextBindingCheck;
        private float nextSnapshotRetry;

        private void OnEnable() { nextBindingCheck = 0f; Render(null); }
        private void OnDisable() => Unbind();
        private void OnDestroy() => Unbind();

        private void Update()
        {
            if (Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + 0.25f;
                IBackpackView inventory = LocalBackpackSourceResolver.Resolve();
                object next = inventory != null && inventory.IsReady ? inventory.EventSource : null;
                if (!ReferenceEquals(next, source))
                {
                    Unbind();
                    source = next;
                    if (source != null)
                    {
                        hudSubscription = EventBus.Global.Subscribe<WeaponHudSnapshot>(InvChannels.WeaponHudChanged, source, this, Render);
                        RequestSnapshot();
                    }
                }
            }
            if (source != null && snapshot == null && Time.unscaledTime >= nextSnapshotRetry) RequestSnapshot();
            RefreshProgress();
        }

        private void RequestSnapshot()
        {
            nextSnapshotRetry = Time.unscaledTime + 1f;
            EventBus.Global.Publish(CmdChannels.WeaponSnapshot, source, new WeaponSnapshotRequest(ItemId.None, hudOnly: true));
        }

        private void Unbind()
        {
            hudSubscription.Dispose();
            hudSubscription = default;
            source = null;
            Render(null);
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
                    ? Mathf.Clamp01((float)((Time.timeAsDouble - snapshot.ActionStartTime) / snapshot.ActionDuration)) : 0f;
            }
            if (actionLabel != null) actionLabel.text = !active ? IdleStatus()
                : snapshot.ActionStage == WeaponUiActionStage.Started ? "换弹中…"
                : snapshot.ActionStage == WeaponUiActionStage.MagazineCommitted ? "弹匣已装入 · 上膛中…" : "已上膛 · 完成换弹中…";
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
