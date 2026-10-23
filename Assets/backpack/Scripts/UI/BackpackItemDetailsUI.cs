using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.UI
{
    /// <summary>只读物品详情；名称查目录，属性读取实际运行时片段，不执行装备或消耗。</summary>
    [DisallowMultipleComponent]
    public sealed class BackpackItemDetailsUI : MonoBehaviour
    {
        private static readonly BackpackTag Weapons = new BackpackTag("GameItems.Equipment.Weapons");
        private static readonly BackpackTag Armors = new BackpackTag("GameItems.Equipment.Armors");
        private static readonly BackpackTag Cloaks = new BackpackTag("GameItems.Equipment.Cloaks");
        private static readonly BackpackTag Consumables = new BackpackTag("GameItems.Consumables");
        private static readonly BackpackTag Materials = new BackpackTag("GameItems.Craftables");
        public ItemManifestCatalog catalog;
        public Image icon;
        public TMP_Text itemName;
        public TMP_Text itemType;
        public TMP_Text itemSize;
        public TMP_Text stackCount;
        public TMP_Text properties;
        public GameObject emptyState;
        public GameObject contentRoot;
        public CanvasGroup group;
        public RectTransform rectTransform;

        private RuntimeItem displayedItem;
        private int displayedCount = -1;
        private bool initialized;
        private readonly StringBuilder text = new StringBuilder();
        private readonly Vector3[] corners = new Vector3[4];
        private Canvas canvas;

        public void Show(RuntimeItem item)
        {
            if (initialized && ReferenceEquals(item, displayedItem) && (item == null || displayedCount == item.StackCount)) return;
            initialized = true;
            displayedItem = item;
            displayedCount = item != null ? item.StackCount : -1;
            if (group != null)
            {
                group.alpha = item != null ? 1f : 0f;
                group.interactable = false;
                group.blocksRaycasts = false;
                group.ignoreParentGroups = false;
            }
            if (emptyState != null) emptyState.SetActive(item == null);
            if (contentRoot != null) contentRoot.SetActive(item != null);
            if (icon != null)
            {
                icon.sprite = item?.GetFragment<ImageFragment>()?.Icon;
                icon.enabled = icon.sprite != null;
                icon.preserveAspect = true;
            }
            SetText(itemName, item != null ? ResolveName(item) : string.Empty);
            SetText(itemType, item != null ? "类型  " + ResolveCategory(item) : string.Empty);
            SetText(itemSize, item != null ? $"占格  {item.Size.X} × {item.Size.Y}" : string.Empty);
            SetText(stackCount, item != null ? $"堆叠  {item.StackCount} / {item.MaxStackSize}" : string.Empty);
            SetText(properties, item != null ? BuildProperties(item) : string.Empty);
        }

        public void SetPointer(Vector2 point)
        {
            if (displayedItem == null || rectTransform == null) return;
            if (canvas == null)
            {
                Canvas parent = GetComponentInParent<Canvas>();
                canvas = parent != null ? parent.rootCanvas : null;
            }
            if (canvas == null || !(canvas.transform is RectTransform canvasRect)) return;
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, point, camera, out Vector2 local)) return;
            rectTransform.position = canvasRect.TransformPoint(local + new Vector2(20f, -20f));
            rectTransform.GetWorldCorners(corners);
            Vector2 minimum = canvasRect.InverseTransformPoint(corners[0]);
            Vector2 maximum = minimum;
            for (int i = 1; i < corners.Length; i++)
            {
                Vector2 corner = canvasRect.InverseTransformPoint(corners[i]);
                minimum = Vector2.Min(minimum, corner);
                maximum = Vector2.Max(maximum, corner);
            }
            Rect bounds = canvasRect.rect;
            // 空间不足时先翻到指针另一侧，再夹紧；浮层通常不会覆盖鼠标下的网格物品。
            Vector2 flip = new Vector2(
                maximum.x > bounds.xMax ? local.x - 20f - maximum.x : 0f,
                minimum.y < bounds.yMin ? local.y + 20f - minimum.y : 0f);
            minimum += flip;
            maximum += flip;
            Vector2 correction = new Vector2(
                minimum.x < bounds.xMin ? bounds.xMin - minimum.x : maximum.x > bounds.xMax ? bounds.xMax - maximum.x : 0f,
                minimum.y < bounds.yMin ? bounds.yMin - minimum.y : maximum.y > bounds.yMax ? bounds.yMax - maximum.y : 0f);
            rectTransform.position += canvasRect.TransformVector(flip + correction);
        }

        private string ResolveName(RuntimeItem item)
        {
            if (catalog != null && catalog.TryGet(item.ItemType, out ItemManifest manifest) && manifest != null)
                return manifest.name;
            return LastSegment(item.ItemType.Name);
        }

        private static string ResolveCategory(RuntimeItem item)
        {
            if (item.ItemType.MatchesTag(Weapons)) return "武器";
            if (item.ItemType.MatchesTag(Armors)) return "护甲";
            if (item.ItemType.MatchesTag(Cloaks)) return "披风";
            if (item.ItemType.MatchesTag(Consumables) || item.GetFragment<ConsumableFragment>() != null) return "消耗品";
            if (item.ItemType.MatchesTag(Materials)) return "材料";
            return "物品";
        }

        private static string LastSegment(string tag)
        {
            int separator = tag.LastIndexOf('.');
            return separator >= 0 ? tag.Substring(separator + 1) : tag;
        }

        private static string ResolveNumberLabel(LabeledNumberFragment number)
        {
            switch (number.Label)
            {
                case "Damage": return "伤害";
                case "Armor": return "护甲";
                case "Heal": return "恢复生命";
                case "": return LastSegment(number.FragmentTag.Name);
                default: return number.Label;
            }
        }

        private string BuildProperties(RuntimeItem item)
        {
            text.Clear();
            foreach (ItemFragment fragment in item.Fragments)
            {
                if (fragment is LabeledNumberFragment number) AppendNumber(number);
                else if (fragment is TextFragment description && !string.IsNullOrWhiteSpace(description.Text))
                    text.AppendLine(description.Text);
                else if (fragment is ConsumableFragment consumable)
                {
                    foreach (ConsumeModifier modifier in consumable.Modifiers) AppendNumber(modifier);
                }
                else if (fragment is EquipmentFragment equipment)
                {
                    foreach (EquipModifier modifier in equipment.Modifiers) AppendNumber(modifier);
                }
            }
            return text.Length > 0 ? text.ToString().TrimEnd() : "暂无额外属性";
        }

        private void AppendNumber(LabeledNumberFragment number)
        {
            if (number == null || (number.CollapseLabel && number.CollapseValue)) return;
            if (!number.CollapseLabel)
                text.Append(ResolveNumberLabel(number));
            if (!number.CollapseValue)
            {
                if (!number.CollapseLabel) text.Append("  ");
                int minimum = Mathf.Clamp(number.MinFractionalDigits, 0, 8);
                int maximum = Mathf.Clamp(number.MaxFractionalDigits, minimum, 8);
                string format = maximum == 0 ? "0" : "0." + new string('0', minimum) + new string('#', maximum - minimum);
                text.Append(number.Value.ToString(format, CultureInfo.InvariantCulture));
            }
            text.AppendLine();
        }

        private static void SetText(TMP_Text label, string value)
        {
            if (label != null && label.text != value) label.text = value;
        }
    }
}
