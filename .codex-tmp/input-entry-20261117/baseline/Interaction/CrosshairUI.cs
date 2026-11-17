using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RPG4.UI;

namespace RPG4.Interaction
{
    /// <summary>准心及提示的展示；固定层级和组件引用由预制体配置。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class CrosshairUI : MonoBehaviour
    {
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private Image crosshair;
        [SerializeField] private Color idleColor = Color.white;
        [SerializeField] private Color interactableColor = new Color(0.3f, 1f, 0.5f, 1f);
        [SerializeField] private TMP_Text promptLabel;
        [SerializeField] private TMP_Text feedbackLabel;
        [SerializeField] private TMP_Text notificationLabel;
        [SerializeField] private GameObject hitMarker;
        [SerializeField] private TMP_Text obstructionLabel;
        [SerializeField] private Color successColor = new Color(0.3f, 1f, 0.5f, 1f);
        [SerializeField] private Color failureColor = new Color(1f, 0.45f, 0.3f, 1f);
        [SerializeField, Min(0.1f)] private float feedbackDuration = 2.5f;
        private float feedbackUntil;
        private float notificationUntil, hitUntil, obstructionUntil;

        private void OnEnable() => ClearFeedback();
        private void OnDisable() => ClearFeedback();

        private void Update()
        {
            float time = Time.unscaledTime;
            if (hitMarker != null) hitMarker.SetActive(PlayerUiPreferences.HitFeedback && time < hitUntil);
            if (obstructionLabel != null) obstructionLabel.gameObject.SetActive(time < obstructionUntil);
            if (feedbackLabel != null && time >= feedbackUntil) feedbackLabel.text = string.Empty;
            if (notificationLabel != null && time >= notificationUntil) notificationLabel.text = string.Empty;
        }

        public void SetState(bool visible, bool canInteract, string prompt = null)
        {
            if (canvasGroup != null) canvasGroup.alpha = visible ? 1f : 0f;
            if (crosshair != null) crosshair.color = canInteract ? interactableColor : idleColor;
            if (promptLabel != null) promptLabel.text = canInteract ? prompt ?? "E / X 交互" : string.Empty;
            if (feedbackLabel != null && Time.unscaledTime >= feedbackUntil) feedbackLabel.text = string.Empty;
        }

        public void ShowFeedback(string message, bool success)
        {
            TMP_Text label = success && notificationLabel != null ? notificationLabel : feedbackLabel;
            if (label == null) return;
            label.text = message;
            label.color = success ? successColor : failureColor;
            float until = Time.unscaledTime + Mathf.Max(0.1f, feedbackDuration);
            if (label == notificationLabel) notificationUntil = until;
            else feedbackUntil = until;
        }

        public void ShowShotFeedback(bool damageApplied, bool obstructed)
        {
            if (damageApplied && PlayerUiPreferences.HitFeedback) hitUntil = Time.unscaledTime + 0.15f;
            if (obstructed) obstructionUntil = Time.unscaledTime + 0.6f;
        }

        private void ClearFeedback()
        {
            feedbackUntil = notificationUntil = hitUntil = obstructionUntil = 0f;
            if (hitMarker != null) hitMarker.SetActive(false);
            if (obstructionLabel != null) obstructionLabel.gameObject.SetActive(false);
            if (feedbackLabel != null) feedbackLabel.text = string.Empty;
            if (notificationLabel != null) notificationLabel.text = string.Empty;
        }
    }
}
