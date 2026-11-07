using System.Collections.Generic;

namespace RPG4.Backpack.Core
{
    /// <summary>空间查询的汇总结果：能否放下、可补入总量与剩余量、是否可堆叠；查询需增量填充故用 class 并复用实例以避免逐帧 GC，明细按栅格顺序排列。泛型化以避免 Core 反向依赖 Items 程序集。</summary>
    public sealed class SlotAvailabilityResult<TItem> where TItem : class
    {
        private readonly List<SlotAvailability> slotAvailabilities = new List<SlotAvailability>();

        public TItem Item { get; set; } // 被查询或待放入的物品；查询本身不写此字段，由调用方按需赋值。

        public int TotalRoomToFill { get; set; } // 本次可补入的总量，即各槽位 AmountToFill 之和；不可堆叠物品每格计 1，此时等于可放置槽位数。

        public int Remainder { get; set; } // 补入后仍放不下的剩余量（部分填充分支回传此值）。

        public bool IsStackable { get; set; } // 与 TotalRoomToFill 共同决定走哪个分支。

        public IReadOnlyList<SlotAvailability> SlotAvailabilities => slotAvailabilities; // 逐槽位明细；索引与物品占用的上左索引同口径。

        public int SlotCount => slotAvailabilities.Count; // 明细条数。

        public bool HasRoom => TotalRoomToFill > 0; // 能否放下一部分或全部。

        /// <summary>追加一条槽位可用性（空间查询循环内调用）。</summary>
        public void Add(SlotAvailability availability) => slotAvailabilities.Add(availability);

        /// <summary>复位为「无空间、无候选」的初始态，供实例复用。</summary>
        public void Clear()
        {
            slotAvailabilities.Clear();
            Item = null;
            TotalRoomToFill = 0;
            Remainder = 0;
            IsStackable = false;
        }

        /// <summary>调试文本：物品、可补入总量、剩余量、可堆叠与明细条数。</summary>
        public override string ToString() =>
            $"SlotAvailabilityResult(item={(Item == null ? "null" : Item.ToString())}, totalRoomToFill={TotalRoomToFill}, " +
            $"remainder={Remainder}, isStackable={IsStackable}, slots={slotAvailabilities.Count})";
    }
}
