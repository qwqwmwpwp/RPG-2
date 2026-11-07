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
        public ProjectionSyncReport(int added, int updated, int removed, int rejected, string firstError, int considered = 0, int encoded = 0, int cacheHits = 0)
        {
            Added = added;
            Updated = updated;
            Removed = removed;
            Rejected = rejected;
            FirstError = firstError;
            Considered = considered;
            Encoded = encoded;
            CacheHits = cacheHits;
        }

        public int Added { get; } // 新增条目数量。

        public int Updated { get; } // 状态更新条目数量。

        public int Removed { get; } // 移除条目数量。

        public int Rejected { get; } // 因非法状态被拒绝投影的物品数量。

        public string FirstError { get; } // 首个失败原因；无失败为 null。

        public int Considered { get; }
        public int Encoded { get; }
        public int CacheHits { get; }

        public int TotalChanges => Added + Updated + Removed; // 实际发生的条目变化总数。

        public bool HasChanges => TotalChanges > 0; // 是否发生了条目变化。

        /// <summary>可诊断文本。</summary>
        public override string ToString()
        {
            string summary = $"add={Added} set={Updated} remove={Removed} reject={Rejected} inspect={Considered} encode={Encoded} hit={CacheHits}";
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
        private readonly Dictionary<ulong, RuntimeItem> itemsById = new Dictionary<ulong, RuntimeItem>();
        private readonly Dictionary<ulong, int> indexById = new Dictionary<ulong, int>();
        private readonly HashSet<ulong> treeIds = new HashSet<ulong>();
        private readonly Dictionary<RuntimeItem, CachedEntry> cachedEntries = new Dictionary<RuntimeItem, CachedEntry>();
        private readonly HashSet<RuntimeItem> dirtyItems = new HashSet<RuntimeItem>();
        private readonly Dictionary<ulong, RuntimeItem> treeOwners = new Dictionary<ulong, RuntimeItem>();
        private readonly Dictionary<RuntimeItem, HashSet<ulong>> registeredTrees = new Dictionary<RuntimeItem, HashSet<ulong>>();
        private InventoryContainer observedContainer;
        private IList<NetworkInventoryEntry> observedTarget;
        private bool needsFullSync = true, fullSyncPending = true;
        private long reportedEncoded, reportedCacheHits;
        private ulong nextVersion, dirtyGeneration, publicationEpoch, lastPublishedGeneration, invalidationEpoch;

        public int ItemCount => itemsById.Count;
        public bool HasPendingChanges => fullSyncPending || dirtyItems.Count > 0;
        public long SyncCount { get; private set; }
        public long FullSyncCount { get; private set; }
        public long ConsideredItemCount { get; private set; }
        public long EncodedItemCount { get; private set; } // 结构/弹序基线编码尝试数，包含其子物品图。
        public long EncodedPayloadBytes { get; private set; }
        public long EncodedRuntimeCount { get; private set; }
        public long EncodedRuntimeBytes { get; private set; }
        public long CacheHitCount { get; private set; }
        public long RestoreAttemptCount { get; private set; }
        public long RestoredItemCount { get; private set; } // 成功恢复的根物品数，服务端确认校验也计入。
        public ProjectionSyncReport LastSyncReport { get; private set; }

        /// <summary>派生数据按容器与目标绑定；失效信号只入集合，不查询静默提交中的状态。</summary>
        public void Bind(InventoryContainer container, IList<NetworkInventoryEntry> target)
        {
            if (ReferenceEquals(observedContainer, container) && ReferenceEquals(observedTarget, target)) return;
            Clear();
            observedContainer = container;
            observedTarget = target;
            observedContainer.ItemTouched += OnItemTouched;
        }

        public void Clear()
        {
            if (observedContainer != null) observedContainer.ItemTouched -= OnItemTouched;
            observedContainer = null;
            observedTarget = null;
            orderedItems.Clear();
            itemsById.Clear();
            indexById.Clear();
            treeIds.Clear();
            cachedEntries.Clear();
            dirtyItems.Clear();
            treeOwners.Clear();
            registeredTrees.Clear();
            needsFullSync = fullSyncPending = true;
            unchecked { publicationEpoch++; dirtyGeneration++; invalidationEpoch++; }
        }

        public void ResetStatistics()
        {
            SyncCount = FullSyncCount = ConsideredItemCount = EncodedItemCount = 0;
            EncodedPayloadBytes = CacheHitCount = RestoreAttemptCount = RestoredItemCount = 0;
            EncodedRuntimeCount = EncodedRuntimeBytes = 0;
            reportedEncoded = reportedCacheHits = 0;
            LastSyncReport = default;
        }

        private void OnItemTouched(RuntimeItem item, bool payloadChanged)
        {
            dirtyItems.Add(item);
            unchecked { dirtyGeneration++; }
            // 静默事务在触碰前已推进对应版本；运行变化不可丢弃稳定结构缓存。
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
            if (item == null || !item.ItemId.IsValid)
            {
                error = "物品为空或标识为 None，拒绝投影。";
                return false;
            }
            if (cachedEntries.TryGetValue(item, out CachedEntry cached)
                && cached.StructureRevision == item.StructureRevision && cached.StackCount == item.StackCount)
            {
                CacheHitCount++;
                if (cached.Revision != item.Revision)
                {
                    if (!TryBuildRuntime(item, out byte[] runtime, out error)) return false;
                    cached.Entry.runtimeState = runtime;
                    cached.Entry.runtimeVersion = AllocateVersion();
                    cached.Revision = item.Revision;
                }
                entry = cached.Entry;
                error = null;
                return true;
            }
            EncodedItemCount++;
            if (!RuntimeItemSnapshotCodec.TryCaptureStructure(item, out RuntimeItemSnapshot snapshot, out error)
                || !RuntimeItemSnapshotCodec.TryEncodeSnapshot(snapshot, out byte[] payload, out error)) return false;
            if (!TryBuildRuntime(item, out byte[] runtimePayload, out error)) return false;
            EncodedPayloadBytes += payload.Length;
            entry = new NetworkInventoryEntry(snapshot.ItemId, snapshot.ItemType, snapshot.StackCount, payload,
                structureVersion: AllocateVersion(), runtimeVersion: AllocateVersion(), runtimeState: runtimePayload);
            var ids = new HashSet<ulong>();
            CollectTreeIds(item, ids); // TryCapture 已验证环、深度与图内重复标识。
            cachedEntries[item] = new CachedEntry(item.Revision, item.StructureRevision, item.StackCount, entry, ids);
            error = null;
            return true;
        }

        private ulong AllocateVersion()
        {
            // 版本仅用于相等匹配，不作大小判断；0 保留给旧的完整快照接口。
            unchecked { nextVersion++; }
            if (nextVersion == 0) nextVersion = 1;
            return nextVersion;
        }

        private bool TryBuildRuntime(RuntimeItem item, out byte[] payload, out string error)
        {
            if (!RuntimeItemSnapshotCodec.TryCaptureNetworkRuntime(item, out payload, out error)) return false;
            EncodedRuntimeCount++;
            EncodedRuntimeBytes += payload.Length;
            return true;
        }

        /// <summary>同一载荷与目录只做一次恢复校验；布局变动不重新实例化物品图。</summary>
        public bool TryValidateRestoration(RuntimeItem item, ItemManifestCatalog catalog, out string error)
        {
            if (!TryBuildEntry(item, out NetworkInventoryEntry entry, out error)) return false;
            CachedEntry cached = cachedEntries[item];
            if (cached.RestorationValidated && ReferenceEquals(cached.ValidatedCatalog, catalog))
                return RuntimeItemSnapshotCodec.TryPrepareNetworkRuntime(item, entry.runtimeState, catalog, out _, out error);
            if (!TryRestoreItem(entry, catalog, out _, out error)) return false;
            cached.ValidatedCatalog = catalog;
            cached.RestorationValidated = true;
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

            RestoreAttemptCount++;
            if (!manifest.TryRestoreRuntimeItem(snapshot, catalog, out item, out error))
            {
                item = null;
                return false;
            }

            if (entry.runtimeState != null)
            {
                if (!RuntimeItemSnapshotCodec.TryPrepareNetworkRuntime(item, entry.runtimeState, catalog,
                    out RuntimeItemStateUpdate update, out error))
                { item = null; return false; }
                update.Apply();
            }

            RestoredItemCount++;
            error = null;
            return true;
        }

        /// <summary>把权威容器投影到目标条目列表：只产生实际变化（Add / Set / Remove），顺序按 <see cref="ItemId"/> 升序（新增即有序）。</summary>
        public void SyncContainer(InventoryContainer container, IList<NetworkInventoryEntry> target,
            out ProjectionSyncReport report)
        {
            if (!TryPrepareContainer(container, target, ItemId.None, out IInventoryPreparedCommit prepared, out report, out _)) return;
            try { prepared.Publish(); }
            catch (Exception exception)
            {
                Invalidate();
                report = FinishReport(0, 0, 0, 1, "投影发布失败：" + exception.Message, report.Considered);
            }
        }

        /// <summary>只生成本批独立增量；不写 target、已发布索引或已发布物品图。</summary>
        public bool TryPrepareContainer(InventoryContainer container, IList<NetworkInventoryEntry> target, ItemId requiredItem,
            out IInventoryPreparedCommit prepared, out ProjectionSyncReport report, out string error)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (target == null) throw new ArgumentNullException(nameof(target));
            Bind(container, target);
            prepared = null; error = null;
            if (!needsFullSync && !HasPendingChanges && target.Count == indexById.Count)
            {
                if (requiredItem.IsValid && !itemsById.ContainsKey(requiredItem.Value))
                { error = "候选投影中没有待确认物品。"; report = FinishReport(0, 0, 0, 1, error, 0); return false; }
                report = default; prepared = EmptyPreparedCommit.Instance; return true;
            }
            SyncCount++;
            bool full = needsFullSync || fullSyncPending || target.Count != indexById.Count;
            var processing = new HashSet<RuntimeItem>(dirtyItems);
            Dictionary<ulong, int> fullIndices = null;
            var removed = new HashSet<ulong>();
            if (full)
            {
                FullSyncCount++;
                processing.UnionWith(container.Items); processing.UnionWith(itemsById.Values);
                fullIndices = new Dictionary<ulong, int>();
                for (int index = 0; index < target.Count; index++)
                { fullIndices[target[index].itemId] = index; removed.Add(target[index].itemId); }
            }
            IDictionary<ulong, int> indices = full ? fullIndices : indexById;
            var ordered = new List<RuntimeItem>(processing); ordered.Sort(ItemIdComparison);
            var staged = new Dictionary<ulong, StagedEntry>();
            var stagedOwners = new HashSet<ulong>();
            int rejected = 0; string firstError = null;
            foreach (RuntimeItem item in ordered)
            {
                if (!container.Contains(item))
                {
                    if (itemsById.TryGetValue(item.ItemId.Value, out RuntimeItem previous) && ReferenceEquals(previous, item))
                        removed.Add(item.ItemId.Value);
                    continue;
                }
                if (!TryBuildEntry(item, out NetworkInventoryEntry entry, out string failure))
                { rejected++; firstError = firstError ?? failure; continue; }
                if (!container.TryGetPlacement(item, out InventoryPlacement placement)) failure = "物品缺少权威放置记录。";
                else
                {
                    entry.upperLeftIndex = placement.UpperLeftIndex; entry.width = placement.Dimensions.X; entry.height = placement.Dimensions.Y;
                    entry.TryValidate(out failure);
                }
                HashSet<ulong> ids = cachedEntries[item].TreeIds;
                if (failure == null && staged.ContainsKey(entry.itemId)) failure = "不同根物品有重复标识。";
                if (failure == null)
                    foreach (ulong id in ids)
                        if (stagedOwners.Contains(id) || (treeOwners.TryGetValue(id, out RuntimeItem owner) && !processing.Contains(owner)))
                        { failure = "不同根物品图有重复标识。"; break; }
                if (failure != null) { rejected++; firstError = firstError ?? failure; continue; }
                staged.Add(entry.itemId, new StagedEntry(item, entry, new HashSet<ulong>(ids)));
                stagedOwners.UnionWith(ids); removed.Remove(entry.itemId);
            }
            // A later removed root with the same id must not remove its staged replacement.
            removed.ExceptWith(staged.Keys);
            if (requiredItem.IsValid && !staged.ContainsKey(requiredItem.Value)
                && (!itemsById.TryGetValue(requiredItem.Value, out RuntimeItem required) || processing.Contains(required) || !container.Contains(required)))
            { rejected++; firstError = firstError ?? "候选投影中没有待确认物品。"; }
            if (rejected > 0)
            {
                Invalidate(); error = firstError;
                report = FinishReport(0, 0, 0, rejected, firstError, ordered.Count); return false;
            }
            int added = 0, updated = 0;
            foreach (StagedEntry entry in staged.Values)
                if (!indices.TryGetValue(entry.Entry.itemId, out int index)) added++;
                else if (!target[index].StateEquals(entry.Entry)) updated++;
            ConsideredItemCount += ordered.Count;
            report = FinishReport(added, updated, removed.Count, 0, null, ordered.Count);
            prepared = new PreparedProjection(this, container, target, ordered.ToArray(), staged, removed, fullIndices,
                dirtyGeneration, publicationEpoch, invalidationEpoch);
            return true;
        }

        public void Invalidate()
        {
            needsFullSync = fullSyncPending = true;
            unchecked { invalidationEpoch++; }
        }

        private sealed class EmptyPreparedCommit : IInventoryPreparedCommit
        {
            internal static readonly EmptyPreparedCommit Instance = new EmptyPreparedCommit();
            public void Publish() { }
            public void Discard() { }
        }

        private sealed class PreparedProjection : IInventoryPreparedCommit
        {
            private readonly InventoryNetworkProjection owner;
            private readonly InventoryContainer container;
            private readonly IList<NetworkInventoryEntry> target;
            private readonly RuntimeItem[] processing;
            private readonly Dictionary<ulong, StagedEntry> staged;
            private readonly HashSet<ulong> removed;
            private readonly Dictionary<ulong, int> fullIndices;
            private readonly ulong generation, epoch, invalidation;
            private bool finished;
            internal PreparedProjection(InventoryNetworkProjection owner, InventoryContainer container, IList<NetworkInventoryEntry> target,
                RuntimeItem[] processing, Dictionary<ulong, StagedEntry> staged, HashSet<ulong> removed,
                Dictionary<ulong, int> fullIndices, ulong generation, ulong epoch, ulong invalidation)
            {
                this.owner = owner; this.container = container; this.target = target; this.processing = processing;
                this.staged = staged; this.removed = removed; this.fullIndices = fullIndices;
                this.generation = generation; this.epoch = epoch; this.invalidation = invalidation;
            }
            public void Publish()
            {
                if (finished) return;
                finished = true;
                try
                {
                    if (!ReferenceEquals(owner.observedContainer, container) || !ReferenceEquals(owner.observedTarget, target))
                        throw new InvalidOperationException("投影绑定已改变，不能发布旧候选。");
                    if (invalidation != owner.invalidationEpoch)
                        throw new InvalidOperationException("投影需要重建，不能发布已失效候选。");
                    // The same participant can validate two required ids in one transaction. Its identical
                    // independent candidates must publish once, without reapplying removal indices.
                    if (epoch != owner.publicationEpoch)
                    {
                        if (owner.lastPublishedGeneration == generation) return;
                        throw new InvalidOperationException("投影候选已过期，需要从权威库存重建。");
                    }
                    if (fullIndices != null)
                    {
                        owner.indexById.Clear();
                        foreach (var pair in fullIndices) owner.indexById.Add(pair.Key, pair.Value);
                    }
                    foreach (RuntimeItem item in processing)
                    {
                        if (owner.registeredTrees.TryGetValue(item, out HashSet<ulong> ids))
                        { foreach (ulong id in ids) owner.treeOwners.Remove(id); owner.registeredTrees.Remove(item); }
                        if (owner.itemsById.TryGetValue(item.ItemId.Value, out RuntimeItem previous) && ReferenceEquals(previous, item))
                            owner.itemsById.Remove(item.ItemId.Value);
                        if (!container.Contains(item)) owner.cachedEntries.Remove(item);
                    }
                    foreach (StagedEntry entry in staged.Values)
                    {
                        if (owner.indexById.TryGetValue(entry.Entry.itemId, out int index))
                        { if (!target[index].StateEquals(entry.Entry)) target[index] = entry.Entry; }
                        else { owner.indexById[entry.Entry.itemId] = target.Count; target.Add(entry.Entry); }
                        owner.itemsById[entry.Entry.itemId] = entry.Item;
                        owner.registeredTrees[entry.Item] = entry.TreeIds;
                        foreach (ulong id in entry.TreeIds) owner.treeOwners[id] = entry.Item;
                    }
                    var removalIndices = new List<int>();
                    foreach (ulong id in removed) if (owner.indexById.TryGetValue(id, out int index)) removalIndices.Add(index);
                    removalIndices.Sort();
                    for (int index = removalIndices.Count - 1; index >= 0; index--) target.RemoveAt(removalIndices[index]);
                    if (removalIndices.Count > 0) owner.RebuildIndex(target);
                    unchecked { owner.publicationEpoch++; }
                    owner.lastPublishedGeneration = generation;
                    if (generation == owner.dirtyGeneration)
                    { owner.dirtyItems.Clear(); owner.needsFullSync = owner.fullSyncPending = false; }
                    else owner.Invalidate();
                }
                catch { owner.Invalidate(); throw; }
            }
            public void Discard() { if (!finished) { finished = true; owner.Invalidate(); } }
        }

        private ProjectionSyncReport FinishReport(int added, int updated, int removed, int rejected, string error, int considered)
        {
            LastSyncReport = new ProjectionSyncReport(added, updated, removed, rejected, error, considered,
                (int)Math.Min(int.MaxValue, EncodedItemCount - reportedEncoded),
                (int)Math.Min(int.MaxValue, CacheHitCount - reportedCacheHits));
            reportedEncoded = EncodedItemCount;
            reportedCacheHits = CacheHitCount;
            return LastSyncReport;
        }

        private sealed class CachedEntry
        {
            internal int Revision;
            internal readonly int StructureRevision, StackCount;
            internal NetworkInventoryEntry Entry;
            internal readonly HashSet<ulong> TreeIds;
            internal ItemManifestCatalog ValidatedCatalog;
            internal bool RestorationValidated;
            internal CachedEntry(int revision, int structureRevision, int stackCount, NetworkInventoryEntry entry, HashSet<ulong> ids)
            { Revision = revision; StructureRevision = structureRevision; StackCount = stackCount; Entry = entry; TreeIds = ids; }
        }

        private readonly struct StagedEntry
        {
            internal readonly RuntimeItem Item;
            internal readonly NetworkInventoryEntry Entry;
            internal readonly HashSet<ulong> TreeIds;
            internal StagedEntry(RuntimeItem item, NetworkInventoryEntry entry, HashSet<ulong> ids)
            { Item = item; Entry = entry; TreeIds = ids; }
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

        internal static void CollectTreeIds(RuntimeItem item, HashSet<ulong> ids)
        {
            if (item.ItemId.IsValid) ids.Add(item.ItemId.Value);
            if (item.WeaponState == null) return;
            if (item.WeaponState.Magazine != null) CollectTreeIds(item.WeaponState.Magazine, ids);
            foreach (RuntimeItem child in item.WeaponState.Attachments.Values) CollectTreeIds(child, ids);
        }
    }
}
