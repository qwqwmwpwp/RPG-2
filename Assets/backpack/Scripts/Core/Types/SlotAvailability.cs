using System;
using UnityEngine;

namespace RPG4.Backpack.Core
{
    /// <summary>单个槽位的可用性：索引、还能补入的堆叠量、该索引上是否已有物品；空间查询的基本单元，交换分支直接读 <see cref="HasItemAtIndex"/> 与 <see cref="AmountToFill"/>。</summary>
    [Serializable]
    public struct SlotAvailability
    {
        public const int NoIndex = -1; // 无效索引。

        [SerializeField]
        private int index;

        [SerializeField]
        private int amountToFill;

        [SerializeField]
        private bool hasItemAtIndex;

        /// <summary>记录某索引的堆叠可补入量与占用状态。</summary>
        public SlotAvailability(int index, int amountToFill, bool hasItemAtIndex)
        {
            this.index = index;
            this.amountToFill = amountToFill;
            this.hasItemAtIndex = hasItemAtIndex;
        }

        /// <summary>槽位线性索引（行优先）；无效为 <see cref="NoIndex"/>，被占用时记录占用物品的上左索引。</summary>
        public int Index
        {
            get => index;
            set => index = value;
        }

        /// <summary>该槽位可补入的堆叠量：仅可堆叠物品记数，不可堆叠时恒为 0（放置量改由 <see cref="SlotAvailabilityResult{TItem}.TotalRoomToFill"/> 每格计 1）。</summary>
        public int AmountToFill
        {
            get => amountToFill;
            set => amountToFill = value;
        }

        /// <summary>该索引上是否已有物品（交换判据：仅单个物品挡路才允许交换）。</summary>
        public bool HasItemAtIndex
        {
            get => hasItemAtIndex;
            set => hasItemAtIndex = value;
        }

        public static SlotAvailability Invalid => new SlotAvailability(NoIndex, 0, false); // 索引 -1、不可填、无物品。

        public bool IsValid => index >= 0; // 索引有效。

        public bool HasRoom => amountToFill > 0; // 该槽位能否补入堆叠量。

        /// <summary>调试文本：索引、可补入量与占用状态。</summary>
        public override string ToString() =>
            $"SlotAvailability(index={index}, amountToFill={amountToFill}, hasItem={hasItemAtIndex})";
    }
}
