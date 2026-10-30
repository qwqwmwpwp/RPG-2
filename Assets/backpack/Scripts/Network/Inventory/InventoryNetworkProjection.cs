using System;
using System.Collections.Generic;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    /// <summary>一次投影同步的结果统计。</summary>
    public readonly struct ProjectionSyncReport
    {
        /// <summary>统计结果。</summary>
        public ProjectionSyncReport(int added, int updated, int removed, int rejected, string firstError)
        {
            Added = added;
            Updated = updated;
            Removed = removed;
            Rejected = rejected;
            FirstError = firstError;
        }

        public int Added { get; } // 新增条目数量。

        public int Updated { get; } // 状态更新条目数量。

        public int Removed { get; } // 移除条目数量。

        public int Rejected { get; } // 因非法状态被拒绝投影的物品数量。

        public string FirstError { get; } // 首个失败原因；无失败为 null。

        public int TotalChanges => Added + Updated + Removed; // 实际发生的条目变化总数。

        public bool HasChanges => TotalChanges > 0; // 是否发生了条目变化。

        /// <summary>可诊断文本。</summary>
        public override string ToString()
        {
            string summary = $"add={Added} set={Updated} remove={Removed} reject={Rejected}";
            return FirstError == null ? summary : summary + " first=" + FirstError;
        }
    }

    /// <summary>物品容器 ↔ 网络条目的纯投影逻辑：不依赖 Mirror / Unity / UI，可独立单测；负责把权威 <see cref="RuntimeItem"/> 抓取为 <see cref="NetworkInventoryEntry"/>（含载荷编码）、按标识索引提供 O(1) 定位、与目标条目列表做差异（只产生 Add / Set / Remove 的实际变化），以及把条目恢复为运行时物品（客户端镜像，不重新随机化）；本类不持有网络状态，条目列表由 <see cref="BackpackNetworkInventory"/> 持有并下发。</summary>
    public sealed class InventoryNetworkProjection
    {
        /// <summary>确定性排序比较器（静态缓存，避免每次调用分配委托）。</summary>
        public static readonly Comparison<RuntimeItem> ItemIdComparison =
            (left, right) => left.ItemId.CompareTo(right.ItemId);

        private readonly List<RuntimeItem> orderedItems = new List<RuntimeItem>();
        private readonly List<int> removalIndices = new List<int>();
        private readonly HashSet<ulong> liveIds = new HashSet<ulong>();
        private readonly Dictionary<ulong, RuntimeItem> itemsById = new Dictionary<ulong, RuntimeItem>();
        private readonly Dictionary<ulong, int> indexById = new Dictionary<ulong, int>();
        private readonly HashSet<ulong> treeIds = new HashSet<ulong>();

        public int ItemCount => itemsById.Count; // 上一次同步记录的权威物品数量。

        /// <summary>清空全部派生索引（服务端 / 客户端生命周期切换时调用）。</summary>
        public void Clear()
        {
            orderedItems.Clear();
            removalIndices.Clear();
            liveIds.Clear();
            itemsById.Clear();
            indexById.Clear();
            treeIds.Clear();
        }

        /// <summary>按标识查找上一次同步看到的权威物品（O(1)）。</summary>
        public bool TryGetItem(ItemId itemId, out RuntimeItem item)
        {
            item = null;
            return itemId.IsValid && itemsById.TryGetValue(itemId.Value, out item);
        }

        /// <summary>把权威物品抓取为网络条目：标识必须有效（服务端必须先用 ItemIdGenerator 登记），载荷必须可编码。</summary>
        public bool TryBuildEntry(RuntimeItem item, out NetworkInventoryEntry entry, out string error)
        {
            entry = default;
            if (item == null)
            {
                error = "物品为空，拒绝投影。";
                return false;
            }

            if (!item.ItemId.IsValid)
            {
                error = $"物品 {item.ItemType} 的标识为 None，拒绝投影（服务端必须先用 ItemIdGenerator 登记）。";
                return false;
            }

            if (!RuntimeItemSnapshotCodec.TryCapture(item, out RuntimeItemSnapshot snapshot, out error))
            {
                return false;
            }

            if (!RuntimeItemSnapshotCodec.TryEncodeSnapshot(snapshot, out byte[] payload, out error))
            {
                return false;
            }

            entry = new NetworkInventoryEntry(snapshot.ItemId, snapshot.ItemType, snapshot.StackCount, payload);
            error = null;
            return true;
        }

        /// <summary>把权威条目恢复为运行时物品：结构取自目录模板、数值来自载荷，不重新随机化；条目非法、目录为空或缺模板即失败。</summary>
        public bool TryRestoreItem(
            NetworkInventoryEntry entry,
            ItemManifestCatalog catalog,
            out RuntimeItem item,
            out string error)
        {
            item = null;
            if (!entry.TryValidate(out error))
            {
                return false;
            }

            if (catalog == null)
            {
                error = "未提供物品类型目录，无法按类型恢复物品。";
                return false;
            }

            if (!catalog.TryGet(entry.Tag, out ItemManifest manifest))
            {
                error = $"物品类型目录中缺少 '{entry.itemType}' 的清单，无法恢复物品 {entry.Id}。";
                return false;
            }

            if (!RuntimeItemSnapshotCodec.TryDecodeSnapshot(entry.Id, entry.Tag, entry.stackCount, entry.fragmentState,
                out RuntimeItemSnapshot snapshot, out error))
            {
                return false;
            }

            if (!manifest.TryRestoreRuntimeItem(snapshot, catalog, out item, out error))
            {
                item = null;
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>把权威容器投影到目标条目列表：只产生实际变化（Add / Set / Remove），顺序按 <see cref="ItemId"/> 升序（新增即有序）。</summary>
        public void SyncContainer(
            InventoryContainer container,
            IList<NetworkInventoryEntry> target,
            out ProjectionSyncReport report)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (target == null) throw new ArgumentNullException(nameof(target));

            string firstError = null;
            int added = 0;
            int updated = 0;
            int removed = 0;
            int rejected = 0;

            RebuildIndex(target);
            container.FillItemsByItemId(orderedItems);

            itemsById.Clear();
            liveIds.Clear();
            treeIds.Clear();

            for (int i = 0; i < orderedItems.Count; i++)
            {
                RuntimeItem item = orderedItems[i];
                if (!TryBuildEntry(item, out NetworkInventoryEntry entry, out string error))
                {
                    rejected++;
                    if (firstError == null) firstError = error;
                    continue;
                }
                var currentIds = new HashSet<ulong>();
                CollectTreeIds(item, currentIds);
                if (treeIds.Overlaps(currentIds))
                {
                    rejected++;
                    if (firstError == null) firstError = "不同根物品图有重复标识。";
                    continue;
                }

                if (!container.TryGetPlacement(item, out InventoryPlacement placement))
                {
                    rejected++;
                    if (firstError == null) firstError = "物品缺少权威放置记录。";
                    continue;
                }
                entry.upperLeftIndex = placement.UpperLeftIndex;
                entry.width = placement.Dimensions.X;
                entry.height = placement.Dimensions.Y;
                if (!entry.TryValidate(out error))
                {
                    rejected++;
                    if (firstError == null) firstError = error;
                    continue;
                }
                itemsById[entry.itemId] = item;
                treeIds.UnionWith(currentIds);
                liveIds.Add(entry.itemId);

                if (indexById.TryGetValue(entry.itemId, out int index))
                {
                    if (!target[index].StateEquals(entry))
                    {
                        target[index] = entry;
                        updated++;
                    }

                    continue;
                }

                target.Add(entry);
                indexById[entry.itemId] = target.Count - 1;
                added++;
            }

            removalIndices.Clear();
            foreach (KeyValuePair<ulong, int> pair in indexById)
            {
                if (!liveIds.Contains(pair.Key))
                {
                    removalIndices.Add(pair.Value);
                }
            }

            if (removalIndices.Count > 0)
            {
                // 降序移除，保证尚未处理的较小下标仍然有效。
                removalIndices.Sort();
                for (int i = removalIndices.Count - 1; i >= 0; i--)
                {
                    target.RemoveAt(removalIndices[i]);
                    removed++;
                }

                RebuildIndex(target);
            }

            report = new ProjectionSyncReport(added, updated, removed, rejected, firstError);
        }

        /// <summary>用权威条目列表整体重建镜像（晚加入客户端 / 全量重同步）：先清空镜像字典，返回被拒绝的条目数量（恢复失败或标识重复）。</summary>
        public int RestoreAll(
            IList<NetworkInventoryEntry> source,
            ItemManifestCatalog catalog,
            Dictionary<ulong, RuntimeItem> destination,
            out string firstError)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            firstError = null;
            int rejected = 0;
            destination.Clear();
            treeIds.Clear();

            for (int i = 0; i < source.Count; i++)
            {
                NetworkInventoryEntry entry = source[i];
                if (!TryRestoreItem(entry, catalog, out RuntimeItem item, out string error))
                {
                    rejected++;
                    if (firstError == null) firstError = entry.itemId + ": " + error;
                    continue;
                }

                var currentIds = new HashSet<ulong>();
                CollectTreeIds(item, currentIds);
                if (destination.ContainsKey(entry.itemId) || treeIds.Overlaps(currentIds))
                {
                    rejected++;
                    if (firstError == null) firstError = "条目 " + entry.itemId + " 重复出现。";
                    continue;
                }

                destination.Add(entry.itemId, item);
                treeIds.UnionWith(currentIds);
            }

            return rejected;
        }

        /// <summary>按目标条目列表重建「标识 → 下标」索引；跳过空标识与重复标识。</summary>
        private void RebuildIndex(IList<NetworkInventoryEntry> target)
        {
            indexById.Clear();
            for (int i = 0; i < target.Count; i++)
            {
                ulong id = target[i].itemId;
                if (id == ItemId.NoneValue) continue;
                if (indexById.ContainsKey(id)) continue;
                indexById.Add(id, i);
            }
        }

        private static void CollectTreeIds(RuntimeItem item, HashSet<ulong> ids)
        {
            if (item.ItemId.IsValid) ids.Add(item.ItemId.Value);
            if (item.WeaponState == null) return;
            if (item.WeaponState.Magazine != null) CollectTreeIds(item.WeaponState.Magazine, ids);
            foreach (RuntimeItem child in item.WeaponState.Attachments.Values) CollectTreeIds(child, ids);
        }
    }
}
