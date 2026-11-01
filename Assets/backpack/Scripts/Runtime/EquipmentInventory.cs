using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.Backpack.Runtime
{
    /// <summary>本地角色的单格武器容器；槽内物品独立归属，背包间操作复用库存事务。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/Equipment Inventory")]
    public sealed class EquipmentInventory : MonoBehaviour, IBackpackView, IInventoryOperations
    {
        private bool networkReadOnly;
        public InventoryHostCore AuthorityCore => core;
        public void SetNetworkAuthority(bool authority)
        {
            networkReadOnly = !authority;
            if (core == null) return;
            core.IsAuthority = () => !networkReadOnly;
            if (networkReadOnly) core.UnsubscribeCommands();
            else if (isActiveAndEnabled) core.SubscribeCommands(core.Container);
        }

        private InventoryHostCore core;
        private ItemAdmissionHandler additionalAdmission;
        private CommitConfirmHandler commitConfirm;

        public InventoryContainer Container => core?.Container;
        public RuntimeItem EquippedItem => Container?.GetItemAt(0);
        public bool IsReady => !networkReadOnly && Container != null;
        public int Rows => IsReady ? 1 : 0;
        public int Columns => IsReady ? 1 : 0;
        public int ConfiguredRows => 1;
        public int ConfiguredColumns => 1;
        public object EventSource => Container;

        /// <summary>角色业务附加检查；只查询，不创建模型或变更库存。</summary>
        public ItemAdmissionHandler AdditionalAdmission
        {
            get => additionalAdmission;
            set => additionalAdmission = value;
        }

        /// <summary>双方静默提交之后、通知之前确认装备运行实例；失败后容器回滚并补偿确认。</summary>
        public CommitConfirmHandler CommitConfirm
        {
            get => commitConfirm;
            set
            {
                commitConfirm = value;
                if (core != null) core.CommitConfirm = value;
            }
        }

        private void Awake()
        {
            foreach (MonoBehaviour component in GetComponents<MonoBehaviour>())
                if (component is ISharedInventoryAccess access && access.IsNetworkSessionActive && !access.IsServerSession)
                    networkReadOnly = true;
            core = new InventoryHostCore(1, 1, LogModule.Equipment, name, true, CanAcceptItem);
            core.CommitConfirm = commitConfirm;
            core.IsAuthority = () => !networkReadOnly;
        }

        private void OnEnable() { if (!networkReadOnly) core?.SubscribeCommands(Container); }
        private void OnDisable() => core?.UnsubscribeCommands();

        public GridCoord ResolveItemDimensions(RuntimeItem item) => GridCoord.One;

        public bool CanAcceptItem(RuntimeItem item, out string error)
        {
            error = null;
            if (item == null || item.StackCount != 1)
            {
                error = "武器槽只能容纳一件武器。";
                return false;
            }
            EquipmentFragment equipment = item.GetFragment<EquipmentFragment>();
            WeaponDefinitionFragment definition = item.GetFragment<WeaponDefinitionFragment>();
            if (equipment == null || !equipment.FragmentTag.MatchesTagExact(FragmentTags.EquipmentFragment)
                || equipment.EquipmentType.IsEmpty || !equipment.EquipmentType.IsValid
                || definition == null)
            {
                error = "物品缺少有效武器定义或装备类型。";
                return false;
            }
            try { definition.Validate(); }
            catch (ArgumentException exception) { error = exception.Message; return false; }
            catch (InvalidOperationException exception) { error = exception.Message; return false; }
            return additionalAdmission == null || additionalAdmission(item, out error);
        }

        public bool CanSubmitTo(IBackpackView destination) => core != null && core.CanSubmitTo(destination);
        public bool HasEmptySpace(GridCoord dimensions) => IsReady && EquippedItem == null;

        public void CopyItems(List<RuntimeItem> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (Container == null) destination.Clear();
            else Container.FillItemsByItemId(destination);
        }

        public bool TryGetPlacement(ItemId itemId, out InventoryPlacement placement)
        {
            placement = InventoryPlacement.Invalid;
            return Container != null && Container.TryGetItemById(itemId, out RuntimeItem item)
                && Container.TryGetPlacement(item, out placement);
        }

        public bool TryEquip(BackpackInventory source, ItemId itemId, out string error)
        {
            error = null;
            if (!isActiveAndEnabled || !IsReady || source == null || !source.isActiveAndEnabled
                || !source.CanSubmitTo(this))
            {
                error = "来源背包或武器槽尚未就绪，或不支持本地装备操作。";
                return false;
            }
            RuntimeItem previous = EquippedItem;
            InventoryOperationResult result = source.ExecuteOperation(new InventoryOperation(0,
                previous == null ? InventoryOperationKind.TransferItem : InventoryOperationKind.SwapItems,
                itemId, previous?.ItemId ?? ItemId.None, destination: this, upperLeftIndex: 0));
            error = result.Error;
            return result.Success;
        }

        public bool TryUnequip(BackpackInventory destination, out string error)
        {
            error = null;
            if (!isActiveAndEnabled || !IsReady || destination == null || !destination.isActiveAndEnabled
                || !CanSubmitTo(destination))
            {
                error = "目标背包或武器槽尚未就绪。";
                return false;
            }
            RuntimeItem item = EquippedItem;
            if (item == null) return true;
            InventoryOperationResult result = core.ExecuteOperation(new InventoryOperation(0,
                InventoryOperationKind.TransferItem, item.ItemId, destination: destination));
            error = result.Error;
            return result.Success;
        }
    }
}
