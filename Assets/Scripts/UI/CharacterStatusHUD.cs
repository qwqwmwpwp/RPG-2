using TMPro;
using UnityEngine;

namespace RPG4.UI
{
    /// <summary>由角色表现桥接驱动的姿态、受击方向与死亡入口；固定节点均由预制体提供。</summary>
    [DisallowMultipleComponent]
    public sealed class CharacterStatusHUD : MonoBehaviour
    {
        [SerializeField] private TMP_Text postureLabel;
        [SerializeField] private TMP_Text postureFeedback;
        [SerializeField] private RectTransform damageDirection;
        [SerializeField] private CanvasGroup damageDirectionGroup;
        [SerializeField] private PlayerDeathUI deathScreen;
        [SerializeField, Min(0.1f)] private float directionDuration = 0.8f;
        private Vector3 towardDamage;
        private float directionUntil, postureUntil, viewYaw;
        private bool wasCrouchHeld, alive = true, presentationActive = true;

        private void OnEnable() { presentationActive = true; ClearFeedback(); }
        private void OnDisable() => SetPresentationActive(false);

        public void ApplyState(bool isAlive, bool crouched, bool canStand, bool crouchHeld, bool canMove, float yaw)
        {
            alive = isAlive;
            viewYaw = yaw;
            if (postureLabel != null) postureLabel.gameObject.SetActive(alive && crouched);
            if (alive && canMove && wasCrouchHeld && !crouchHeld && crouched && !canStand)
            {
                postureUntil = Time.unscaledTime + 1.5f;
                if (postureFeedback != null) postureFeedback.text = "空间不足，无法起身";
            }
            wasCrouchHeld = crouchHeld;
            if (!alive) ClearFeedback();
            if (deathScreen != null) deathScreen.SetLifeState(alive);
        }

        public void ShowDamageDirection(Vector3 incomingDirection)
        {
            if (!alive || !presentationActive) return;
            incomingDirection.y = 0f;
            float length = incomingDirection.sqrMagnitude;
            if (length < 0.0001f || float.IsNaN(length) || float.IsInfinity(length)) return;
            towardDamage = -incomingDirection.normalized;
            directionUntil = Time.unscaledTime + directionDuration;
        }

        private void LateUpdate()
        {
            if (!presentationActive) return;
            if (postureFeedback != null && Time.unscaledTime >= postureUntil) postureFeedback.text = string.Empty;
            bool showDirection = alive && Time.unscaledTime < directionUntil;
            if (damageDirectionGroup != null)
                damageDirectionGroup.alpha = showDirection ? Mathf.Clamp01((directionUntil - Time.unscaledTime) / 0.2f) : 0f;
            if (!showDirection || damageDirection == null || !(damageDirection.parent is RectTransform bounds)) return;
            Vector3 local = Quaternion.Euler(0f, -viewYaw, 0f) * towardDamage;
            float angle = Mathf.Atan2(local.x, local.z);
            damageDirection.anchoredPosition = new Vector2(Mathf.Sin(angle) * bounds.rect.width * 0.4f,
                Mathf.Cos(angle) * bounds.rect.height * 0.36f);
            damageDirection.localRotation = Quaternion.Euler(0f, 0f, -angle * Mathf.Rad2Deg);
        }

        public void SetPresentationActive(bool value)
        {
            presentationActive = value;
            if (value) return;
            ClearFeedback();
            if (postureLabel != null) postureLabel.gameObject.SetActive(false);
            if (deathScreen != null) deathScreen.Close();
        }
        private void ClearFeedback()
        {
            directionUntil = postureUntil = 0f;
            if (damageDirectionGroup != null) damageDirectionGroup.alpha = 0f;
            if (postureFeedback != null) postureFeedback.text = string.Empty;
        }
    }
}
