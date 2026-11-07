using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>固定操作区始终操作已选中的持久 ItemId，不读取临时悬停对象。</summary>
    [DisallowMultipleComponent]
    public sealed class ConsumableItemActionsUI : MonoBehaviour
    {
        [SerializeField] private BackpackInteractionController interaction;
        [SerializeField] private Button useButton;
        [SerializeField] private Button quickButton;
        [SerializeField] private TMP_Text selectionLabel;
        private ItemId selectedId;
        private IBackpackView selectedSource;

        private void Awake()
        {
            useButton?.onClick.AddListener(UseSelected);
            quickButton?.onClick.AddListener(SetQuick);
        }
        private void OnDestroy()
        {
            useButton?.onClick.RemoveListener(UseSelected);
            quickButton?.onClick.RemoveListener(SetQuick);
        }
        private void LateUpdate()
        {
            RuntimeItem item = interaction != null ? interaction.SelectedItem : null;
            IBackpackView local = LocalBackpackSourceResolver.Resolve();
            bool owned = item != null && interaction != null && ReferenceEquals(interaction.SelectedInventory, local);
            selectedSource = owned ? local : null;
            selectedId = owned ? item.ItemId : ItemId.None;
            bool idle = interaction != null && !interaction.IsCarrying && !interaction.IsChoosingSplitQuantity
                && !interaction.IsWaitingForOperation;
            bool usable = owned && ConsumableQuickUse.IsUsable(item);
            bool active = local != null && InventoryHostCore.TryGetCommandHost(local.EventSource, out InventoryHostCore host)
                && host.GetConsumableUseState().Active;
            if (useButton != null) useButton.interactable = usable && idle && !active && !InventoryUIInputRouter.IsConsumablePending;
            if (quickButton != null) quickButton.interactable = usable && idle;
            if (selectionLabel != null) selectionLabel.text = !owned ? "选择自己背包中的药品" : !usable ? "当前物品不可用药"
                : (ConsumableQuickUse.SelectedType == item.ItemType.Name ? "已设为快捷药品" : "已选中药品") + " · H 使用一份";
        }
        private RuntimeItem CurrentSelection()
        {
            IBackpackView local = LocalBackpackSourceResolver.Resolve();
            return ReferenceEquals(local, selectedSource) && interaction != null
                && ReferenceEquals(interaction.SelectedInventory, local) && interaction.SelectedItem?.ItemId == selectedId
                ? ConsumableQuickUse.Find(local, selectedId) : null;
        }
        private void UseSelected()
        {
            RuntimeItem item = CurrentSelection();
            if (ConsumableQuickUse.IsUsable(item)) InventoryUIInputRouter.RequestConsumable(selectedSource, item.ItemId,
                GetComponent<UIBase>());
        }
        private void SetQuick()
        {
            RuntimeItem item = CurrentSelection();
            if (ConsumableQuickUse.IsUsable(item)) ConsumableQuickUse.Select(item);
        }
    }
}
