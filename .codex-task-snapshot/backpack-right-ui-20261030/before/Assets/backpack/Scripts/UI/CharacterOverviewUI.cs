using System.Text;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Attributes = RPG4.CharacterAttributes.CharacterAttributes;

namespace RPG4.Backpack.UI
{
    /// <summary>库存中的角色概览、实际武器比较与本地显示设置；不执行装备操作。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(220)]
    public sealed class CharacterOverviewUI : MonoBehaviour
    {
        [SerializeField] private BackpackWindowUIData ui;
        [SerializeField] private BackpackContainerUI equipmentView;
        [SerializeField] private TMP_Text healthLabel;
        [SerializeField] private TMP_Text characterStatus;
        [SerializeField] private TMP_Text weaponName;
        [SerializeField] private TMP_Text weaponStats;
        [SerializeField] private TMP_Text selectionLabel;
        [SerializeField] private TMP_Text comparisonLabel;
        [SerializeField] private WeaponInspectionPanel inspection;
        [SerializeField] private Button detailsButton;
        [SerializeField] private Button inspectionButton;
        [SerializeField] private Color activeTabColor = new Color(0.12f, 0.34f, 0.38f, 1f);
        [SerializeField] private Color inactiveTabColor = new Color(0.08f, 0.17f, 0.21f, 1f);
        [SerializeField] private Button textScaleButton;
        [SerializeField] private Button hitFeedbackButton;
        [SerializeField] private Button lowHealthPulseButton;
        [SerializeField] private TMP_Text textScaleLabel;
        [SerializeField] private TMP_Text hitFeedbackLabel;
        [SerializeField] private TMP_Text lowHealthPulseLabel;
        private IBackpackView boundSource;
        private Attributes attributes;
        private RuntimeItem lastEquipped, lastPreview;
        private int lastEquippedRevision = -1, lastPreviewRevision = -1;
        private bool initialized;
        private bool showingInspection;
        private readonly StringBuilder comparison = new StringBuilder();
        private readonly List<RuntimeItem> equippedItems = new List<RuntimeItem>();

        private void Awake()
        {
            if (textScaleButton != null) textScaleButton.onClick.AddListener(PlayerUiPreferences.CycleTextScale);
            if (hitFeedbackButton != null) hitFeedbackButton.onClick.AddListener(PlayerUiPreferences.ToggleHitFeedback);
            if (lowHealthPulseButton != null) lowHealthPulseButton.onClick.AddListener(PlayerUiPreferences.ToggleLowHealthPulse);
            if (detailsButton != null) detailsButton.onClick.AddListener(ShowDetails);
            if (inspectionButton != null) inspectionButton.onClick.AddListener(ShowInspection);
        }
        private void OnEnable()
        {
            PlayerUiPreferences.Changed += RefreshPreferences;
            RefreshPreferences();
            ShowDetails();
        }
        private void OnDisable()
        {
            PlayerUiPreferences.Changed -= RefreshPreferences;
            initialized = false;
            boundSource = null;
            attributes = null;
        }
        private void OnDestroy()
        {
            if (textScaleButton != null) textScaleButton.onClick.RemoveListener(PlayerUiPreferences.CycleTextScale);
            if (hitFeedbackButton != null) hitFeedbackButton.onClick.RemoveListener(PlayerUiPreferences.ToggleHitFeedback);
            if (lowHealthPulseButton != null) lowHealthPulseButton.onClick.RemoveListener(PlayerUiPreferences.ToggleLowHealthPulse);
            if (detailsButton != null) detailsButton.onClick.RemoveListener(ShowDetails);
            if (inspectionButton != null) inspectionButton.onClick.RemoveListener(ShowInspection);
        }

