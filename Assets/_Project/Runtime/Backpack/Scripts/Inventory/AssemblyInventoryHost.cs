using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    public sealed partial class InventoryHostCore
    {
        public Func<ItemId, InventoryContainer> AssemblyContainerResolver { get; set; }
        public Func<IEnumerable<InventoryContainer>> AssemblyCandidateContainersProvider { get; set; }
        public Action<AssemblyQuery> ForwardAssemblyQuery { get; set; }
        public Action<AssemblyOperation> ForwardAssemblyOperation { get; set; }
        public Action<AssemblyQuery> AuthorityAssemblyQueryDispatcher { get; set; }
        public Action<AssemblyOperation> AuthorityAssemblyOperationDispatcher { get; set; }
        private EventSubscription assemblyQuerySubscription, assemblyOperationSubscription;

        private InventoryContainer ResolveAssemblyContainer(ItemId id)
            => AssemblyContainerResolver != null ? AssemblyContainerResolver(id)
                : WeaponContainerResolver != null ? WeaponContainerResolver(id) : Container;

        private List<InventoryContainer> AssemblyCandidateContainers()
        {
            var result = new List<InventoryContainer>();
            IEnumerable<InventoryContainer> configured = AssemblyCandidateContainersProvider?.Invoke();
            if (configured != null)
            {
                foreach (InventoryContainer container in configured)
                    if (container != null && !container.SingleCellItems && !result.Contains(container)) result.Add(container);
            }
            else if (Container != null && !Container.SingleCellItems) result.Add(Container);
            return result;
        }

        private bool ResolveAssembly(AssemblyOperation operation, out InventoryContainer rootContainer,
            out InventoryContainer sourceContainer, out RuntimeItem root, out RuntimeItem parent,
            out RuntimeItem incoming, out string error)
        {
            rootContainer = null; sourceContainer = Container; root = null; parent = null; incoming = null; error = null;
            if (!ResolveAuthority() || Container == null) { error = "仅可写的权威库存可以执行装配。"; return false; }
            if (!operation.RootItemId.IsValid || !operation.ParentItemId.IsValid || !ItemAssemblySlots.IsValid(operation.SlotId))
            { error = "装配根、父物品或槽标识无效。"; return false; }
            rootContainer = ResolveAssemblyContainer(operation.RootItemId);
            if (rootContainer == null || !rootContainer.TryGetItemById(operation.RootItemId, out root))
            { error = "装配根已不在玩家可操作容器内。"; return false; }
            if (operation.ExpectedStructureRevision >= 0 && root.StructureRevision != operation.ExpectedStructureRevision)
            { error = "装配结构已改变，请刷新后重试。"; return false; }
            error = WeaponOperationBlockReason?.Invoke(root.ItemId);
            if (!string.IsNullOrEmpty(error)) return false;
            if (!ItemAssemblyTraversal.TryFind(root, operation.ParentItemId, out parent))
            { error = "父物品已不在指定装配树内。"; return false; }
            if (!operation.CandidateItemId.IsNone)
            {
                sourceContainer = null;
                foreach (InventoryContainer container in AssemblyCandidateContainers())
                    if (container.TryGetItemById(operation.CandidateItemId, out RuntimeItem found))
                    {
                        if (sourceContainer != null) { error = "可操作容器中存在重复候选物品标识。"; return false; }
                        sourceContainer = container; incoming = found;
                    }
                if (sourceContainer == null) { error = "可操作背包或仓库中没有该候选根物品。"; return false; }
            }
            return true;
        }

        public AssemblyOperationResult EvaluateAssembly(AssemblyOperation operation)
            => EvaluateAssemblyCore(operation, false);

        public AssemblyOperationResult ExecuteAssembly(AssemblyOperation operation)
        {
            using (InventoryTransactions.DeferPublication()) return EvaluateAssemblyCore(operation, true);
        }

        private AssemblyOperationResult EvaluateAssemblyCore(AssemblyOperation operation, bool apply)
        {
            RuntimeItem root = null;
            InventoryContainer.AssemblyPlan plan = null;
            bool success = false;
            string error;
            try
            {
                success = ResolveAssembly(operation, out InventoryContainer target, out InventoryContainer source,
                    out root, out RuntimeItem parent, out RuntimeItem incoming, out error);
                if (success) success = source.TryPlanAssembly(target, root, parent, operation.SlotId, incoming, Catalog, out plan, out error);
                if (success && apply) success = source.TryApplyAssembly(target, root, parent, operation.SlotId, incoming, Catalog, source.CommitParticipant, out error);
            }
            catch (Exception exception) { success = false; error = "装配校验失败：" + exception.Message; }
            return new AssemblyOperationResult(operation.RequestId, operation.SessionId, operation.RootItemId,
                operation.ParentItemId, operation.SlotId, success, success ? null : error, root?.StructureRevision ?? -1,
                plan?.ConflictItemId ?? ItemId.None, plan?.ConflictSlotId);
        }

        public AssemblyInspectionSnapshot QueryAssembly(AssemblyQuery query)
        {
            var slots = new List<AssemblySlotSnapshot>();
            var candidates = new List<AssemblyCandidateSnapshot>();
            RuntimeItem root = null;
            string reason = null, current = null, preview = null;
            int total = 0, next = -1, offset = Math.Max(0, query.CandidateOffset), limit = Math.Max(1, Math.Min(64, query.CandidateLimit));
            ItemId parentId = query.ParentItemId.IsNone ? query.RootItemId : query.ParentItemId;
            try
            {
                if (!ResolveAuthority() || Container == null) reason = "仅权威库存可以提供装配查询。";
                else if (!query.RootItemId.IsNone)
                {
                    InventoryContainer target = ResolveAssemblyContainer(query.RootItemId);
                    if (target == null || !target.TryGetItemById(query.RootItemId, out root)) reason = "装配根已不在玩家可操作容器内。";
                    else
                    {
                        reason = WeaponOperationBlockReason?.Invoke(root.ItemId);
                        current = AssemblyCombatSummary(root, Catalog);
                        foreach (RuntimeItem _ in ItemAssemblyTraversal.Enumerate(root)) { }
                        AppendAssemblySlots(root, root, 0, query, slots);
                        if (ItemAssemblySlots.IsValid(query.SlotId) && ItemAssemblyTraversal.TryFind(root, parentId, out RuntimeItem parent)
                            && parent.Assembly.GetSlot(query.SlotId) != null)
                        {
                            preview = AssemblyCombatSummary(root, Catalog, parentId, query.SlotId);
                            var all = new List<RuntimeItem>();
                            foreach (InventoryContainer source in AssemblyCandidateContainers())
                                foreach (RuntimeItem candidate in source.Items)
                                    if (!ReferenceEquals(candidate, root) && parent.Assembly.CanAccept(query.SlotId, candidate, Catalog)) all.Add(candidate);
                            all.Sort((a, b) => a.ItemId.Value.CompareTo(b.ItemId.Value));
                            total = all.Count;
                            for (int index = offset; index < total && candidates.Count < limit; index++)
                            {
                                RuntimeItem candidate = all[index];
                                AssemblyOperation operation = new AssemblyOperation(query.RequestId, root.ItemId, parentId,
                                    query.SlotId, candidate.ItemId, root.StructureRevision, query.SessionId);
                                AssemblyOperationResult result = EvaluateAssembly(operation);
                                GridCoord size = InventoryContainer.GetItemDimensions(root);
                                string summary = null;
                                try
                                {
                                    size = ItemAssemblySizeCalculator.GetDimensions(root, parentId, query.SlotId, candidate);
                                    summary = AssemblyCombatSummary(root, Catalog, parentId, query.SlotId, candidate);
                                }
                                catch (InvalidOperationException) { }
                                candidates.Add(new AssemblyCandidateSnapshot(WeaponUiSnapshots.Describe(candidate, Catalog), result.Success,
                                    result.Error, size, summary, result.ConflictItemId, result.ConflictSlotId));
                            }
                            if ((long)offset + candidates.Count < total) next = offset + candidates.Count;
                        }
                        else if (query.SlotId != null) reason = "父物品或本地槽位已不存在。";
                    }
                }
            }
            catch (Exception exception) { reason = "装配查询失败：" + exception.Message; slots.Clear(); candidates.Clear(); }
            return new AssemblyInspectionSnapshot(WeaponUiSnapshots.Describe(root, Catalog), slots, candidates, current, preview,
                reason, root?.StructureRevision ?? -1, query.SessionId, query.RequestId, query.RootItemId, parentId,
                query.SlotId, offset, total, next);
        }

        private void AppendAssemblySlots(RuntimeItem root, RuntimeItem item, int depth, AssemblyQuery query, List<AssemblySlotSnapshot> result)
        {
            // 模板每节点最多32槽，整树最多32节点；输出仍保持所有槽的稳定父子顺序。
            if (depth > RuntimeItemSnapshot.MaxDepth) throw new InvalidOperationException("装配树深度超限。");
            foreach (ItemSlotDefinition slot in item.Assembly.Slots)
            {
                RuntimeItem child = item.Assembly.GetChild(slot.SlotId);
                AssemblyOperationResult removal = EvaluateAssembly(new AssemblyOperation(query.RequestId, root.ItemId, item.ItemId,
                    slot.SlotId, default, root.StructureRevision, query.SessionId));
                result.Add(new AssemblySlotSnapshot(item.ItemId, slot.SlotId, slot.DisplayName, slot.Required, depth,
                    WeaponUiSnapshots.Describe(child, Catalog), removal.Success, removal.Error));
                if (child != null) AppendAssemblySlots(root, child, depth + 1, query, result);
            }
        }

        public static string AssemblyCombatSummary(RuntimeItem root, ItemManifestCatalog catalog,
            ItemId parentItemId = default, string slotId = null, RuntimeItem replacement = null)
        {
            WeaponItemState weapon = root?.WeaponState;
            if (weapon == null) return null;
            BackpackTag ammo = weapon.ChamberAmmo;
            RuntimeItem magazine = parentItemId == root.ItemId && slotId == ItemAssemblySlots.Magazine ? replacement : weapon.Magazine;
            if (ammo.IsEmpty && magazine?.MagazineState.RoundCount > 0) ammo = magazine.MagazineState.Rounds[0];
            bool referenceAmmo = ammo.IsEmpty;
            if (referenceAmmo && catalog != null)
                foreach (ItemManifest manifest in catalog.Manifests)
                {
                    AmmoDefinitionFragment definition = manifest?.GetFragment<AmmoDefinitionFragment>();
                    if (definition != null && definition.AmmoType.MatchesTagExact(weapon.Definition.AmmoType))
                    { ammo = manifest.ItemType; break; }
                }
            if (WeaponParameterCalculator.TryCalculateAssembly(root, catalog, ammo, out WeaponCombatParameters value, parentItemId, slotId, replacement))
                return (referenceAmmo ? "参考弹药 · " : "") + $"伤害 {value.Damage:0.#} · 射速 {60f / value.Interval:0} 发/分 · 散布 {value.Spread:0.##}° · 后坐力 {value.Recoil:0.##}×";
            return "缺少兼容弹药模板，无法预览装配参数。";
        }

        public void PublishAssemblyInspection(AssemblyInspectionSnapshot snapshot)
        { if (commandSource != null) InventoryTransactions.RunPostCommit(() => EventBus.Global.Publish(InvChannels.AssemblyInspectionChanged, commandSource, snapshot), "装配查询通知失败"); }
        public void PublishAssemblyOperationResult(AssemblyOperationResult result)
        { if (commandSource != null) InventoryTransactions.RunPostCommit(() => EventBus.Global.Publish(InvChannels.AssemblyOperationCompleted, commandSource, result), "装配操作结果通知失败"); }
        private void OnAssemblyQueryCommand(AssemblyQuery query)
        {
            if (ResolveAuthority())
            { if (AuthorityAssemblyQueryDispatcher != null) AuthorityAssemblyQueryDispatcher(query); else PublishAssemblyInspection(QueryAssembly(query)); }
            else if (ForwardAssemblyQuery != null) ForwardAssemblyQuery(query);
            else PublishAssemblyInspection(new AssemblyInspectionSnapshot(null, null, null, null, null, "当前库存不支持装配查询。", -1,
                query.SessionId, query.RequestId, query.RootItemId, query.ParentItemId, query.SlotId));
        }
        private void OnAssemblyOperationCommand(AssemblyOperation operation)
        {
            if (ResolveAuthority())
            { if (AuthorityAssemblyOperationDispatcher != null) AuthorityAssemblyOperationDispatcher(operation); else PublishAssemblyOperationResult(ExecuteAssembly(operation)); }
            else if (ForwardAssemblyOperation != null) ForwardAssemblyOperation(operation);
            else PublishAssemblyOperationResult(new AssemblyOperationResult(operation.RequestId, operation.SessionId,
                operation.RootItemId, operation.ParentItemId, operation.SlotId, false, "当前库存不支持装配操作。"));
        }
    }
}
