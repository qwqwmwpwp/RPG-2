using TMPro;
using UnityEngine;
using UnityEngine.UI;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using System;

namespace RPG4.Backpack.UI
{
    /// <summary>可复用候选行；选择事件以所属检查面板为来源隔离。</summary>
    [DisallowMultipleComponent]
    public sealed class WeaponCandidateRow : MonoBehaviour
    {
        public Button selectButton;
        public TMP_Text itemName;
        public TMP_Text description;
        public Image icon;
        private object owner;
        private int index;
        private Action<int> assemblySelection;

        private void Awake()
        {
            if (selectButton != null) selectButton.onClick.AddListener(Select);
            Configure(itemName, true);
            Configure(description, false);
        }

        private static void Configure(TMP_Text text, bool singleLine)
        {
            if (text == null) return;
            text.enableWordWrapping = !singleLine;
            text.overflowMode = singleLine ? TextOverflowModes.Ellipsis : TextOverflowModes.Truncate;
        }

        private void OnDestroy()
        {
            if (selectButton != null) selectButton.onClick.RemoveListener(Select);
        }

        public void Bind(object panel, int candidateIndex, WeaponUiCandidate candidate, bool waiting)
        {
            owner = panel; assemblySelection = null;
            index = candidateIndex;
            if (itemName != null) itemName.text = candidate.Item.Name;
            if (description != null) description.text = !candidate.CanApply ? candidate.Reason
                : candidate.Kind == WeaponOperationKind.LoadMagazine ? $"库存 {candidate.Item.Count} · 可装填 {candidate.MaxCount} 发 · 点击选择"
                : candidate.Kind == WeaponOperationKind.ExchangeMagazine ? "点击装配弹匣" : "点击装配枪口";
            if (description != null && !string.IsNullOrEmpty(candidate.CombatSummary))
                description.text += "\n" + candidate.CombatSummary;
            if (icon != null)
            {
                icon.sprite = candidate.Item.Icon;
                icon.enabled = icon.sprite != null;
                icon.preserveAspect = true;
            }
            if (selectButton != null) selectButton.interactable = candidate.CanApply && !waiting;
        }

        public void BindAssembly(Action<int> onSelect, int candidateIndex, AssemblyCandidateSnapshot candidate, bool selected, bool waiting)
        {
            owner = null; assemblySelection = onSelect; index = candidateIndex;
            if (itemName != null) itemName.text = candidate.Item.Name + (selected ? " · 已预览" : "");
            if (description != null) description.text = !candidate.CanApply ? candidate.Reason
                : candidate.Dimensions.X + "×" + candidate.Dimensions.Y + " · 点击预览";
            if (description != null && !string.IsNullOrEmpty(candidate.CombatSummary)) description.text += "\n" + candidate.CombatSummary;
            if (icon != null) { icon.sprite = candidate.Item.Icon; icon.enabled = icon.sprite != null; icon.preserveAspect = true; }
            // 不可用项仍可点选查看原因与冲突定位；安装由面板单独门控。
            if (selectButton != null) selectButton.interactable = !waiting;
        }

        private void Select()
        {
            if (assemblySelection != null) { assemblySelection(index); return; }
            if (owner != null) EventBus.Global.Publish(UiChannels.WeaponCandidateSelected, owner, index);
        }
    }
}
