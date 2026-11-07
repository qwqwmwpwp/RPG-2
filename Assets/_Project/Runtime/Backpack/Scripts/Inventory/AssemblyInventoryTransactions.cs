using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    public sealed partial class InventoryContainer
    {
        internal sealed class AssemblyPlan
        {
            internal RuntimeItem Root, Parent, Incoming, Outgoing;
            internal InventoryContainer RootContainer;
            internal string SlotId;
            internal int RootIndex, ReturnIndex;
            internal GridCoord RootDimensions;
            internal ItemId ConflictItemId;
            internal string ConflictSlotId;
        }

        private sealed class AssemblyNode
        {
            internal RuntimeItem Item, Parent;
            internal string SlotId;
            internal AssemblyNode(RuntimeItem item, RuntimeItem parent, string slotId)
            { Item = item; Parent = parent; SlotId = slotId; }
        }

        internal bool TryPlanAssembly(InventoryContainer rootContainer, RuntimeItem root, RuntimeItem parent,
            string slotId, RuntimeItem incoming, ItemManifestCatalog catalog, out AssemblyPlan plan, out string error)
        {
            plan = null;
            try { return TryPlanAssemblyCore(rootContainer, root, parent, slotId, incoming, catalog, out plan, out error); }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
            { error = "装配结构校验失败：" + exception.Message; return false; }
        }

        private bool TryPlanAssemblyCore(InventoryContainer rootContainer, RuntimeItem root, RuntimeItem parent,
            string slotId, RuntimeItem incoming, ItemManifestCatalog catalog, out AssemblyPlan plan, out string error)
        {
            plan = new AssemblyPlan { Root = root, Parent = parent, Incoming = incoming, RootContainer = rootContainer, SlotId = slotId, ReturnIndex = NoIndex };
            error = null;
            if (rootContainer == null || root == null || parent == null || !rootContainer.Contains(root)
                || root.ParentItem != null || !ReferenceEquals(root.RootOwner, rootContainer))
            { error = "装配根必须属于玩家可操作的根容器。"; return false; }
            if (!CanMutate(incoming, out error) || !rootContainer.CanMutate(root, out error)) return false;
            if (!ItemAssemblySlots.IsValid(slotId) || parent.Assembly.GetSlot(slotId) == null)
            { error = "父物品没有指定的本地装配槽。"; return false; }
            var original = new List<AssemblyNode>();
            if (!CollectAssembly(root, null, null, 0, null, null, null, original, new HashSet<RuntimeItem>(), new HashSet<ulong>(), out error)) return false;
            bool foundParent = false;
            foreach (AssemblyNode node in original)
            {
                if (ReferenceEquals(node.Item, parent)) foundParent = true;
                if (node.Parent != null && (!ReferenceEquals(node.Item.ParentItem, node.Parent) || node.Item.RootOwner != null))
                { error = "已安装物品的父子归属非法。"; return false; }
            }
            if (!foundParent) { error = "父物品不属于指定装配根。"; return false; }
            plan.Outgoing = parent.Assembly.GetChild(slotId);
            if (incoming == null && plan.Outgoing == null) { error = "槽位为空，没有可卸下物品。"; return false; }
            if (incoming != null && (!Contains(incoming) || incoming.ParentItem != null || !ReferenceEquals(incoming.RootOwner, this)
                || ReferenceEquals(root, incoming) || incoming.StackCount != 1 || !incoming.ItemId.IsValid))
            { error = "候选物品必须是本背包内数量为一的独立根物品。"; return false; }
            if (incoming != null)
            {
                var candidateTree = new List<AssemblyNode>();
                if (!CollectAssembly(incoming, null, null, 0, null, null, null, candidateTree, new HashSet<RuntimeItem>(), new HashSet<ulong>(), out error)) return false;
                var oldIds = new HashSet<ulong>();
                foreach (AssemblyNode node in original) oldIds.Add(node.Item.ItemId.Value);
                foreach (AssemblyNode node in candidateTree)
                {
                    if (oldIds.Contains(node.Item.ItemId.Value)) { error = "候选子树与装配根含重复物品标识。"; return false; }
                    if (node.Parent != null && (!ReferenceEquals(node.Item.ParentItem, node.Parent) || node.Item.RootOwner != null))
                    { error = "候选子树的父子归属非法。"; return false; }
                }
            }
            var preview = new List<AssemblyNode>();
            if (!CollectAssembly(root, null, null, 0, parent, slotId, incoming, preview, new HashSet<RuntimeItem>(), new HashSet<ulong>(), out error)) return false;
            foreach (AssemblyNode node in preview)
                if (node.Parent != null && !node.Parent.Assembly.CanAccept(node.SlotId, node.Item, catalog))
                { plan.ConflictItemId = node.Item.ItemId; plan.ConflictSlotId = node.SlotId; error = "物品类型、弹匣型号或弹种不兼容装配槽。"; return false; }
            var previewItems = new List<RuntimeItem>(preview.Count);
            foreach (AssemblyNode node in preview) previewItems.Add(node.Item);
            if (!ItemAssemblyRules.TryValidateConflicts(previewItems, out plan.ConflictItemId, out plan.ConflictSlotId, out error)) return false;
            if (plan.Outgoing != null && !CanAcceptItem(plan.Outgoing, out error)) return false;
            var proposedIds = new HashSet<ulong>();
            foreach (AssemblyNode node in preview) proposedIds.Add(node.Item.ItemId.Value);
            if (plan.Outgoing != null)
                foreach (RuntimeItem child in ItemAssemblyTraversal.Enumerate(plan.Outgoing)) proposedIds.Add(child.ItemId.Value);
            foreach (RuntimeItem other in Items)
            {
                if (ReferenceEquals(other, root) || ReferenceEquals(other, incoming)) continue;
                foreach (RuntimeItem child in ItemAssemblyTraversal.Enumerate(other))
                    if (proposedIds.Contains(child.ItemId.Value)) { error = "装配物品标识与背包内其他子树重复。"; return false; }
            }
            if (!ReferenceEquals(rootContainer, this))
                foreach (RuntimeItem other in rootContainer.Items)
                {
                    if (ReferenceEquals(other, root)) continue;
                    foreach (RuntimeItem child in ItemAssemblyTraversal.Enumerate(other))
                        if (proposedIds.Contains(child.ItemId.Value)) { error = "装配物品标识与目标容器其他子树重复。"; return false; }
                }
            rootContainer.TryGetPlacement(root, out InventoryPlacement rootPlacement);
            plan.RootIndex = rootPlacement.UpperLeftIndex;
            plan.RootDimensions = rootContainer.SingleCellItems ? GridCoord.One
                : ItemAssemblySizeCalculator.GetDimensions(root, parent.ItemId, slotId, incoming);
            if (!rootContainer.IsRegionAvailable(plan.RootIndex, plan.RootDimensions, root,
                ReferenceEquals(rootContainer, this) ? incoming : null))
            { error = "装配后的根物品在原位置没有足够占格空间。"; return false; }
            if (plan.Outgoing != null)
            {
                GridCoord size = ResolveItemDimensions(plan.Outgoing);
                int preferred = incoming != null ? placements[incoming].UpperLeftIndex : 0;
                for (int step = -1; step < SlotCount; step++)
                {
                    int index = step == -1 ? preferred : step;
                    if (!IsRegionAvailable(index, size, incoming, ReferenceEquals(rootContainer, this) ? root : null)) continue;
                    if (ReferenceEquals(rootContainer, this) && AssemblyRegionsOverlap(index, size, plan.RootIndex, plan.RootDimensions)) continue;
                    plan.ReturnIndex = index; break;
                }
                if (plan.ReturnIndex == NoIndex) { error = "背包空间不足，无法接回拆下的完整子装配。"; return false; }
            }
            return true;
        }

        private static bool CollectAssembly(RuntimeItem item, RuntimeItem parent, string localSlot, int depth,
            RuntimeItem replacedParent, string replacedSlot, RuntimeItem replacement, List<AssemblyNode> result,
            HashSet<RuntimeItem> seen, HashSet<ulong> ids, out string error)
        {
            error = null;
            if (item == null || !item.ItemId.IsValid || depth > RuntimeItemSnapshot.MaxDepth || !seen.Add(item)
                || !ids.Add(item.ItemId.Value) || seen.Count > RuntimeItemSnapshot.MaxTreeItems || item.StackCount != 1)
            { error = "装配树包含无效标识、重复、环、数量异常或超出节点/深度上限。"; return false; }
            result.Add(new AssemblyNode(item, parent, localSlot));
            foreach (var pair in item.Assembly.InstalledChildren)
            {
                if (ReferenceEquals(item, replacedParent) && pair.Key == replacedSlot) continue;
                if (item.Assembly.GetSlot(pair.Key) == null) { error = "子物品引用了父物品不存在的本地槽。"; return false; }
                if (!CollectAssembly(pair.Value, item, pair.Key, depth + 1, replacedParent, replacedSlot, replacement, result, seen, ids, out error)) return false;
            }
            return !ReferenceEquals(item, replacedParent) || replacement == null
                || CollectAssembly(replacement, item, replacedSlot, depth + 1, replacedParent, replacedSlot, null, result, seen, ids, out error);
        }

        private bool AssemblyRegionsOverlap(int leftIndex, GridCoord left, int rightIndex, GridCoord right)
        {
            int lx = leftIndex % Columns, ly = leftIndex / Columns, rx = rightIndex % Columns, ry = rightIndex / Columns;
            return lx < rx + right.X && rx < lx + left.X && ly < ry + right.Y && ry < ly + left.Y;
        }

        internal bool TryApplyAssembly(InventoryContainer rootContainer, RuntimeItem root, RuntimeItem parent, string slotId,
            RuntimeItem incoming, ItemManifestCatalog catalog, IInventoryCommitParticipant confirm, out string error)
        {
            if (rootContainer == null) { error = "装配根容器为空。"; return false; }
            return InventoryTransactions.Execute(this, rootContainer, (InventoryTransactions tx, out string failure) =>
            {
                if (!TryPlanAssembly(rootContainer, root, parent, slotId, incoming, catalog, out AssemblyPlan plan, out failure)) return false;
                foreach (RuntimeItem child in ItemAssemblyTraversal.Enumerate(root)) tx.Track(child);
                if (incoming != null) foreach (RuntimeItem child in ItemAssemblyTraversal.Enumerate(incoming)) tx.Track(child);
                tx.OnRollback(() => parent.Assembly.SetChild(slotId, plan.Outgoing));
                if (incoming != null) { ClearCells(placements[incoming]); RemovePlacement(incoming); }
                rootContainer.ClearCells(rootContainer.placements[root]); rootContainer.RemovePlacement(root);
                parent.Assembly.SetChild(slotId, incoming);
                rootContainer.CommitPlacement(root, plan.RootIndex, plan.RootDimensions);
                rootContainer.TouchItem(root, true);
                if (plan.Outgoing != null) CommitPlacement(plan.Outgoing, plan.ReturnIndex, ResolveItemDimensions(plan.Outgoing));
                if (!tx.Prepare(confirm, plan.Outgoing?.ItemId ?? ItemId.None, out failure)
                    || !tx.Prepare(rootContainer.CommitParticipant, root.ItemId, out failure)) return false;
                return tx.Commit(() =>
                {
                    InventoryTransactions.RunPostCommit(() => rootContainer.PublishCommittedItemState(root), "装配已提交，根状态通知失败");
                    NotifyChanged(); if (!ReferenceEquals(this, rootContainer)) rootContainer.NotifyChanged();
                    if (incoming != null) NotifyItemRemoved(incoming);
                    if (plan.Outgoing != null) NotifyItemAdded(plan.Outgoing);
                }, out failure);
            }, out error);
        }
    }
}
