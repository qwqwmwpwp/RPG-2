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

    public readonly struct InstalledItemSnapshot : IEquatable<InstalledItemSnapshot>
    {
        public string SlotId { get; }
        public RuntimeItemSnapshot Item { get; }
        public InstalledItemSnapshot(string slotId, RuntimeItemSnapshot item)
        {
            if (!ItemAssemblySlots.IsValid(slotId)) throw new ArgumentException("装配槽标识无效。", nameof(slotId));
            SlotId = slotId; Item = item;
        }
        public bool Equals(InstalledItemSnapshot other) => string.Equals(SlotId, other.SlotId, StringComparison.Ordinal) && Item.Equals(other.Item);
        public override bool Equals(object obj) => obj is InstalledItemSnapshot other && Equals(other);
        public override int GetHashCode() => (SlotId == null ? 0 : StringComparer.Ordinal.GetHashCode(SlotId)) * 397 ^ Item.GetHashCode();
    }

    /// <summary>运行时物品的完整运行时快照：身份 + 类型 + 堆叠数 + 全部可随机化数值节点的当前值；不含 Unity Object 引用与网格位置（位置由本地表现层负责），与网络框架解耦、编码由 RuntimeItemSnapshotCodec 显式完成。</summary>
    public readonly struct RuntimeItemSnapshot : IEquatable<RuntimeItemSnapshot>
    {
        public const byte CurrentVersion = 3;
        public const int MaxDepth = 4;
        public const int MaxTreeItems = 32;

        public const int MaxNumberStateCount = 64; // 单个物品允许携带的数值节点上限（防御异常载荷导致的分配放大）。

        public const int MaxItemTypeLength = 128; // 物品类型标签串的最大长度。

        private readonly IReadOnlyList<InstalledItemSnapshot> installedChildren;
        private readonly FragmentNumberState[] numbers; // 数值状态数组。

        /// <summary>构造快照；数值节点数组为空、数量超限或堆叠数小于 1 时抛出异常。</summary>
        public RuntimeItemSnapshot(ItemId itemId, BackpackTag itemType, int stackCount, FragmentNumberState[] numbers)
            : this(itemId, itemType, stackCount, numbers, null, null) { }

        public RuntimeItemSnapshot(ItemId itemId, BackpackTag itemType, int stackCount, FragmentNumberState[] numbers,
            WeaponStateSnapshot weapon, MagazineStateSnapshot magazine)
            : this(itemId, itemType, stackCount, numbers, weapon, magazine, LegacyChildren(weapon)) { }

        public RuntimeItemSnapshot(ItemId itemId, BackpackTag itemType, int stackCount, FragmentNumberState[] numbers,
            WeaponStateSnapshot weapon, MagazineStateSnapshot magazine, IEnumerable<InstalledItemSnapshot> installedChildren)
            : this(itemId, itemType, stackCount, numbers, weapon, magazine, installedChildren, CurrentVersion) { }

        internal RuntimeItemSnapshot(ItemId itemId, BackpackTag itemType, int stackCount, FragmentNumberState[] numbers,
            WeaponStateSnapshot weapon, MagazineStateSnapshot magazine, IEnumerable<InstalledItemSnapshot> installedChildren, byte version)
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

            this.numbers = (FragmentNumberState[])numbers.Clone();
            ItemId = itemId;
            ItemType = itemType;
            StackCount = stackCount;
            // v3只在通用子树中持有子节点，旧构造函数的数据也显式迁移。
            Weapon = weapon == null ? null : new WeaponStateSnapshot(weapon.Durability, weapon.ChamberAmmo, weapon.ShotSequence);
            Magazine = magazine;
            var children = new List<InstalledItemSnapshot>();
            var slots = new HashSet<string>(StringComparer.Ordinal);
            if (installedChildren == null) throw new ArgumentNullException(nameof(installedChildren));
            foreach (InstalledItemSnapshot child in installedChildren)
            {
                if (!ItemAssemblySlots.IsValid(child.SlotId) || !slots.Add(child.SlotId) || children.Count >= MaxTreeItems - 1)
                    throw new ArgumentException("装配槽标识无效、重复或子节点超限。", nameof(installedChildren));
                children.Add(child);
            }
            foreach (InstalledItemSnapshot legacy in LegacyChildren(weapon))
            {
                bool found = false;
                foreach (InstalledItemSnapshot child in children)
                    if (string.Equals(child.SlotId, legacy.SlotId, StringComparison.Ordinal))
                    {
                        if (!child.Equals(legacy)) throw new ArgumentException("旧装配子树与通用子树不一致。", nameof(installedChildren));
                        found = true; break;
                    }
                if (!found) throw new ArgumentException("旧装配子树未迁移到通用子树。", nameof(installedChildren));
            }
            children.Sort((left, right) => StringComparer.Ordinal.Compare(left.SlotId, right.SlotId));
            this.installedChildren = children.AsReadOnly();
            Version = version;
        }

        public ItemId ItemId { get; } // 运行时实例标识（可能为 ItemId.None，由网络层负责拒绝）。

        public BackpackTag ItemType { get; } // 物品类型标签（模板身份）。

        public int StackCount { get; } // 快照时刻的堆叠数副本；唯一来源仍是 RuntimeItem.StackCount。
        public WeaponStateSnapshot Weapon { get; }
        public MagazineStateSnapshot Magazine { get; }
        public byte Version { get; }
        public IReadOnlyList<InstalledItemSnapshot> InstalledChildren => installedChildren ?? Array.Empty<InstalledItemSnapshot>();

        internal static IEnumerable<InstalledItemSnapshot> LegacyChildren(WeaponStateSnapshot weapon)
        {
            if (weapon == null) yield break;
            if (weapon.Magazine.HasValue) yield return new InstalledItemSnapshot(ItemAssemblySlots.Magazine, weapon.Magazine.Value);
            foreach (AttachmentStateSnapshot child in weapon.Attachments)
            {
                if (child.Slot != WeaponAttachmentSlot.Muzzle) throw new ArgumentException("无法映射旧配件槽。");
                yield return new InstalledItemSnapshot(ItemAssemblySlots.Muzzle, child.Item);
            }
        }

        public IReadOnlyList<FragmentNumberState> Numbers => numbers ?? Array.Empty<FragmentNumberState>(); // 按节点坐标升序排列的数值状态，顺序与模板遍历顺序一致，供确定性比较与载荷编码使用。

        /// <inheritdoc/>
        public bool Equals(RuntimeItemSnapshot other)
        {
            if (ItemId != other.ItemId || StackCount != other.StackCount) return false;
            if (!string.Equals(ItemType.ToString(), other.ItemType.ToString(), StringComparison.Ordinal)) return false;
            if (!Equals(Weapon, other.Weapon) || !Equals(Magazine, other.Magazine)) return false;

            if (InstalledChildren.Count != other.InstalledChildren.Count) return false;
            for (int i = 0; i < InstalledChildren.Count; i++) if (!InstalledChildren[i].Equals(other.InstalledChildren[i])) return false;
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
                hash = (hash * 397) ^ (Weapon?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ (Magazine?.GetHashCode() ?? 0);
                foreach (InstalledItemSnapshot child in InstalledChildren) hash = hash * 397 ^ child.GetHashCode();
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