        private void LateUpdate()
        {
            if (ui == null || ui.window == null || !ui.window.IsOpen)
            {
                initialized = false;
                if (showingInspection) ShowDetails();
                return;
            }
            IBackpackView source = ui.primaryView != null ? ui.primaryView.Inventory : null;
            if (!ReferenceEquals(source, boundSource))
            {
                boundSource = source;
                Component owner = source as Component;
                attributes = owner != null ? owner.GetComponent<Attributes>() : null;
                initialized = false;
            }
            SetText(healthLabel, attributes != null ? $"生命  {attributes.CurrentHealth:0.#} / {attributes.MaxHealth:0.#}" : "生命状态未就绪");
            SetText(characterStatus, attributes == null ? "等待角色就绪" : !attributes.IsAlive ? "角色已死亡"
                : attributes.NormalizedHealth <= 0.25f ? "生命危险" : "状态正常");

            RuntimeItem equipped = null;
            equippedItems.Clear();
            equipmentView?.Inventory?.CopyItems(equippedItems);
            if (equippedItems.Count > 0) equipped = equippedItems[0];
            BackpackInteractionController interaction = ui.interaction;
            bool busy = interaction == null || interaction.IsCarrying || interaction.IsChoosingSplitQuantity || interaction.IsWaitingForOperation;
            RuntimeItem hovered = !busy && interaction.HasPointer ? interaction.DisplayedItem : null;
            RuntimeItem preview = !busy ? hovered ?? interaction.SelectedItem : null;
            SetText(selectionLabel, showingInspection ? "武器装配 · F 检查"
                : preview == null ? "物品详情 · 悬停预览"
                : equipped != null && preview.ItemId == equipped.ItemId ? "物品详情 · 已装备"
                : hovered != null ? "物品详情 · 悬停预览" : "物品详情 · 已选中");
            if (textScaleButton != null) textScaleButton.interactable = !busy;
            if (hitFeedbackButton != null) hitFeedbackButton.interactable = !busy;
            if (lowHealthPulseButton != null) lowHealthPulseButton.interactable = !busy;
            if (inspectionButton != null) inspectionButton.interactable = !busy;
            int equippedRevision = equipped?.Revision ?? -1, previewRevision = preview?.Revision ?? -1;
            if (initialized && ReferenceEquals(equipped, lastEquipped) && ReferenceEquals(preview, lastPreview)
                && equippedRevision == lastEquippedRevision && previewRevision == lastPreviewRevision) return;
            initialized = true;
            lastEquipped = equipped; lastPreview = preview;
            lastEquippedRevision = equippedRevision; lastPreviewRevision = previewRevision;
            ItemManifestCatalog catalog = ui.itemDetails != null ? ui.itemDetails.catalog : null;
            SetText(weaponName, equipped != null ? WeaponUiSnapshots.Describe(equipped, catalog).Name + " · 已装备" : "未装备武器");
            SetText(weaponStats, equipped != null ? Stats(equipped, catalog) : "将武器放入装备槽，\n或使用背包中的快速装备。");
            SetText(comparisonLabel, Compare(preview, equipped, catalog));
        }

