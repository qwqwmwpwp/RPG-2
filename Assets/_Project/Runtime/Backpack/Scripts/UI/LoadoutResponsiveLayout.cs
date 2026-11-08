using System.Collections.Generic;
using UnityEngine;

namespace RPG4.Backpack.UI
{
    /// <summary>按可用画布尺寸切换整备窗口的可逆布局，仅调整 RectTransform。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public sealed class LoadoutResponsiveLayout : MonoBehaviour
    {
        [SerializeField] private float wideWidth = 1600f;
        [SerializeField] private float mediumWidth = 1180f;
        [SerializeField] private float edge = 24f;
        [SerializeField] private float gap = 24f;
        [SerializeField] private bool hideOverviewOnNarrow = true;
        private RectTransform root, overview, primary, secondary, inspection;
        private readonly Dictionary<RectTransform, Snapshot> original = new Dictionary<RectTransform, Snapshot>();
        private LayoutMode mode = (LayoutMode)(-1);
        private enum LayoutMode { Wide, Medium, Narrow }

        private struct Snapshot
        {
            public Vector2 anchorMin, anchorMax, pivot, position, size;
            public bool active;
            public Snapshot(RectTransform r) { anchorMin = r.anchorMin; anchorMax = r.anchorMax; pivot = r.pivot; position = r.anchoredPosition; size = r.sizeDelta; active = r.gameObject.activeSelf; }
            public void Restore(RectTransform r) { r.anchorMin = anchorMin; r.anchorMax = anchorMax; r.pivot = pivot; r.anchoredPosition = position; r.sizeDelta = size; r.gameObject.SetActive(active); }
        }

        private void Awake()
        {
            root = transform as RectTransform;
            overview = Find("CharacterOverview"); primary = Find("PlayerViewport"); secondary = Find("ContainerViewport");
            if (primary == null) primary = Find("BackpackViewport"); if (secondary == null) secondary = Find("StashViewport");
            WeaponInspectionPanel panel = GetComponentInChildren<WeaponInspectionPanel>(true);
            inspection = panel != null ? panel.transform as RectTransform : Find("WeaponInspectionPanel");
            Capture(overview); Capture(primary); Capture(secondary); Capture(inspection);
            Capture(Find("PlayerSummary")); Capture(Find("ContainerSummary")); Capture(Find("ActionHint")); Capture(Find("OperationStatus"));
        }
        private void OnEnable() { Apply(true); }
        private void LateUpdate() { Apply(false); }
        private void Capture(RectTransform r) { if (r != null && !original.ContainsKey(r)) original.Add(r, new Snapshot(r)); }

        private void Apply(bool force)
        {
            if (root == null || root.rect.width <= 1f || root.rect.height <= 1f) return;
            float width = root.rect.width, height = root.rect.height;
            LayoutMode next = width >= wideWidth ? LayoutMode.Wide : width >= mediumWidth ? LayoutMode.Medium : LayoutMode.Narrow;
            if (!force && next == mode) return; mode = next;
            foreach (KeyValuePair<RectTransform, Snapshot> pair in original) pair.Value.Restore(pair.Key);
            if (next == LayoutMode.Wide) ApplyWide(width, height); else if (next == LayoutMode.Medium) ApplyMedium(width, height); else ApplyNarrow(width, height);
        }
        private void ApplyWide(float width, float height)
        {
            SetRect(overview, edge, -100f, 260f, Mathf.Max(420f, height - 194f));
            float available = Mathf.Max(420f, width - edge * 2f - 260f - gap * 2f), column = Mathf.Clamp((available - gap) * .5f, 360f, 620f), x = edge + 260f + gap;
            SetPanel(primary, x, -202f, column, Mathf.Max(300f, height - 296f)); SetPanel(secondary, x + column + gap, -202f, column, Mathf.Max(300f, height - 296f));
            SetRect(Find("PlayerSummary"), x, -154f, column, 40f); SetRect(Find("ContainerSummary"), x + column + gap, -154f, column, 40f); SetFooter(width); SetInspection(false);
        }
        private void ApplyMedium(float width, float height)
        {
            SetRect(overview, edge, -100f, 176f, Mathf.Max(300f, height - 194f));
            float available = Mathf.Max(420f, width - edge * 2f - 176f - gap * 2f), column = Mathf.Clamp((available - gap) * .5f, 300f, 560f), x = edge + 176f + gap;
            SetPanel(primary, x, -202f, column, Mathf.Max(260f, height - 296f)); SetPanel(secondary, x + column + gap, -202f, column, Mathf.Max(260f, height - 296f));
            SetRect(Find("PlayerSummary"), x, -154f, column, 40f); SetRect(Find("ContainerSummary"), x + column + gap, -154f, column, 40f); SetFooter(width); SetInspection(false);
        }
        private void ApplyNarrow(float width, float height)
        {
            if (overview != null) overview.gameObject.SetActive(!hideOverviewOnNarrow);
            float contentWidth = Mathf.Max(280f, width - edge * 2f), panelHeight = Mathf.Max(220f, (height - 340f) * .5f);
            SetPanel(primary, edge, -120f, contentWidth, panelHeight); SetPanel(secondary, edge, -(120f + panelHeight + gap + 40f), contentWidth, panelHeight);
            SetRect(Find("PlayerSummary"), edge, -72f, contentWidth, 40f); SetRect(Find("ContainerSummary"), edge, -(120f + panelHeight + gap), contentWidth, 40f); SetFooter(width); SetInspection(true);
        }
        private void SetInspection(bool narrow)
        {
            if (inspection == null) return;
            if (!narrow) { inspection.anchorMin = new Vector2(.5f, .5f); inspection.anchorMax = inspection.anchorMin; inspection.pivot = new Vector2(.5f, .5f); inspection.anchoredPosition = Vector2.zero; return; }
            inspection.anchorMin = Vector2.zero; inspection.anchorMax = Vector2.one; inspection.pivot = new Vector2(.5f, .5f); inspection.offsetMin = new Vector2(edge, edge + 38f); inspection.offsetMax = new Vector2(-edge, -edge);
        }
        private void SetFooter(float width)
        {
            SetRect(Find("ActionHint"), edge, -Mathf.Max(0f, root.rect.height - 70f), Mathf.Max(240f, width - edge * 2f), 36f);
            SetRect(Find("OperationStatus"), edge, -Mathf.Max(0f, root.rect.height - 108f), Mathf.Max(240f, width - edge * 2f), 30f);
        }
        private void SetPanel(RectTransform r, float x, float y, float w, float h) { SetRect(r, x, y, w, h); }
        private static void SetRect(RectTransform r, float x, float y, float w, float h)
        {
            if (r == null) return;
            // Layout coordinates are authored from the top-left of the loadout root;
            // keep the same anchor convention when applying responsive dimensions.
            r.anchorMin = new Vector2(0f, 1f);
            r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, y);
            r.sizeDelta = new Vector2(w, h);
        }
        private RectTransform Find(string name) { return root == null ? null : FindRecursive(root, name); }
        private static RectTransform FindRecursive(Transform parent, string name)
        { for (int i = 0; i < parent.childCount; i++) { Transform c = parent.GetChild(i); if (c.name == name) return c as RectTransform; RectTransform f = FindRecursive(c, name); if (f != null) return f; } return null; }
    }
}
