using System;
using System.Collections.Generic;
using RPG4.Backpack.Items;

namespace RPG4.LoadoutTemplate
{
    /// <summary>将已解码的角色状态转换为恢复槽位与容器所需的事务写入。</summary>
    internal static class NetworkLoadoutRestore
    {
        /// <summary>观察运行物品编号并采集恢复依赖，创建完成端点回填后调用 onRestored 的事务。</summary>
        internal static bool TryPrepare(RoleLoadoutController role, Candidate candidate, Action onRestored,
            out LoadoutStateVersion before, out PreparedLoadoutOperation prepared, out string error)
        {
            before = default;
            prepared = null;
            error = null;
            ulong highWater = 0;
            foreach (ItemDisplayData item in candidate.items)
            {
                if (item == null) continue;
                foreach (RuntimeItem node in RuntimeNodes(item)) highWater = Math.Max(highWater, node.ItemId.Value);
            }
            try { LoadoutRuntimeItems.SharedItemIds.Observe(new ItemId(highWater)); }
            catch (ArgumentOutOfRangeException exception) { error = exception.Message; return false; }
            before = role.CaptureStateVersion();
            var dependencies = new Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot>();
            foreach (RoleSlotConfiguration config in role.Configuration)
            {
                ILoadoutEndpoint slot = role.GetSlot(config.slotId);
                dependencies.Add(slot, LoadoutRuleSnapshot.CaptureEndpoint(slot, slot.Snapshot));
                if ((int)config.slotId < 4)
                {
                    ILoadoutEndpoint grid = config.containerController;
                    dependencies.Add(grid, LoadoutRuleSnapshot.CaptureEndpoint(grid, grid.Snapshot));
                }
            }
            prepared = new PreparedLoadoutOperation(new Dictionary<ILoadoutEndpoint, List<ItemDisplayData>>(),
                new Dictionary<ILoadoutEndpoint, object>(), dependencies,
                selectedWeaponChanged: role.SelectedWeaponSlot != candidate.selectedWeapon, applyState: () =>
                {
                    role.ClearRuntimeReservations();
                    role.ApplySelectedWeapon(candidate.selectedWeapon);
                    // 已完成完整解码与规则检查，按持久状态恢复槽位和网格版本。
                    foreach (RoleSlotConfiguration config in role.Configuration)
                    {
                        int index = (int)config.slotId;
                        var endpoint = (LoadoutSlotEndpoint)role.GetSlot(config.slotId);
                        endpoint.ApplyNetworkState(candidate.slots[index].id, candidate.items[index], candidate.slots[index].revision);
                    }
                    foreach (RoleSlotConfiguration config in role.Configuration)
                        if ((int)config.slotId < 4)
                            config.containerController.ApplyNetworkStorage(candidate.items[(int)config.slotId]?.Storage,
                                candidate.slots[(int)config.slotId].storageRevision);
                    onRestored();
                });
            return true;
        }

        /// <summary>枚举物品装配树及其附属容器中的所有运行物品，以恢复编号高水位。</summary>
        private static IEnumerable<RuntimeItem> RuntimeNodes(ItemDisplayData item)
        {
            if (item.RuntimeItem != null) foreach (RuntimeItem node in ItemAssemblyTraversal.Enumerate(item.RuntimeItem)) yield return node;
            if (item.Storage != null) foreach (ItemDisplayData child in item.Storage.Items)
                foreach (RuntimeItem node in RuntimeNodes(child)) yield return node;
        }
    }
}
