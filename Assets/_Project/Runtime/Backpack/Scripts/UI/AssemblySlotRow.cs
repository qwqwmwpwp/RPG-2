using TMPro;
using UnityEngine;
using UnityEngine.UI;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;

namespace RPG4.Backpack.UI
{
    [DisallowMultipleComponent]
    public sealed class AssemblySlotRow : MonoBehaviour
    {
        public Button selectButton;
        public TMP_Text slotName;
        public TMP_Text installedName;
        public TMP_Text state;
        public Button removeButton;
        private WeaponInspectionPanel owner;
        private AssemblySlotSnapshot slot;

        private void Awake()
        {
            if (selectButton != null) selectButton.onClick.AddListener(Select);
            if (removeButton != null) removeButton.onClick.AddListener(Remove);
        }
        private void OnDestroy()
        {
            if (selectButton != null) selectButton.onClick.RemoveListener(Select);
            if (removeButton != null) removeButton.onClick.RemoveListener(Remove);
        }
        public void Bind(WeaponInspectionPanel panel, AssemblySlotSnapshot value, bool selected, bool waiting)
        {
            owner = panel; slot = value;
            if (slotName != null) slotName.text = (value.Depth > 0 ? new string('　', Mathf.Clamp(value.Depth, 0, 6)) + "↳ " : string.Empty)
                + value.DisplayName + (value.Required ? " · 必需" : "");
            if (installedName != null) installedName.text = (value.InstalledItem?.Name ?? "未安装")
                + (state == null && !string.IsNullOrEmpty(value.Reason) ? " · " + value.Reason : string.Empty);
            if (state != null)
            {
                state.text = selected ? "当前槽位 · 已选中" : value.InstalledItem != null ? (value.CanRemove ? "已安装 · 可卸下" : "已安装 · 不可卸下")
                    : !string.IsNullOrEmpty(value.Reason) ? "不可用 · " + value.Reason : value.Required ? "必需 · 待安装" : "可安装";
                state.color = selected ? new Color(0.34f, 0.88f, 1f) : value.InstalledItem != null && value.CanRemove ? new Color(0.45f, 0.9f, 0.7f) : Color.white;
            }
            if (selectButton != null) selectButton.interactable = !waiting && string.IsNullOrEmpty(value.Reason);
            if (removeButton != null)
            {
                removeButton.gameObject.SetActive(value.InstalledItem != null);
                removeButton.interactable = value.InstalledItem != null && value.CanRemove && !waiting;
            }
        }
        private void Select() { if (owner != null && slot != null) owner.SelectAssemblySlot(slot); }
        private void Remove() { if (owner != null && slot != null) owner.RemoveAssemblySlot(slot); }
    }
}
