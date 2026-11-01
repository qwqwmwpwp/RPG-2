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
        [Tooltip("Online scenes bind their owned inventory explicitly; never fall back while unbound.")]
        [SerializeField] private bool explicitSessionSource;
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
        private IBackpackView sessionInventory;
        private object snapshotSource;
        private long bindingGeneration;
        private bool publishingSessionBindingChanged;
        public event System.Action<WeaponAmmoHUD> SessionBindingChanged;

        public bool BindSessionSource(IBackpackView inventory)
        {
            if (!explicitSessionSource || !isActiveAndEnabled || inventory == null || !inventory.IsReady
                || inventory.EventSource == null) { UnbindSessionSource(); return false; }
            object expectedSource = inventory.EventSource;
            sessionInventory = inventory;
            if (!ReferenceEquals(source, expectedSource)) BindSource(expectedSource);
            return this != null && isActiveAndEnabled && explicitSessionSource
                && ReferenceEquals(sessionInventory, inventory) && inventory.IsReady
                && ReferenceEquals(inventory.EventSource, expectedSource) && ReferenceEquals(source, expectedSource);
        }

        /// <summary>Only a snapshot received for this exact current binding satisfies presentation readiness.
        /// A valid empty-weapon snapshot also counts; a subscription alone never does.</summary>
        public bool IsSessionBoundTo(IBackpackView inventory) => explicitSessionSource && isActiveAndEnabled
            && inventory != null && inventory.IsReady && ReferenceEquals(sessionInventory, inventory)
            && inventory.EventSource != null && ReferenceEquals(source, inventory.EventSource)
            && ReferenceEquals(snapshotSource, source) && snapshot != null;

        public void UnbindSessionSource() { sessionInventory = null; Unbind(); }

        private void OnEnable() { nextBindingCheck = 0f; Render(null); }
        private void OnDisable() => UnbindSessionSource();
        private void OnDestroy() => UnbindSessionSource();

        private void Update()
        {
            if (Time.unscaledTime >= nextBindingCheck)
            {
                nextBindingCheck = Time.unscaledTime + 0.25f;
                IBackpackView inventory = explicitSessionSource ? sessionInventory : LocalBackpackSourceResolver.Resolve();
                object next = inventory != null && inventory.IsReady ? inventory.EventSource : null;
                if (!ReferenceEquals(next, source))
                {
                    BindSource(next);
                }
            }
            if (source != null && snapshot == null && Time.unscaledTime >= nextSnapshotRetry) RequestSnapshot();
            RefreshProgress();
        }

        private void BindSource(object next)
        {
            IBackpackView expectedInventory = sessionInventory;
            long clearedGeneration = Unbind();
            // Cleanup notification is public: a subscriber may cancel, replace or destroy this binding.
            // Use the token captured BEFORE that notification, never the possibly newer generation afterward.
            if (this == null || !isActiveAndEnabled || bindingGeneration != clearedGeneration) return;
            if (explicitSessionSource && (expectedInventory == null
                || !ReferenceEquals(sessionInventory, expectedInventory) || !expectedInventory.IsReady
                || !ReferenceEquals(expectedInventory.EventSource, next))) return;
            source = next;
            if (source == null) return;
            long generation = bindingGeneration;
            object expectedSource = source;
            hudSubscription = EventBus.Global.Subscribe<WeaponHudSnapshot>(InvChannels.WeaponHudChanged, source, this, value =>
            {
                if (!isActiveAndEnabled || generation != bindingGeneration || !ReferenceEquals(source, expectedSource)) return;
                if (explicitSessionSource && (sessionInventory == null || !sessionInventory.IsReady
                    || !ReferenceEquals(sessionInventory.EventSource, expectedSource))) return;
                snapshotSource = value != null ? expectedSource : null;
                Render(value);
                PublishSessionBindingChanged();
            });
            RequestSnapshot();
        }

        private void RequestSnapshot()
        {
            nextSnapshotRetry = Time.unscaledTime + 1f;
            EventBus.Global.Publish(CmdChannels.WeaponSnapshot, source, new WeaponSnapshotRequest(ItemId.None, hudOnly: true));
        }

        private long Unbind()
        {
            long clearedGeneration = ++bindingGeneration;
            hudSubscription.Dispose();
            hudSubscription = default;
            source = null;
            snapshotSource = null;
            Render(null);
            PublishSessionBindingChanged();
            return clearedGeneration;
        }

        private void PublishSessionBindingChanged()
        {
            var handlers = SessionBindingChanged;
            if (handlers == null || publishingSessionBindingChanged) return;
            publishingSessionBindingChanged = true;
            try
            {
                // Reentrant cleanup still advances bindingGeneration. The current notification's remaining
                // observers read that final state, without recursively broadcasting the same cleanup.
                foreach (System.Delegate handler in handlers.GetInvocationList())
                    try { ((System.Action<WeaponAmmoHUD>)handler)(this); }
                    catch (System.Exception) { Debug.LogError("A weapon HUD binding subscriber failed.", this); }
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
