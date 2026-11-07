using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using System.Collections.Generic;

namespace RPG4.Backpack.Inventory
{
    public sealed partial class InventoryContainer
    {
        private bool CanInsertGraph(RuntimeItem item, RuntimeItem ignored, out string error)
        {
            if (item.WeaponState != null && !RuntimeItemSnapshotCodec.TryCapture(item, out _, out error)) return false;
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

        private static void CollectIds(RuntimeItem root, HashSet<ulong> ids)
        {
            if (root.ItemId.IsValid) ids.Add(root.ItemId.Value);
            if (root.WeaponState == null) return;
            if (root.WeaponState.Magazine != null) CollectIds(root.WeaponState.Magazine, ids);
            foreach (RuntimeItem child in root.WeaponState.Attachments.Values) CollectIds(child, ids);
        }
        public bool CanExchangeWeaponMagazine(InventoryContainer weaponContainer, RuntimeItem weapon, RuntimeItem incoming,
            ItemManifestCatalog catalog, out string error)
            => TryPlanWeaponExchange(weaponContainer, weapon, incoming, true, default, catalog, out _, out _, out error);

        public bool TryExchangeWeaponMagazine(InventoryContainer weaponContainer, RuntimeItem weapon, RuntimeItem incoming,
            ItemManifestCatalog catalog, out string error)
            => TryExchangeWeaponChild(weaponContainer, weapon, incoming, true, default, catalog, CommitParticipant, out error);

        public bool CanExchangeWeaponAttachment(InventoryContainer weaponContainer, RuntimeItem weapon, WeaponAttachmentSlot slot,
            RuntimeItem incoming, ItemManifestCatalog catalog, out string error)
            => TryPlanWeaponExchange(weaponContainer, weapon, incoming, false, slot, catalog, out _, out _, out error);

        public bool TryExchangeWeaponAttachment(InventoryContainer weaponContainer, RuntimeItem weapon, WeaponAttachmentSlot slot,
            RuntimeItem incoming, ItemManifestCatalog catalog, out string error)
            => TryExchangeWeaponChild(weaponContainer, weapon, incoming, false, slot, catalog, CommitParticipant, out error);

        private bool TryPlanWeaponExchange(InventoryContainer weaponContainer, RuntimeItem weapon, RuntimeItem incoming,
            bool magazine, WeaponAttachmentSlot slot, ItemManifestCatalog catalog,
            out RuntimeItem outgoing, out int returnIndex, out string error)
        {
            outgoing = null; returnIndex = NoIndex; error = null;
            if (!CanMutate(incoming, out error) || (weaponContainer != null && !weaponContainer.CanMutate(weapon, out error))) return false;
            if (weaponContainer == null || weapon == null || !weaponContainer.Contains(weapon) || weapon.WeaponState == null
                || !ReferenceEquals(weapon.RootOwner, weaponContainer))
            { error = "武器必须属于指定的根容器。"; return false; }
            if (magazine) outgoing = weapon.WeaponState.Magazine;
            else weapon.WeaponState.Attachments.TryGetValue(slot, out outgoing);
            if (incoming == null && outgoing == null) { error = "槽位为空，没有可交换物品。"; return false; }
            if (incoming != null)
            {
                if (!Contains(incoming) || !ReferenceEquals(incoming.RootOwner, this) || incoming.ParentItem != null
                    || !incoming.ItemId.IsValid || ReferenceEquals(incoming, weapon))
                { error = "候选物品必须是背包内有效且未安装的根物品。"; return false; }
                if (magazine ? !weapon.WeaponState.IsCompatibleMagazine(incoming, catalog) : !weapon.WeaponState.IsCompatibleAttachment(slot, incoming))
                { error = "候选物品不兼容武器槽。"; return false; }
                if (HasItemId(weapon, incoming.ItemId)) { error = "子物品标识与武器图重复。"; return false; }
            }
            if (outgoing == null) return true;
            if (!ReferenceEquals(outgoing.ParentItem, weapon) || outgoing.RootOwner != null || !outgoing.ItemId.IsValid)
            { error = "原子物品归属非法。"; return false; }
            if (!CanAcceptItem(outgoing, out error)) return false;
            foreach (RuntimeItem root in Items)
                if (!ReferenceEquals(root, weapon) && !ReferenceEquals(root, incoming) && HasItemId(root, outgoing.ItemId))
                { error = "返回物品标识与背包内其他物品重复。"; return false; }
            GridCoord size = ResolveItemDimensions(outgoing);
            int preferred = incoming != null ? placements[incoming].UpperLeftIndex : 0;
            returnIndex = FindSwapRegion(preferred, size, incoming);
            if (returnIndex == NoIndex) { error = "背包空间不足，无法接回原子物品。"; return false; }
            return true;
        }

        private static bool HasItemId(RuntimeItem root, ItemId id)
        {
            if (root.ItemId == id) return true;
            if (root.WeaponState == null) return false;
            if (root.WeaponState.Magazine != null && HasItemId(root.WeaponState.Magazine, id)) return true;
            foreach (RuntimeItem child in root.WeaponState.Attachments.Values) if (HasItemId(child, id)) return true;
            return false;
        }

        internal bool TryExchangeWeaponChild(InventoryContainer weaponContainer, RuntimeItem weapon, RuntimeItem incoming,
            bool magazine, WeaponAttachmentSlot slot, ItemManifestCatalog catalog, IInventoryCommitParticipant confirm, out string error)
        {
            if (weaponContainer == null) { error = "目标武器容器为空。"; return false; }
            return InventoryTransactions.Execute(this, weaponContainer, (InventoryTransactions tx, out string failure) =>
            {
                if (!TryPlanWeaponExchange(weaponContainer, weapon, incoming, magazine, slot, catalog,
                    out RuntimeItem outgoing, out int index, out failure)) return false;
                tx.Track(weapon); tx.Track(incoming); tx.Track(outgoing);
                tx.OnRollback(() =>
                {
                    if (magazine) weapon.WeaponState.SetMagazine(outgoing); else weapon.WeaponState.SetAttachment(slot, outgoing);
                });
                if (incoming != null) { ClearCells(placements[incoming]); RemovePlacement(incoming); }
                if (magazine) weapon.WeaponState.SetMagazine(incoming); else weapon.WeaponState.SetAttachment(slot, incoming);
                weaponContainer.TouchItem(weapon, true);
                if (outgoing != null) CommitPlacement(outgoing, index, ResolveItemDimensions(outgoing));
                if (!tx.Prepare(confirm, outgoing?.ItemId ?? ItemId.None, out failure)
                    || !tx.Prepare(weaponContainer.CommitParticipant, weapon.ItemId, out failure)) return false;
                bool same = ReferenceEquals(this, weaponContainer);
                return tx.Commit(() =>
                {
                    InventoryTransactions.RunPostCommit(() => weaponContainer.PublishCommittedItemState(weapon), "武器已提交，状态通知失败");
                    NotifyChanged(); if (!same) weaponContainer.NotifyChanged();
                    if (incoming != null) NotifyItemRemoved(incoming);
                    if (outgoing != null) NotifyItemAdded(outgoing);
                }, out failure);
            }, out error);
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
