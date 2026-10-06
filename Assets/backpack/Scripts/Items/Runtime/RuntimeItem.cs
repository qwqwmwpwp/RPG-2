using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>运行时物品实例：持有标识、类型与克隆自模板的片段集合，堆叠数可变且受单堆上限约束。</summary>
    public sealed class RuntimeItem : BackpackItem
    {
        private readonly List<ItemFragment> fragments; // 克隆自模板的片段实例。
        private readonly ReadOnlyCollection<ItemFragment> readOnlyFragments; // Fragments 的只读包装，构造时一次性创建。
        private int stackCount; // 当前堆叠数。

        /// <summary>创建运行时物品实例，并校验类型标签与初始堆叠数。</summary>
        internal RuntimeItem(ItemId id, BackpackTag itemType, List<ItemFragment> fragments, int initialStackCount)
        {
            if (itemType.IsEmpty || !itemType.IsValid)
            {
                throw new ArgumentException("物品类型必须是格式合法的非空标签。", nameof(itemType));
            }

            this.fragments = fragments ?? throw new ArgumentNullException(nameof(fragments));
            readOnlyFragments = this.fragments.AsReadOnly();
            ItemId = id;
            ItemType = itemType;
            SetStackCount(initialStackCount);
        }

        public ItemId ItemId { get; } // 实例唯一标识；ItemId.None 表示离线/尚未登记，一旦创建即不可变更，网络层只接受有效标识。

        public override BackpackTag ItemType { get; } // 物品类型标签（模板身份）。

        public IReadOnlyList<ItemFragment> Fragments => readOnlyFragments; // 片段集合（只读）。

        public override int StackCount => stackCount; // 当前堆叠数。

        // 占格尺寸（X 为宽、Y 为高）：读网格占位片段，缺该片段时按 1×1 处理。
        public override GridCoord Size
        {
            get
            {
                GridFragment grid = GetFragment<GridFragment>();
                return grid == null ? GridCoord.One : new GridCoord(grid.Width, grid.Height);
            }
        }

        public bool IsStackable => GetFragment<StackableFragment>() != null; // 是否可堆叠。

        public bool IsConsumable => GetFragment<ConsumableFragment>() != null; // 是否可消耗。

        public int MaxStackSize => GetFragment<StackableFragment>()?.MaxStackSize ?? 1; // 单堆上限；不可堆叠时为 1。

        /// <summary>按类型返回第一个匹配的片段；无匹配为 null。</summary>
        public T GetFragment<T>() where T : ItemFragment
        {
            for (int i = 0; i < fragments.Count; i++)
            {
                if (fragments[i] is T match) return match;
            }

            return null;
        }

        /// <summary>按类型与标签精确匹配返回片段；无匹配为 null。</summary>
        public T GetFragment<T>(BackpackTag fragmentTag) where T : ItemFragment
        {
            if (fragmentTag.IsEmpty) return null;

            for (int i = 0; i < fragments.Count; i++)
            {
                if (fragments[i] is T match && match.FragmentTag.MatchesTagExact(fragmentTag))
                {
                    return match;
                }
            }

            return null;
        }

        /// <summary>设置堆叠数，超出 1..单堆上限时抛出异常。</summary>
        public void SetStackCount(int value)
        {
            int maxStackSize = MaxStackSize;
            if (value < 1 || value > maxStackSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, $"堆叠数必须在 1..{maxStackSize} 范围内。");
            }

            stackCount = value;
        }
    }
}
