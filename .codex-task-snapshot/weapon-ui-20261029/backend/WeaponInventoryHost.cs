using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    public sealed partial class InventoryHostCore
    {
        public bool CanExchangeWeaponMagazine(InventoryContainer weaponContainer, ItemId weaponId, ItemId magazineId, out string error)
            => CanExchangeWeaponChild(weaponContainer, weaponId, magazineId, true, default, out error);
        public bool TryExchangeWeaponMagazine(InventoryContainer weaponContainer, ItemId weaponId, ItemId magazineId, out string error)
            => TryExchangeWeaponChild(weaponContainer, weaponId, magazineId, true, default, out error);
        public bool CanExchangeWeaponAttachment(InventoryContainer weaponContainer, ItemId weaponId, WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
            => CanExchangeWeaponChild(weaponContainer, weaponId, attachmentId, false, slot, out error);
        public bool TryExchangeWeaponAttachment(InventoryContainer weaponContainer, ItemId weaponId, WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
            => TryExchangeWeaponChild(weaponContainer, weaponId, attachmentId, false, slot, out error);

        private bool ResolveWeaponExchange(InventoryContainer weaponContainer, ItemId weaponId, ItemId childId,
            out RuntimeItem weapon, out RuntimeItem child, out string error)
        {
            weapon = null; child = null; error = null;
            if (!ResolveAuthority() || !EnsureContainer(out error)) { error = error ?? "仅权威端可修改武器子物品。"; return false; }
            if (weaponContainer == null || !weaponContainer.TryGetItemById(weaponId, out weapon)) { error = "指定容器内没有目标武器。"; return false; }
            if (!childId.IsNone && !Container.TryGetItemById(childId, out child)) { error = "背包内没有候选子物品。"; return false; }
            return true;
        }

        private bool CanExchangeWeaponChild(InventoryContainer weaponContainer, ItemId weaponId, ItemId childId,
            bool magazine, WeaponAttachmentSlot slot, out string error)
        {
            if (!ResolveWeaponExchange(weaponContainer, weaponId, childId, out RuntimeItem weapon, out RuntimeItem child, out error)) return false;
            return magazine ? Container.CanExchangeWeaponMagazine(weaponContainer, weapon, child, Catalog, out error)
                : Container.CanExchangeWeaponAttachment(weaponContainer, weapon, slot, child, Catalog, out error);
        }

        private bool TryExchangeWeaponChild(InventoryContainer weaponContainer, ItemId weaponId, ItemId childId,
            bool magazine, WeaponAttachmentSlot slot, out string error)
        {
            if (!ResolveWeaponExchange(weaponContainer, weaponId, childId, out RuntimeItem weapon, out RuntimeItem child, out error)) return false;
            return Container.TryExchangeWeaponChild(weaponContainer, weapon, child, magazine, slot, Catalog, CommitConfirm, out error);
        }

        public bool CanLoadMagazine(ItemId magazineId, ItemId ammoId, int roundCount, out string error)
        {
            if (!ResolveAuthority()) { error = "仅权威端可装填弹匣。"; return false; }
            if (!TryResolveItem(magazineId, out RuntimeItem magazine, out error) || !TryResolveItem(ammoId, out RuntimeItem ammo, out error)) return false;
            return Container.CanLoadMagazine(magazine, ammo, roundCount, Catalog, out error);
        }

        public bool TryLoadMagazine(ItemId magazineId, ItemId ammoId, int roundCount, out string error)
        {
            if (!CanLoadMagazine(magazineId, ammoId, roundCount, out error)) return false;
            Container.TryGetItemById(magazineId, out RuntimeItem magazine);
            Container.TryGetItemById(ammoId, out RuntimeItem ammo);
            return Container.TryLoadMagazine(magazine, ammo, roundCount, Catalog, out error);
        }
    }
}
