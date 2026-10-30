using UnityEngine;
using UnityEngine.UI;
using TMPro;

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
        [SerializeField] private Color successColor = new Color(0.3f, 1f, 0.5f, 1f);
        [SerializeField] private Color failureColor = new Color(1f, 0.45f, 0.3f, 1f);
        [SerializeField, Min(0.1f)] private float feedbackDuration = 2.5f;
        private float feedbackUntil;

        public void SetState(bool visible, bool canInteract, string prompt = null)
        {
            if (canvasGroup != null) canvasGroup.alpha = visible ? 1f : 0f;
            if (crosshair != null) crosshair.color = canInteract ? interactableColor : idleColor;
            if (promptLabel != null) promptLabel.text = canInteract ? prompt ?? "E / X 交互" : string.Empty;
            if (feedbackLabel != null && Time.unscaledTime >= feedbackUntil) feedbackLabel.text = string.Empty;
        }

        public void ShowFeedback(string message, bool success)
        {
            if (feedbackLabel == null) return;
            feedbackLabel.text = message;
            feedbackLabel.color = success ? successColor : failureColor;
            feedbackUntil = Time.unscaledTime + Mathf.Max(0.1f, feedbackDuration);
        }
    }
}
