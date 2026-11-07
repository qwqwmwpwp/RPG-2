using TMPro;
using UnityEngine;

namespace RPG4.UI
{
    /// <summary>只缩放预制体明确接线的文字；基础字号仍由资产维护。</summary>
    [DisallowMultipleComponent]
    public sealed class UiTextScale : MonoBehaviour
    {
        [SerializeField] private TMP_Text[] labels = System.Array.Empty<TMP_Text>();
        private float[] sizes;

        private void Awake()
        {
            sizes = new float[labels.Length];
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] != null) sizes[i] = labels[i].fontSize;
        }
        private void OnEnable()
        {
            PlayerUiPreferences.Changed += Apply;
            Apply();
        }
        private void OnDisable() => PlayerUiPreferences.Changed -= Apply;
        private void Apply()
        {
            if (sizes == null) return;
            float scale = PlayerUiPreferences.TextScale;
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] != null) labels[i].fontSize = sizes[i] * scale;
        }
    }
}
