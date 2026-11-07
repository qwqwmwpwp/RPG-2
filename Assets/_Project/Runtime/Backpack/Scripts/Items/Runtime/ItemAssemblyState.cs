using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>每个物品的唯一装配子树；槽标识仅在当前父物品内寻址。</summary>
    public sealed class ItemAssemblyState
    {
        private readonly RuntimeItem owner;
        private readonly SortedDictionary<string, RuntimeItem> children = new SortedDictionary<string, RuntimeItem>(StringComparer.Ordinal);
        private readonly ReadOnlyDictionary<string, RuntimeItem> readOnlyChildren;
        private readonly IReadOnlyList<ItemSlotDefinition> slots;
        private readonly bool legacy;
        public IReadOnlyDictionary<string, RuntimeItem> InstalledChildren => readOnlyChildren;
        public IReadOnlyList<ItemSlotDefinition> Slots => slots;
        internal ItemAssemblyState(RuntimeItem owner)
        {
            this.owner = owner;
            readOnlyChildren = new ReadOnlyDictionary<string, RuntimeItem>(children);
            ItemAssemblyDefinitionFragment definition = owner.GetFragment<ItemAssemblyDefinitionFragment>();
            if (definition != null) { slots = definition.Slots; return; }
            legacy = true;
            var fallback = new List<ItemSlotDefinition>();
            WeaponDefinitionFragment weapon = owner.GetFragment<WeaponDefinitionFragment>();
            if (weapon != null)
            {
                if (!weapon.MagazineType.IsEmpty) fallback.Add(new ItemSlotDefinition(ItemAssemblySlots.Magazine, "弹匣", Array.Empty<BackpackTag>()));
                if (weapon.AllowsSlot(WeaponAttachmentSlot.Muzzle)) fallback.Add(new ItemSlotDefinition(ItemAssemblySlots.Muzzle, "枪口", Array.Empty<BackpackTag>()));
            }
            slots = fallback.AsReadOnly();
        }
        public RuntimeItem GetChild(string slotId) => slotId != null && children.TryGetValue(slotId, out RuntimeItem item) ? item : null;
        public ItemSlotDefinition GetSlot(string slotId)
        {
            foreach (ItemSlotDefinition slot in slots) if (string.Equals(slot.SlotId, slotId, StringComparison.Ordinal)) return slot;
            return null;
        }
        public IEnumerable<ItemSlotDefinition> EnumerateSlots() => slots;
        public bool CanAccept(string slotId, RuntimeItem child, ItemManifestCatalog catalog)
        {
            ItemSlotDefinition slot = GetSlot(slotId);
            if (slot == null || child == null || child.StackCount != 1) return false;
            if (!legacy && !slot.Allows(child.ItemType)) return false;
            // 武器弹匣仍必须满足弹种与弹匣型号规则；普通物品槽只使用模板允许表。
            if (owner.WeaponState != null && slotId == ItemAssemblySlots.Magazine)
                return owner.WeaponState.IsCompatibleMagazine(child, catalog);
            if (owner.WeaponState != null && slotId == ItemAssemblySlots.Muzzle)
                return owner.WeaponState.IsCompatibleAttachment(WeaponAttachmentSlot.Muzzle, child);
            return !legacy;
        }
        internal void SetChild(string slotId, RuntimeItem item)
        {
            if (GetSlot(slotId) == null) throw new ArgumentException("物品没有该装配槽。", nameof(slotId));
            RuntimeItem previous = GetChild(slotId);
            if (ReferenceEquals(previous, item)) return;
            if (item != null)
            {
                if (item.ParentItem != null || item.RootOwner != null) throw new InvalidOperationException("子物品必须先从原归属移除。");
                for (RuntimeItem ancestor = owner; ancestor != null; ancestor = ancestor.ParentItem)
                    if (ReferenceEquals(ancestor, item)) throw new InvalidOperationException("装配不能形成循环。");
            }
            if (previous != null) { previous.Changed -= OnChildChanged; previous.ParentItem = null; children.Remove(slotId); }
            if (item != null) { children.Add(slotId, item); item.ParentItem = owner; item.Changed += OnChildChanged; }
            owner.AdvanceStateRevision(false);
        }
        private void OnChildChanged(RuntimeItem item) => owner.PublishStateChanged();
    }

    public static class ItemAssemblyTraversal
    {
        public static IEnumerable<RuntimeItem> Enumerate(RuntimeItem root, bool includeRoot = true)
        {
            if (root == null) yield break;
            int count = 0;
            var seen = new HashSet<RuntimeItem>();
            var ids = new HashSet<ulong>();
            foreach (RuntimeItem item in Walk(root, 0, seen, ids))
            {
                if (++count > RuntimeItemSnapshot.MaxTreeItems) throw new InvalidOperationException("物品树节点超限。");
                if (includeRoot || !ReferenceEquals(item, root)) yield return item;
            }
        }
        private static IEnumerable<RuntimeItem> Walk(RuntimeItem item, int depth, HashSet<RuntimeItem> seen, HashSet<ulong> ids)
        {
            if (item == null || depth > RuntimeItemSnapshot.MaxDepth || !seen.Add(item) || seen.Count > RuntimeItemSnapshot.MaxTreeItems
                || item.ItemId.IsValid && !ids.Add(item.ItemId.Value)) throw new InvalidOperationException("物品树含环、重复标识或超限。");
            yield return item;
            foreach (RuntimeItem child in item.Assembly.InstalledChildren.Values)
                foreach (RuntimeItem descendant in Walk(child, depth + 1, seen, ids)) yield return descendant;
        }
        public static bool TryFind(RuntimeItem root, ItemId itemId, out RuntimeItem item)
        {
            item = null;
            if (!itemId.IsValid) return false;
            foreach (RuntimeItem candidate in Enumerate(root)) if (candidate.ItemId == itemId) { item = candidate; return true; }
            return false;
        }
        // 预览只覆盖一次指定父物品的本地槽，避免修改活物品和旧子树重复计入。
        internal static IEnumerable<RuntimeItem> EnumerateReplacement(RuntimeItem root, ItemId parentItemId, string slotId, RuntimeItem replacement)
        {
            var seen = new HashSet<RuntimeItem>();
            var ids = new HashSet<ulong>();
            bool replaced = false;
            foreach (RuntimeItem item in WalkReplacement(root, 0, parentItemId, slotId, replacement, seen, ids, ref replaced)) yield return item;
            if (slotId != null && !replaced) throw new InvalidOperationException("预览父物品或槽不存在。");
        }
        private static List<RuntimeItem> WalkReplacement(RuntimeItem item, int depth, ItemId parentItemId, string slotId, RuntimeItem replacement,
            HashSet<RuntimeItem> seen, HashSet<ulong> ids, ref bool replaced)
        {
            if (item == null || depth > RuntimeItemSnapshot.MaxDepth || !seen.Add(item) || seen.Count > RuntimeItemSnapshot.MaxTreeItems
                || item.ItemId.IsValid && !ids.Add(item.ItemId.Value)) throw new InvalidOperationException("预览物品树含环、重复标识或超限。");
            var result = new List<RuntimeItem> { item };
            bool target = slotId != null && !replaced && item.ItemId == parentItemId;
            if (target && item.Assembly.GetSlot(slotId) == null) throw new InvalidOperationException("预览槽不存在。");
            if (target) replaced = true;
            var children = new SortedDictionary<string, RuntimeItem>(StringComparer.Ordinal);
            foreach (var pair in item.Assembly.InstalledChildren) children.Add(pair.Key, pair.Value);
            if (target) { children.Remove(slotId); if (replacement != null) children.Add(slotId, replacement); }
            foreach (RuntimeItem child in children.Values)
                result.AddRange(WalkReplacement(child, depth + 1, parentItemId, slotId, replacement, seen, ids, ref replaced));
            return result;
        }
    }
}