        private string Compare(RuntimeItem candidate, RuntimeItem equipped, ItemManifestCatalog catalog)
        {
            if (candidate?.WeaponState == null || equipped?.WeaponState == null || candidate.ItemId == equipped.ItemId) return string.Empty;
            if (!TryParameters(candidate, catalog, out WeaponCombatParameters next)
                || !TryParameters(equipped, catalog, out WeaponCombatParameters current))
                return "实战对比暂不可用\n装入有效弹药后，按实际配件与弹药计算。";
            comparison.Clear();
            comparison.AppendLine("与当前装备相比（各自当前弹药）");
            AppendDelta("伤害", next.Damage - current.Damage, false, "");
            AppendDelta("射速", 60f / next.Interval - 60f / current.Interval, false, " 发/分");
            AppendDelta("散布", next.Spread - current.Spread, true, "°");
            AppendDelta("后坐力", next.Recoil - current.Recoil, true, "×");
            return comparison.ToString().TrimEnd();
        }
        private static string Stats(RuntimeItem item, ItemManifestCatalog catalog)
        {
            if (TryParameters(item, catalog, out WeaponCombatParameters value))
                return $"伤害 {value.Damage:0.#}\n射速 {60f / value.Interval:0} 发/分\n散布 {value.Spread:0.##}°\n后坐力 {value.Recoil:0.##}×";
            WeaponDefinitionFragment definition = item.WeaponState.Definition;
            return $"基础参数\n伤害 {definition.BaseDamage:0.#}\n射速 {60f / definition.ShotInterval:0} 发/分\n散布 {definition.SpreadAngle:0.##}°";
        }
        private static bool TryParameters(RuntimeItem item, ItemManifestCatalog catalog, out WeaponCombatParameters value)
        {
            BackpackTag ammo = item.WeaponState.ChamberAmmo;
            if (ammo.IsEmpty && item.WeaponState.Magazine?.MagazineState.RoundCount > 0)
                ammo = item.WeaponState.Magazine.MagazineState.Rounds[0];
            return WeaponParameterCalculator.TryCalculate(item, catalog, ammo, out value);
        }
        private void AppendDelta(string label, float delta, bool lowerIsBetter, string unit)
        {
            bool equal = Mathf.Abs(delta) < 0.001f;
            bool better = lowerIsBetter ? delta < 0f : delta > 0f;
            string color = equal ? "#CBD7DE" : better ? "#7FE0B1" : "#FF998C";
            comparison.Append(label).Append("  <color=").Append(color).Append('>')
                .Append(equal ? "— 0" : delta > 0f ? "↑ +" : "↓ ")
                .Append(equal ? string.Empty : delta.ToString("0.##"))
                .Append(unit).AppendLine("</color>");
        }
        private void RefreshPreferences()
        {
            SetText(textScaleLabel, $"文字 {PlayerUiPreferences.TextScale * 100f:0}%");
            SetText(hitFeedbackLabel, "命中反馈：" + (PlayerUiPreferences.HitFeedback ? "开" : "关"));
            SetText(lowHealthPulseLabel, "低血量脉冲：" + (PlayerUiPreferences.LowHealthPulse ? "开" : "关"));
        }
        private void ShowDetails() => SetInspection(false);
        private void ShowInspection()
        {
            if (inspection == null || ui == null || ui.interaction == null || ui.interaction.IsCarrying) return;
            RuntimeItem candidate = ui.interaction.SelectedItem ?? lastPreview;
            if (candidate != null && candidate.WeaponState == null && candidate.MagazineState == null) candidate = null;
            if (candidate != null && !(ui.primaryView?.Inventory?.TryGetPlacement(candidate.ItemId, out _) ?? false)
                && !(equipmentView?.Inventory?.TryGetPlacement(candidate.ItemId, out _) ?? false)) candidate = null;
            inspection.Inspect(candidate ?? lastEquipped);
            SetInspection(true);
        }
        public void InspectAtPointer(Vector2 pointer)
        {
            if (inspection == null || ui == null || ui.interaction == null
                || !ui.interaction.TryGetItemAtPointer(pointer, out RuntimeItem item, out _)) return;
            inspection.InspectAtPointer(pointer);
            if (inspection.InspectedItemId == item.ItemId) SetInspection(true);
        }
        private void SetInspection(bool value)
        {
            showingInspection = value;
            if (detailsButton != null && detailsButton.targetGraphic != null)
                detailsButton.targetGraphic.color = value ? inactiveTabColor : activeTabColor;
            if (inspectionButton != null && inspectionButton.targetGraphic != null)
                inspectionButton.targetGraphic.color = value ? activeTabColor : inactiveTabColor;
            if (inspection != null) inspection.SetPresentationVisible(value);
            if (ui != null && ui.itemDetails != null) ui.itemDetails.gameObject.SetActive(!value);
            if (comparisonLabel != null) comparisonLabel.gameObject.SetActive(!value);
        }
        private static void SetText(TMP_Text label, string value)
        {
            if (label != null && label.text != value) label.text = value;
        }
    }
}
