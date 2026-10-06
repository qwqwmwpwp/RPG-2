using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>单个可随机化数值节点的运行时状态：节点由「顶层片段下标 + 嵌套 Modifier 下标」寻址，只有 LabeledNumberFragment 及其派生类型在运行时携带随机化结果，其余片段字段来自模板、不进入运行时快照。</summary>
    public readonly struct FragmentNumberState : IEquatable<FragmentNumberState>
    {
        public const int SelfModifierIndex = -1; // 表示数值节点就是顶层片段自身的 Modifier 下标。

        public const int MaxIndex = ushort.MaxValue; // 下标允许的最大值，与网络载荷的 ushort 编码一致。

        /// <summary>以节点坐标与运行时数值构造状态；下标越界或数值为 NaN / 无穷大时抛出异常。</summary>
        public FragmentNumberState(int fragmentIndex, int modifierIndex, float value)
        {
            if (fragmentIndex < 0 || fragmentIndex > MaxIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(fragmentIndex), fragmentIndex, $"片段下标必须在 0..{MaxIndex} 范围内。");
            }

            if (modifierIndex < SelfModifierIndex || modifierIndex > MaxIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(modifierIndex), modifierIndex, $"Modifier 下标必须在 {SelfModifierIndex}..{MaxIndex} 范围内。");
            }

            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "运行时数值必须是有限数。");
            }

            FragmentIndex = fragmentIndex;
            ModifierIndex = modifierIndex;
            Value = value;
        }

        public int FragmentIndex { get; } // 顶层片段下标（对应 RuntimeItem.Fragments 的下标）。

        public int ModifierIndex { get; } // 嵌套 Modifier 下标；SelfModifierIndex 表示片段自身。

        public float Value { get; } // 运行时数值（服务端随机化结果，客户端原样恢复、不重新随机）。

        /// <inheritdoc/>
        public bool Equals(FragmentNumberState other)
        {
            return FragmentIndex == other.FragmentIndex
                && ModifierIndex == other.ModifierIndex
                && Value.Equals(other.Value);
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is FragmentNumberState other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = FragmentIndex;
                hash = (hash * 397) ^ ModifierIndex;
                hash = (hash * 397) ^ Value.GetHashCode();
                return hash;
            }
        }

        /// <summary>可诊断文本，例如 [0] 或 [2.1]。</summary>
        public override string ToString()
        {
            return ModifierIndex == SelfModifierIndex
                ? "[" + FragmentIndex + "]"
                : "[" + FragmentIndex + "." + ModifierIndex + "]";
        }
    }

    /// <summary>运行时物品的完整运行时快照：身份 + 类型 + 堆叠数 + 全部可随机化数值节点的当前值；不含 Unity Object 引用与网格位置（位置由本地表现层负责），与网络框架解耦、编码由 RuntimeItemSnapshotCodec 显式完成。</summary>
    public readonly struct RuntimeItemSnapshot : IEquatable<RuntimeItemSnapshot>
    {
        public const byte CurrentVersion = 1; // 当前快照格式版本；载荷版本不匹配一律按失败处理。

        public const int MaxNumberStateCount = 64; // 单个物品允许携带的数值节点上限（防御异常载荷导致的分配放大）。

        public const int MaxItemTypeLength = 128; // 物品类型标签串的最大长度。

        private readonly FragmentNumberState[] numbers; // 数值状态数组。

        /// <summary>构造快照；数值节点数组为空、数量超限或堆叠数小于 1 时抛出异常。</summary>
        public RuntimeItemSnapshot(ItemId itemId, BackpackTag itemType, int stackCount, FragmentNumberState[] numbers)
        {
            if (numbers == null) throw new ArgumentNullException(nameof(numbers));
            if (numbers.Length > MaxNumberStateCount)
            {
                throw new ArgumentException($"数值节点数量 {numbers.Length} 超过上限 {MaxNumberStateCount}。", nameof(numbers));
            }

            if (stackCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(stackCount), stackCount, "堆叠数必须大于 0。");
            }

            this.numbers = numbers;
            ItemId = itemId;
            ItemType = itemType;
            StackCount = stackCount;
        }

        public ItemId ItemId { get; } // 运行时实例标识（可能为 ItemId.None，由网络层负责拒绝）。

        public BackpackTag ItemType { get; } // 物品类型标签（模板身份）。

        public int StackCount { get; } // 快照时刻的堆叠数副本；唯一来源仍是 RuntimeItem.StackCount。

        public IReadOnlyList<FragmentNumberState> Numbers => numbers ?? Array.Empty<FragmentNumberState>(); // 按节点坐标升序排列的数值状态，顺序与模板遍历顺序一致，供确定性比较与载荷编码使用。

        /// <inheritdoc/>
        public bool Equals(RuntimeItemSnapshot other)
        {
            if (ItemId != other.ItemId || StackCount != other.StackCount) return false;
            if (!string.Equals(ItemType.ToString(), other.ItemType.ToString(), StringComparison.Ordinal)) return false;

            IReadOnlyList<FragmentNumberState> left = Numbers;
            IReadOnlyList<FragmentNumberState> right = other.Numbers;
            if (left.Count != right.Count) return false;

            for (int i = 0; i < left.Count; i++)
            {
                if (!left[i].Equals(right[i])) return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is RuntimeItemSnapshot other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = ItemId.GetHashCode();
                hash = (hash * 397) ^ StackCount;
                hash = (hash * 397) ^ ItemType.GetHashCode();
                IReadOnlyList<FragmentNumberState> list = Numbers;
                for (int i = 0; i < list.Count; i++)
                {
                    hash = (hash * 397) ^ list[i].GetHashCode();
                }

                return hash;
            }
        }

        /// <summary>可诊断文本，例如 ItemId(7) IronOre x3 nodes=2。</summary>
        public override string ToString()
        {
            return ItemId + " " + ItemType + " x" + StackCount + " nodes=" + Numbers.Count;
        }
    }
}
