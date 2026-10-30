using RPG4.CharacterAttributes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using AttributeComponent = RPG4.CharacterAttributes.CharacterAttributes;

namespace RPG4.UI
{
    /// <summary>场景和预制体预接线的生命视图；同一组件支持玩家 HUD 与靶子世界血条。</summary>
    [DisallowMultipleComponent]
    public sealed class CharacterHealthView : MonoBehaviour
    {
        [SerializeField] private AttributeComponent attributes;
        [SerializeField] private Image healthFill;
        [SerializeField] private TMP_Text healthLabel;
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private string displayName = "生命";
        [SerializeField] private string idleStatus = "";
        [SerializeField] private string deadStatus = "已死亡";
        [SerializeField] private Transform billboardRoot;
        [SerializeField] private Image damageOverlay;
        [SerializeField, Min(0.01f)] private float flashDuration = 0.3f;

        private Camera worldCamera;
        private float messageUntil;
        private float flashUntil;
        private bool showingDamage;

        private void OnEnable()
        {
            if (attributes == null || healthFill == null || healthLabel == null)
            {
                Debug.LogError("CharacterHealthView 未配置生命组件、血条或生命文字。", this);
                enabled = false;
                return;
            }
            attributes.Changed += Refresh;
            attributes.Damaged += OnDamaged;
            attributes.Revived += OnRevived;
            showingDamage = false;
            SetOverlayAlpha(0f);
            Refresh();
        }

        private void OnDisable()
        {
            if (attributes != null)
            {
                attributes.Changed -= Refresh;
                attributes.Damaged -= OnDamaged;
                attributes.Revived -= OnRevived;
            }
            SetOverlayAlpha(0f);
        }

        private void Refresh()
        {
            Vector2 maximum = healthFill.rectTransform.anchorMax;
            maximum.x = attributes.NormalizedHealth;
            healthFill.rectTransform.anchorMax = maximum;
            healthLabel.text = $"{displayName}   {attributes.CurrentHealth:0.#} / {attributes.MaxHealth:0.#}";
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (statusLabel == null) return;
            if (!attributes.IsAlive) statusLabel.text = deadStatus;
            else if (!showingDamage) statusLabel.text = idleStatus;
        }

        private void OnDamaged(DamageInfo damage, float appliedAmount)
        {
            showingDamage = true;
            messageUntil = Time.unscaledTime + 1.25f;
            flashUntil = Time.unscaledTime + flashDuration;
            if (statusLabel != null && attributes.IsAlive) statusLabel.text = $"受到 {appliedAmount:0.#} 点伤害";
            RefreshStatus();
            SetOverlayAlpha(0.22f);
        }

        private void OnRevived()
        {
            showingDamage = false;
            flashUntil = 0f;
            SetOverlayAlpha(0f);
            Refresh();
        }

        private void LateUpdate()
        {
            if (showingDamage && Time.unscaledTime >= messageUntil)
            {
                showingDamage = false;
                RefreshStatus();
            }
            if (damageOverlay != null)
                SetOverlayAlpha(0.22f * Mathf.Clamp01((flashUntil - Time.unscaledTime) / flashDuration));
            if (billboardRoot == null) return;
            if (worldCamera == null || !worldCamera.isActiveAndEnabled) worldCamera = Camera.main;
            if (worldCamera != null) billboardRoot.rotation = worldCamera.transform.rotation;
        }

        private void SetOverlayAlpha(float alpha)
        {
            if (damageOverlay == null) return;
            Color color = damageOverlay.color;
            color.a = alpha;
            damageOverlay.color = color;
        }
    }
}
