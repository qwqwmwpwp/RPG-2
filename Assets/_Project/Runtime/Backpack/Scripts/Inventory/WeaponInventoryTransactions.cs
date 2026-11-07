using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using System.Collections.Generic;

namespace RPG4.Backpack.Inventory
{
    public sealed partial class InventoryContainer
    {
        private bool CanInsertGraph(RuntimeItem item, RuntimeItem ignored, out string error)
        {
            try
            {
                if (!RuntimeItemSnapshotCodec.TryCapture(item, out _, out error)) return false;
                var ids = new HashSet<ulong>();
                CollectIds(item, ids);
                foreach (RuntimeItem root in Items)
                {
                    if (ReferenceEquals(root, ignored)) continue;
                    foreach (ulong id in ids)
                        if (HasItemId(root, new ItemId(id))) { error = "物品图与容器已有物品有重复标识。"; return false; }
                }
                error = null; return true;
            }
            catch (System.InvalidOperationException exception) { error = exception.Message; return false; }
        }

        private static void CollectIds(RuntimeItem root, HashSet<ulong> ids)
        {
            foreach (RuntimeItem item in ItemAssemblyTraversal.Enumerate(root))
                if (item.ItemId.IsValid) ids.Add(item.ItemId.Value);
        }
        private static bool HasItemId(RuntimeItem root, ItemId id)
            => ItemAssemblyTraversal.TryFind(root, id, out _);

        public bool CanExchangeWeaponMagazine(InventoryContainer weaponContainer, RuntimeItem weapon, RuntimeItem incoming,
            ItemManifestCatalog catalog, out string error)
            => CanExchangeLegacyAssembly(weaponContainer, weapon, incoming, ItemAssemblySlots.Magazine, catalog, out error);
        public bool TryExchangeWeaponMagazine(InventoryContainer weaponContainer, RuntimeItem weapon, RuntimeItem incoming,
            ItemManifestCatalog catalog, out string error)
            => TryExchangeWeaponChild(weaponContainer, weapon, incoming, true, default, catalog, CommitParticipant, out error);
        public bool CanExchangeWeaponAttachment(InventoryContainer weaponContainer, RuntimeItem weapon, WeaponAttachmentSlot slot,
            RuntimeItem incoming, ItemManifestCatalog catalog, out string error)
        {
            if (slot != WeaponAttachmentSlot.Muzzle) { error = "不支持的旧武器配件槽。"; return false; }
            return CanExchangeLegacyAssembly(weaponContainer, weapon, incoming, ItemAssemblySlots.Muzzle, catalog, out error);
        }
        public bool TryExchangeWeaponAttachment(InventoryContainer weaponContainer, RuntimeItem weapon, WeaponAttachmentSlot slot,
            RuntimeItem incoming, ItemManifestCatalog catalog, out string error)
            => TryExchangeWeaponChild(weaponContainer, weapon, incoming, false, slot, catalog, CommitParticipant, out error);
        private bool CanExchangeLegacyAssembly(InventoryContainer weaponContainer, RuntimeItem weapon, RuntimeItem incoming,
            string slotId, ItemManifestCatalog catalog, out string error)
        {
            if (weapon?.WeaponState == null) { error = "目标不是武器。"; return false; }
            return TryPlanAssembly(weaponContainer, weapon, weapon, slotId, incoming, catalog, out _, out error);
        }
        internal bool TryExchangeWeaponChild(InventoryContainer weaponContainer, RuntimeItem weapon, RuntimeItem incoming,
            bool magazine, WeaponAttachmentSlot slot, ItemManifestCatalog catalog, IInventoryCommitParticipant confirm, out string error)
        {
            if (weapon?.WeaponState == null || !magazine && slot != WeaponAttachmentSlot.Muzzle)
            { error = "目标不是武器或旧配件槽不受支持。"; return false; }
            return TryApplyAssembly(weaponContainer, weapon, weapon, magazine ? ItemAssemblySlots.Magazine : ItemAssemblySlots.Muzzle,
                incoming, catalog, confirm, out error);
        }

        public bool CanLoadMagazine(RuntimeItem magazine, RuntimeItem ammo, int count, ItemManifestCatalog catalog, out string error)
        {
            error = null;
            if (!CanMutate(magazine, out error) || !CanMutate(ammo, out error)) return false;
            if (magazine?.MagazineState == null || ammo == null || !Contains(magazine) || !Contains(ammo)
                || ReferenceEquals(magazine, ammo) || count <= 0 || count > ammo.StackCount
                || ammo.WeaponState != null || ammo.MagazineState != null || ammo.GetFragment<AttachmentDefinitionFragment>() != null
                || count > magazine.MagazineState.Capacity - magazine.MagazineState.RoundCount
                || !magazine.MagazineState.CanAcceptAmmo(ammo.ItemType, catalog))
            { error = "装填需要同背包内弹匣与兼容弹药，且数量不超过库存和余量。"; return false; }
            return true;
        }

        public bool TryLoadMagazine(RuntimeItem magazine, RuntimeItem ammo, int count, ItemManifestCatalog catalog, out string error)
            => TryLoadMagazine(magazine, ammo, count, catalog, CommitParticipant, out error);

        internal bool TryLoadMagazine(RuntimeItem magazine, RuntimeItem ammo, int count, ItemManifestCatalog catalog,
            IInventoryCommitParticipant confirm, out string error)
            => InventoryTransactions.Execute(this, null, (InventoryTransactions tx, out string failure) =>
            {
                if (!CanLoadMagazine(magazine, ammo, count, catalog, out failure)) return false;
                tx.Track(magazine); tx.Track(ammo);
                var previousRounds = new List<BackpackTag>(magazine.MagazineState.Rounds);
                tx.OnRollback(() =>
                {
                    if (!magazine.MagazineState.TryRestore(previousRounds, catalog, out string restoreError))
                        throw new System.InvalidOperationException(restoreError);
                });
                bool consumed = count == ammo.StackCount;
                if (consumed) { ClearCells(placements[ammo]); RemovePlacement(ammo); }
                else SetItemStack(ammo, ammo.StackCount - count);
                magazine.MagazineState.AddRounds(ammo.ItemType, count); TouchItem(magazine, true);
                if (!tx.Prepare(confirm, magazine.ItemId, out failure)) return false;
                return tx.Commit(() =>
                {
                    InventoryTransactions.RunPostCommit(() => PublishCommittedItemState(magazine), "弹匣已提交，状态通知失败");
                    NotifyChanged(); if (consumed) NotifyItemRemoved(ammo); else NotifyStackChanged(ammo);
                }, out failure);
            }, out error);
    }
}
