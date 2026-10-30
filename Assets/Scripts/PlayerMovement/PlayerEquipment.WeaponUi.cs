using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Equipment;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.PlayerMovement
{
    public sealed partial class PlayerEquipment
    {
        private object weaponUiSource;
        private object weaponUiEquipmentSource;
        private EventSubscription weaponUiRequest;
        private EventSubscription weaponUiInventoryChanged;
        private EventSubscription weaponUiEquipmentChanged;
        private EventSubscription weaponUiActionChanged;
        private readonly Dictionary<int, WeaponSnapshotRequest> weaponUiInspections = new Dictionary<int, WeaponSnapshotRequest>();
        private readonly List<RuntimeItem> weaponUiItems = new List<RuntimeItem>();
        private bool weaponUiDirty;
        private Weapon weaponUiObservedAction;
        private bool weaponUiWasReloading;
        private WeaponUiActionStage weaponUiActionStage;

        private void BindWeaponUiEvents()
        {
            object source = InventoryAvailable ? inventory.EventSource : null;
            object equipmentSource = EquipmentAvailable ? equipmentInventory.EventSource : null;
            if (ReferenceEquals(source, weaponUiSource) && ReferenceEquals(equipmentSource, weaponUiEquipmentSource)
                && (source == null || weaponUiRequest.IsAlive)) return;
            UnbindWeaponUiEvents();
            weaponUiSource = source;
            weaponUiEquipmentSource = equipmentSource;
            if (source == null) return;
            inventory.ConfigureWeaponOperations(ResolveOwnedWeaponContainer, WeaponUiBlockReason);
            weaponUiRequest = EventBus.Global.Subscribe<WeaponSnapshotRequest>(CmdChannels.WeaponSnapshot,
                source, this, OnWeaponSnapshotRequest);
            weaponUiInventoryChanged = EventBus.Global.Subscribe(InvChannels.Changed, source, this, MarkWeaponUiDirty);
            if (equipmentSource != null)
                weaponUiEquipmentChanged = EventBus.Global.Subscribe(InvChannels.Changed,
                    equipmentSource, this, MarkWeaponUiDirty);
            BindWeaponUiAction();
            weaponUiDirty = true;
        }

        private InventoryContainer ResolveOwnedWeaponContainer(ItemId id)
        {
            if (!InventoryAvailable || !EquipmentAvailable) return null;
            if (inventory.Container.TryGetItemById(id, out _)) return inventory.Container;
            return equipmentInventory.Container.TryGetItemById(id, out _) ? equipmentInventory.Container : null;
        }

        private string WeaponUiBlockReason(ItemId id)
        {
            if (!isActiveAndEnabled || !InventoryAvailable || !EquipmentAvailable) return "角色背包或武器槽尚未就绪。";
            if (equipmentInventory.EquippedItem?.ItemId != id) return null;
            if (CurrentWeapon == null || CurrentWeapon.ItemId != id) return "当前武器尚未就绪。";
            return current.IsReloading ? "当前武器正在换弹。" : null;
        }

        private string WeaponUiRejection(WeaponOperation operation)
            => inventory.CanExecuteWeaponOperation(operation, out string error) ? null : error;

        private void OnWeaponSnapshotRequest(WeaponSnapshotRequest request)
        {
            if (!request.HudOnly)
            {
                if (request.InspectedItemId.IsNone) weaponUiInspections.Remove(request.SessionId);
                else weaponUiInspections[request.SessionId] = request;
                PublishWeaponInspection(request);
            }
            PublishWeaponHud();
        }

        private void PublishWeaponInspection(WeaponSnapshotRequest request)
        {
            if (weaponUiSource == null) return;
            InventoryContainer container = ResolveOwnedWeaponContainer(request.InspectedItemId);
            RuntimeItem item = null;
            container?.TryGetItemById(request.InspectedItemId, out item);
            if (item != null && item.WeaponState == null && item.MagazineState == null) item = null;
            inventory.CopyItems(weaponUiItems);
            var snapshot = WeaponUiSnapshots.Inspection(item, weaponUiItems, inventory.Catalog,
                equipmentInventory.EquippedItem?.ItemId ?? ItemId.None, request, WeaponUiRejection,
                item == null && request.InspectedItemId.IsValid ? "所选物品已离开玩家库存。" : WeaponUiBlockReason(request.InspectedItemId));
            EventBus.Global.Publish(InvChannels.WeaponInspectionChanged, weaponUiSource, snapshot);
        }

        private void PublishWeaponHud()
        {
            if (weaponUiSource == null) return;
            RuntimeItem item = EquipmentAvailable ? equipmentInventory.EquippedItem : null;
            WeaponItemState state = item?.WeaponState;
            MagazineItemState magazine = state?.Magazine?.MagazineState;
            int spareMagazines = 0, looseAmmo = 0;
            if (state != null)
            {
                inventory.CopyItems(weaponUiItems);
                foreach (RuntimeItem candidate in weaponUiItems)
                {
                    if (candidate.MagazineState?.RoundCount > 0 && state.IsCompatibleMagazine(candidate, inventory.Catalog)) spareMagazines++;
                    AmmoDefinitionFragment ammo = candidate.GetFragment<AmmoDefinitionFragment>();
                    if (ammo != null && candidate.WeaponState == null && candidate.MagazineState == null
                        && ammo.AmmoType.MatchesTagExact(state.Definition.AmmoType)) looseAmmo += candidate.StackCount;
                }
            }
            bool hasAction = current != null && item != null && current.ItemId == item.ItemId;
            var snapshot = new WeaponHudSnapshot(WeaponUiSnapshots.Describe(item, inventory.Catalog),
                state == null || state.ChamberAmmo.IsEmpty ? 0 : 1, magazine?.RoundCount ?? 0, magazine?.Capacity ?? 0,
                state?.Durability ?? 0f, state?.Definition.MaxDurability ?? 0f,
                hasAction ? weaponUiActionStage : WeaponUiActionStage.None,
                hasAction ? current.ReloadStartTime : 0d, hasAction ? current.Definition.ReloadDuration : 0f,
                spareMagazines, looseAmmo, state == null ? null : state.Definition.FireMode == WeaponFireMode.Automatic
                    ? "全自动" : "半自动", WeaponHudStatus(state));
            EventBus.Global.Publish(InvChannels.WeaponHudChanged, weaponUiSource, snapshot);
        }

        private void MarkWeaponUiDirty() => weaponUiDirty = true;

        private string WeaponHudStatus(WeaponItemState state)
        {
            if (state == null) return null;
            if (state.Durability <= 0f) return "武器已损坏";
            if (state.Durability < state.Definition.DurabilityPerShot) return "耐久不足，无法开火";
            if (state.ChamberAmmo.IsEmpty)
                return state.Magazine?.MagazineState.RoundCount > 0 ? "未上膛" : state.Magazine == null ? "未安装弹匣" : "弹药耗尽";
            return state.CanConsumeShot(inventory.Catalog) ? null : "当前弹药不可用";
        }

        private void BindWeaponUiAction()
        {
            if (ReferenceEquals(weaponUiObservedAction, current)) return;
            weaponUiActionChanged.Dispose();
            weaponUiActionChanged = default;
            weaponUiObservedAction = current;
            weaponUiWasReloading = current != null && current.IsReloading;
            weaponUiActionStage = current == null ? WeaponUiActionStage.None : (WeaponUiActionStage)current.ReloadStage;
            if (current != null)
                weaponUiActionChanged = EventBus.Global.Subscribe(InvChannels.WeaponActionChanged,
                    current, this, OnWeaponUiActionChanged);
            weaponUiDirty = true;
        }

        private void OnWeaponUiActionChanged()
        {
            Weapon weapon = weaponUiObservedAction;
            if (weapon == null) return;
            weaponUiActionStage = weaponUiWasReloading && !weapon.IsReloading && weapon.ReloadStage == WeaponReloadStage.None
                ? WeaponUiActionStage.Cancelled : (WeaponUiActionStage)weapon.ReloadStage;
            weaponUiWasReloading = weapon.IsReloading;
            weaponUiDirty = true;
        }

        private void FlushWeaponUi()
        {
            if (!weaponUiDirty || weaponUiSource == null) return;
            weaponUiDirty = false;
            PublishWeaponHud();
            foreach (WeaponSnapshotRequest request in new List<WeaponSnapshotRequest>(weaponUiInspections.Values))
                PublishWeaponInspection(request);
        }

        private void UnbindWeaponUiEvents()
        {
            weaponUiRequest.Dispose(); weaponUiInventoryChanged.Dispose(); weaponUiEquipmentChanged.Dispose();
            weaponUiRequest = weaponUiInventoryChanged = weaponUiEquipmentChanged = default;
            weaponUiActionChanged.Dispose();
            weaponUiActionChanged = default;
            weaponUiObservedAction = null;
            if (weaponUiSource != null)
            {
                EventBus.Global.Publish(InvChannels.WeaponHudChanged, weaponUiSource,
                    new WeaponHudSnapshot(null, 0, 0, 0, 0f, 0f, WeaponUiActionStage.None, 0d, 0f));
                foreach (WeaponSnapshotRequest request in new List<WeaponSnapshotRequest>(weaponUiInspections.Values))
                    EventBus.Global.Publish(InvChannels.WeaponInspectionChanged, weaponUiSource,
                        WeaponUiSnapshots.Inspection(null, weaponUiItems, inventory.Catalog, ItemId.None, request, WeaponUiRejection));
                inventory.ConfigureWeaponOperations(null, _ => "玩家武器交互已停用。");
            }
            weaponUiSource = weaponUiEquipmentSource = null;
            weaponUiInspections.Clear();
            weaponUiDirty = false;
        }
    }
}
