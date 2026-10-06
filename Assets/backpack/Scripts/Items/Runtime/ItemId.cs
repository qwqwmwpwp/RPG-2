using System;

namespace RPG4.Backpack.Items
{
    /// <summary>运行时物品实例的唯一不可变标识：0（None）表示无标识，有效标识由 ItemIdGenerator 产出且不小于 1，与模板类型身份无关；本类型不感知网络框架，跨端传输由适配层显式读写底层 Value。</summary>
    public readonly struct ItemId : IEquatable<ItemId>, IComparable<ItemId>
    {
        public const ulong NoneValue = 0UL; // 「无标识」的底层值，恒为 0。

        public static readonly ItemId None = default; // 无标识值，等价于 default(ItemId)。

        private readonly ulong value; // 底层标识值。

        /// <summary>以指定底层值构造标识；传 0 得到 None。</summary>
        public ItemId(ulong value)
        {
            this.value = value;
        }

        public ulong Value => value; // 底层标识值。

        public bool IsNone => value == NoneValue; // 是否为空标识。

        public bool IsValid => value != NoneValue; // 是否为有效标识（非 0）。

        /// <inheritdoc/>
        public bool Equals(ItemId other)
        {
            return value == other.value;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is ItemId other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return value.GetHashCode();
        }

        /// <inheritdoc/>
        public int CompareTo(ItemId other)
        {
            return value.CompareTo(other.value);
        }

        /// <summary>可诊断文本，例如 ItemId(None) 或 ItemId(42)。</summary>
        public override string ToString()
        {
            return IsNone ? "ItemId(None)" : "ItemId(" + value + ")";
        }

        /// <summary>值相等。</summary>
        public static bool operator ==(ItemId left, ItemId right)
        {
            return left.value == right.value;
        }

        /// <summary>值不等。</summary>
        public static bool operator !=(ItemId left, ItemId right)
        {
            return left.value != right.value;
        }

        /// <summary>按底层值比较。</summary>
        public static bool operator <(ItemId left, ItemId right)
        {
            return left.value < right.value;
        }

        /// <summary>按底层值比较。</summary>
        public static bool operator <=(ItemId left, ItemId right)
        {
            return left.value <= right.value;
        }

        /// <summary>按底层值比较。</summary>
        public static bool operator >(ItemId left, ItemId right)
        {
            return left.value > right.value;
        }

        /// <summary>按底层值比较。</summary>
        public static bool operator >=(ItemId left, ItemId right)
        {
            return left.value >= right.value;
        }

        /// <summary>显式取出底层值（网络写入、存档等需要显式转换）。</summary>
        public static explicit operator ulong(ItemId id)
        {
            return id.value;
        }

        /// <summary>显式从底层值构造。</summary>
        public static explicit operator ItemId(ulong value)
        {
            return new ItemId(value);
        }
    }
}
