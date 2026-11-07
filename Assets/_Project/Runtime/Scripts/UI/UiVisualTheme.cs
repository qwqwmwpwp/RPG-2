using UnityEngine;

namespace RPG4.UI
{
    /// <summary>
    /// Shared visual tokens for UI prefabs. The asset contains presentation values only;
    /// it does not carry navigation or gameplay state.
    /// </summary>
    [CreateAssetMenu(menuName = "RPG4/UI/Visual Theme", fileName = "UiVisualTheme")]
    public sealed class UiVisualTheme : ScriptableObject
    {
        [Header("Semantic colors")]
        [SerializeField] private Color backgroundColor = new Color32(17, 21, 27, 255);
        [SerializeField] private Color surfaceColor = new Color32(29, 38, 49, 255);
        [SerializeField] private Color surfaceRaisedColor = new Color32(39, 52, 67, 255);
        [SerializeField] private Color primaryTextColor = new Color32(242, 245, 247, 255);
        [SerializeField] private Color secondaryTextColor = new Color32(168, 180, 192, 255);
        [SerializeField] private Color disabledColor = new Color32(102, 113, 125, 255);
        [SerializeField] private Color accentColor = new Color32(108, 210, 255, 255);
        [SerializeField] private Color successColor = new Color32(115, 217, 139, 255);
        [SerializeField] private Color warningColor = new Color32(242, 200, 107, 255);
        [SerializeField] private Color dangerColor = new Color32(240, 120, 120, 255);

        [Header("Spacing")]
        [SerializeField, Min(0f)] private float spacingUnit = 8f;
        [SerializeField, Min(0f)] private float controlSpacing = 8f;
        [SerializeField, Min(0f)] private float sectionSpacing = 16f;
        [SerializeField, Min(0f)] private float panelPadding = 24f;

        [Header("Focus")]
        [SerializeField] private Color focusColor = new Color32(127, 219, 255, 255);
        [SerializeField, Min(0f)] private float focusThickness = 2f;
        [SerializeField, Min(0f)] private float focusPadding = 2f;
        [SerializeField, Range(0f, 1f)] private float focusAlpha = 1f;

        public Color BackgroundColor => backgroundColor;
        public Color SurfaceColor => surfaceColor;
        public Color SurfaceRaisedColor => surfaceRaisedColor;
        public Color PrimaryTextColor => primaryTextColor;
        public Color SecondaryTextColor => secondaryTextColor;
        public Color DisabledColor => disabledColor;
        public Color AccentColor => accentColor;
        public Color SuccessColor => successColor;
        public Color WarningColor => warningColor;
        public Color DangerColor => dangerColor;

        public float SpacingUnit => spacingUnit;
        public float ControlSpacing => controlSpacing;
        public float SectionSpacing => sectionSpacing;
        public float PanelPadding => panelPadding;

        public Color FocusColor => focusColor;
        public float FocusThickness => focusThickness;
        public float FocusPadding => focusPadding;
        public float FocusAlpha => focusAlpha;
    }
}
