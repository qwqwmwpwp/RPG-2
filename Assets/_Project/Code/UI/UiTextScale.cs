using TMPro;
using UnityEngine;
namespace RPG4.UI
{
    /// <summary>Settings controller adapter. The display helper receives only a numeric scale.</summary>
    [DisallowMultipleComponent]
    public sealed class UiTextScale : MonoBehaviour
    {
        [SerializeField] private TMP_Text[] labels = System.Array.Empty<TMP_Text>();
        private TextScaleDisplay display;
        private TextScaleDisplay Display => display ?? (display = new TextScaleDisplay(labels));
        private void Awake() { _ = Display; }
        private void OnEnable() { PlayerUiPreferences.Changed += ApplyPreference; ApplyPreference(); }
        private void OnDisable() => PlayerUiPreferences.Changed -= ApplyPreference;
        private void ApplyPreference() => ApplyScale(PlayerUiPreferences.TextScale);
        public void ApplyScale(float scale) => Display.Apply(scale);
        private sealed class TextScaleDisplay
        {
            private readonly TMP_Text[] labels;
            private readonly float[] sizes;
            public TextScaleDisplay(TMP_Text[] labels)
            {
                this.labels = labels; sizes = new float[labels.Length];
                for (int i = 0; i < labels.Length; i++) if (labels[i] != null) sizes[i] = labels[i].fontSize;
            }
            public void Apply(float scale)
            { for (int i = 0; i < labels.Length; i++) if (labels[i] != null) labels[i].fontSize = sizes[i] * scale; }
        }
    }
}
