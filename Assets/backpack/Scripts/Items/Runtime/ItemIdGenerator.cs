using System;

namespace RPG4.Backpack.Items
{
    /// <summary>运行时物品标识生成器：在单个服务端会话内单调递增地产出 ItemId；只在服务端主线程使用、一个会话一个实例且不加锁，每次生成 O(1) 且无堆分配，标识空间耗尽时抛 OverflowException 绝不回绕。</summary>
    public sealed class ItemIdGenerator
    {
        public const ulong FirstValue = 1UL; // 首个有效标识的底层值。

        private ulong lastIssued; // 最近一次发放的底层值。

        /// <summary>创建从 FirstValue 开始发放的生成器。</summary>
        public ItemIdGenerator()
        {
        }

        /// <summary>创建从指定值之后继续递增的生成器，用于会话续接或存档恢复（0 表示尚未发放）。</summary>
        public ItemIdGenerator(ulong lastIssuedValue)
        {
            lastIssued = lastIssuedValue;
        }

        public ItemId LastIssued => new ItemId(lastIssued); // 最近一次发放的标识；尚未发放时为 ItemId.None。

        /// <summary>产出下一个唯一标识；标识空间（ulong）耗尽时抛出 OverflowException，绝不回绕。</summary>
        public ItemId Next()
        {
            if (lastIssued == ulong.MaxValue)
            {
                throw new OverflowException("ItemId 标识空间已耗尽（ulong.MaxValue），拒绝回绕以避免产生重复标识。");
            }

            lastIssued++;
            return new ItemId(lastIssued);
        }
    }
}
