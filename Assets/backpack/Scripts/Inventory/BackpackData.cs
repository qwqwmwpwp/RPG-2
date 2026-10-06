using System;
using System.Collections.Generic;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>储存数据类：角色身上具体物品的集合，只提供按下标与按实例的只读查询，不提供增删；集合顺序在构造时冻结，实例判等一律按引用。</summary>
    public sealed class BackpackData
    {
        private readonly IReadOnlyList<BackpackItem> items; // 物品集合的只读视图。

        /// <summary>以物品集合构造；集合为空引用、含空元素或同一实例重复登记时抛出异常。</summary>
        public BackpackData(IEnumerable<BackpackItem> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            List<BackpackItem> copies = new List<BackpackItem>();
            foreach (BackpackItem item in items)
            {
                if (item == null)
                {
                    throw new ArgumentException("物品集合不能包含空元素。", nameof(items));
                }

                int existingIndex = IndexOfReference(copies, item);
                if (existingIndex >= 0)
                {
                    throw new ArgumentException(
                        $"物品集合不能重复登记同一实例（下标 {existingIndex} 与 {copies.Count}）。", nameof(items));
                }

                copies.Add(item);
            }

            this.items = copies.AsReadOnly();
        }

        public IReadOnlyList<BackpackItem> Items => items; // 物品集合（只读，顺序即登记顺序）。

        public int Count => items.Count; // 物品数量。

        /// <summary>按下标取物品；下标越界返回 false 且 item 为 null。</summary>
        public bool TryGetItem(int index, out BackpackItem item)
        {
            if (index < 0 || index >= items.Count)
            {
                item = null;
                return false;
            }

            item = items[index];
            return true;
        }

        /// <summary>物品实例在集合中的下标；空引用或不在集合返回 -1。</summary>
        public int IndexOf(BackpackItem item)
        {
            if (item == null) return -1;

            for (int i = 0; i < items.Count; i++)
            {
                if (ReferenceEquals(items[i], item)) return i;
            }

            return -1;
        }

        /// <summary>物品实例是否在集合中（按引用判等）。</summary>
        public bool Contains(BackpackItem item) => IndexOf(item) >= 0;

        /// <summary>按引用在整个集合中查找实例下标，找不到返回 -1。</summary>
        private static int IndexOfReference(List<BackpackItem> candidates, BackpackItem item)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (ReferenceEquals(candidates[i], item)) return i;
            }

            return -1;
        }
    }
}
