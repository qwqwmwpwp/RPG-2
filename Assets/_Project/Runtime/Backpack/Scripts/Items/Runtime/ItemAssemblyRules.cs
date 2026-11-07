using System.Collections.Generic;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>共享装配树规则，供快照恢复与库存事务使用。</summary>
    public static class ItemAssemblyRules
    {
        public static bool TryValidateConflicts(IReadOnlyList<RuntimeItem> items,
            out ItemId conflictItemId, out string conflictSlotId, out string error)
        {
            conflictItemId = ItemId.None;
            conflictSlotId = null;
            error = null;
            if (items == null)
            {
                error = "装配树为空。";
                return false;
            }

            foreach (RuntimeItem item in items)
            {
                if (item == null)
                {
                    error = "装配树包含空物品。";
                    return false;
                }
                ItemAssemblyDefinitionFragment definition = item.GetFragment<ItemAssemblyDefinitionFragment>();
                if (definition == null) continue;
                foreach (RuntimeItem other in items)
                {
                    if (other == null || ReferenceEquals(item, other)) continue;
                    foreach (BackpackTag type in definition.ConflictingItemTypes)
                    {
                        if (!type.MatchesTagExact(other.ItemType)) continue;
                        conflictItemId = other.ItemId;
                        error = "装配树中的物品类型互斥。";
                        return false;
                    }
                    foreach (ItemSlotConflict conflict in definition.SlotConflicts)
                    {
                        if (!conflict.OwnerItemType.MatchesTagExact(other.ItemType)
                            || other.Assembly.GetSlot(conflict.SlotId) == null) continue;
                        conflictItemId = other.ItemId;
                        conflictSlotId = conflict.SlotId;
                        error = "装配物品与树中物品提供的槽位互斥。";
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
