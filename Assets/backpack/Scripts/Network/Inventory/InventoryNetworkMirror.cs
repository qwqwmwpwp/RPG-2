using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    /// <summary>先校验整批物品图与占格，再提交客户端镜像；布局变化复用物品实例。</summary>
    internal sealed class InventoryNetworkMirror
    {
        private readonly HashSet<ulong> sourceIds = new HashSet<ulong>();
        private readonly HashSet<ulong> affected = new HashSet<ulong>();
        private readonly Dictionary<ulong, NetworkInventoryEntry> pending = new Dictionary<ulong, NetworkInventoryEntry>();
        private readonly Dictionary<ulong, NetworkInventoryEntry> applied = new Dictionary<ulong, NetworkInventoryEntry>();
        private readonly Dictionary<ulong, HashSet<ulong>> appliedTrees = new Dictionary<ulong, HashSet<ulong>>();
        private readonly Dictionary<ulong, ulong> treeOwners = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, StagedItem> staged = new Dictionary<ulong, StagedItem>();
        private readonly Dictionary<ulong, ulong> stagedOwners = new Dictionary<ulong, ulong>();
        private readonly Dictionary<int, ulong> stagedCells = new Dictionary<int, ulong>();
        private readonly List<ItemId> lastChangedItems = new List<ItemId>();
        internal IReadOnlyList<ItemId> LastChangedItems => lastChangedItems;
        internal bool LastFullRefresh { get; private set; } = true;
        private ulong[] cells = Array.Empty<ulong>();
        private int appliedRows, appliedColumns;
        private ItemManifestCatalog appliedCatalog;
        private bool needsFull = true, forceRestore;

        public long BatchCount { get; private set; }
        public long FullRefreshCount { get; private set; }
        public long RestoredItemCount { get; private set; }
        public long ReusedItemCount { get; private set; }
        public int LastRestoredItemCount { get; private set; }
        public int LastChangedItemCount { get; private set; }

        public void RequestFullRefresh(bool restoreItems = false)
        {
            needsFull = true;
            forceRestore |= restoreItems;
            sourceIds.Clear();
            affected.Clear();
            pending.Clear();
        }

        public void Clear()
        {
            RequestFullRefresh();
            applied.Clear();
            appliedTrees.Clear();
            treeOwners.Clear();
            ClearStaging();
            cells = Array.Empty<ulong>();
            appliedRows = appliedColumns = 0;
            appliedCatalog = null;
            lastChangedItems.Clear();
            LastFullRefresh = true;
            forceRestore = false;
        }

        public void ResetStatistics()
        {
            BatchCount = FullRefreshCount = RestoredItemCount = ReusedItemCount = 0;
            LastRestoredItemCount = LastChangedItemCount = 0;
        }

        public void Added(NetworkInventoryEntry entry)
        {
            if (needsFull) return;
            if (!sourceIds.Add(entry.itemId)) { RequestFullRefresh(); return; }
            affected.Add(entry.itemId);
            pending[entry.itemId] = entry;
        }

        public void Removed(NetworkInventoryEntry entry)
        {
            if (needsFull) return;
            if (!sourceIds.Remove(entry.itemId)) { RequestFullRefresh(); return; }
            affected.Add(entry.itemId);
            pending.Remove(entry.itemId);
        }

        public void Replaced(NetworkInventoryEntry previous, NetworkInventoryEntry current)
        {
            if (needsFull) return;
            if (previous.itemId != current.itemId) { Removed(previous); Added(current); return; }
            if (!sourceIds.Contains(current.itemId)) { RequestFullRefresh(); return; }
            affected.Add(current.itemId);
            pending[current.itemId] = current;
        }

        public bool TryApply(IList<NetworkInventoryEntry> entries, ItemManifestCatalog catalog, int rows, int columns,
            InventoryNetworkProjection projection, Dictionary<ulong, RuntimeItem> mirror,
            Dictionary<ulong, InventoryPlacement> placements, out string error)
        {
            BatchCount++;
            LastRestoredItemCount = LastChangedItemCount = 0;
            ClearStaging();
            if (rows < 1 || columns < 1 || (long)rows * columns > InventoryContainer.MaxSlotCount)
                return Fail("同步的容器行列非法或尚未就绪。", out error);
            bool sameCatalog = ReferenceEquals(catalog, appliedCatalog);
            bool full = needsFull || !sameCatalog || rows != appliedRows || columns != appliedColumns;
            if (full)
            {
                FullRefreshCount++;
                pending.Clear();
                affected.Clear();
                affected.UnionWith(applied.Keys);
                sourceIds.Clear();
                foreach (NetworkInventoryEntry entry in entries)
                {
                    if (!sourceIds.Add(entry.itemId)) return Fail("同步条目中有重复的根物品标识。", out error);
                    pending.Add(entry.itemId, entry);
                    affected.Add(entry.itemId);
                }
            }
            LastChangedItemCount = affected.Count;
            foreach (KeyValuePair<ulong, NetworkInventoryEntry> pair in pending)
            {
                NetworkInventoryEntry entry = pair.Value;
                if (!entry.TryValidate(out error)) return Fail(error, out error);
                GridCoord size = new GridCoord(entry.width, entry.height);
                if (!GridUtils.IsInGridBounds(entry.upperLeftIndex, size, columns, rows))
                    return Fail("物品 " + entry.itemId + " 的同步位置越界。", out error);
                RuntimeItem item;
                HashSet<ulong> ids;
                if (!forceRestore && sameCatalog && applied.TryGetValue(pair.Key, out NetworkInventoryEntry previous)
                    && previous.ItemStateEquals(entry) && mirror.TryGetValue(pair.Key, out item))
                {
                    ids = appliedTrees[pair.Key];
                    ReusedItemCount++;
                }
                else
                {
                    if (!projection.TryRestoreItem(entry, catalog, out item, out error)) return Fail(error, out error);
                    LastRestoredItemCount++;
                    RestoredItemCount++;
                    ids = new HashSet<ulong>();
                    InventoryNetworkProjection.CollectTreeIds(item, ids);
                }
                foreach (ulong id in ids)
                {
                    if (stagedOwners.ContainsKey(id)
                        || (!full && treeOwners.TryGetValue(id, out ulong owner) && !affected.Contains(owner)))
                        return Fail("不同物品图中有重复标识。", out error);
                    stagedOwners.Add(id, pair.Key);
                }
                int originX = entry.upperLeftIndex % columns, originY = entry.upperLeftIndex / columns;
                for (int y = 0; y < entry.height; y++)
                for (int x = 0; x < entry.width; x++)
                {
                    int cell = (originY + y) * columns + originX + x;
                    if (stagedCells.ContainsKey(cell)
                        || (!full && cells[cell] != 0 && !affected.Contains(cells[cell])))
                        return Fail("同步物品占格重叠。", out error);
                    stagedCells.Add(cell, pair.Key);
                }
                staged.Add(pair.Key, new StagedItem(item, entry, ids));
            }

            // 校验期间只写暂存区；同批交换、根/子物品转换不暴露中间占格。
            if (full)
            {
                mirror.Clear();
                placements.Clear();
                applied.Clear();
                appliedTrees.Clear();
                treeOwners.Clear();
                cells = new ulong[rows * columns];
            }
            else
            {
                foreach (ulong id in affected)
                {
                    if (placements.TryGetValue(id, out InventoryPlacement old))
                    {
                        int originX = old.UpperLeftIndex % columns, originY = old.UpperLeftIndex / columns;
                        for (int y = 0; y < old.Dimensions.Y; y++)
                        for (int x = 0; x < old.Dimensions.X; x++)
                            cells[(originY + y) * columns + originX + x] = 0;
                    }
                    if (appliedTrees.TryGetValue(id, out HashSet<ulong> ids))
                        foreach (ulong childId in ids) treeOwners.Remove(childId);
                    appliedTrees.Remove(id);
                    applied.Remove(id);
                    mirror.Remove(id);
                    placements.Remove(id);
                }
            }
            foreach (KeyValuePair<ulong, StagedItem> pair in staged)
            {
                StagedItem next = pair.Value;
                mirror.Add(pair.Key, next.Item);
                placements.Add(pair.Key, new InventoryPlacement(next.Item, next.Entry.upperLeftIndex,
                    new GridCoord(next.Entry.width, next.Entry.height)));
                applied.Add(pair.Key, next.Entry);
                appliedTrees.Add(pair.Key, next.TreeIds);
                foreach (ulong id in next.TreeIds) treeOwners.Add(id, pair.Key);
            }
            foreach (KeyValuePair<int, ulong> pair in stagedCells) cells[pair.Key] = pair.Value;
            appliedRows = rows;
            appliedColumns = columns;
            appliedCatalog = catalog;
            LastFullRefresh = full;
            lastChangedItems.Clear();
            if (!full) foreach (ulong id in affected) lastChangedItems.Add(new ItemId(id));
            needsFull = forceRestore = false;
            pending.Clear();
            affected.Clear();
            ClearStaging();
            error = null;
            return true;
        }

        private bool Fail(string reason, out string error)
        {
            // 保留上一完整镜像，宿主标记未就绪；下一批或显式刷新从完整条目恢复。
            RequestFullRefresh();
            lastChangedItems.Clear();
            LastFullRefresh = true;
            ClearStaging();
            error = reason;
            return false;
        }

        private void ClearStaging()
        {
            staged.Clear();
            stagedOwners.Clear();
            stagedCells.Clear();
        }

        private readonly struct StagedItem
        {
            internal readonly RuntimeItem Item;
            internal readonly NetworkInventoryEntry Entry;
            internal readonly HashSet<ulong> TreeIds;
            internal StagedItem(RuntimeItem item, NetworkInventoryEntry entry, HashSet<ulong> ids)
            { Item = item; Entry = entry; TreeIds = ids; }
        }
    }
}
