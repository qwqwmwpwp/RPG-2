namespace RPG4.Backpack.Core
{
    /// <summary>悬停落点的空间查询结果：落点能否放下，以及被挡时挡路的是否为单个可交换物品；悬停反馈据此分为高亮、置灰、无高亮三分支。泛型化以避免 Core 反向依赖 Items 程序集。</summary>
    public struct SpaceQueryResult<TItem> where TItem : class
    {
        public const int NoIndex = -1; // 无效索引。

        /// <summary>记录落点可放置性与挡路物品。</summary>
        public SpaceQueryResult(bool hasSpace, TItem validItem, int upperLeftIndex)
        {
            HasSpace = hasSpace;
            ValidItem = validItem;
            UpperLeftIndex = upperLeftIndex;
        }

        public bool HasSpace { get; set; } // 落点是否完全空置：扫描范围内有一格被占即为 false。

        public TItem ValidItem { get; set; } // 挡路的单个可交换物品；仅当所有被占格属于同一个上左索引时才有值，否则为 null。

        public int UpperLeftIndex { get; set; } // 挡路物品的上左格索引，不是悬停落点索引：置灰高亮以它为锚点，尺寸另取该物品占位尺寸。

        public bool CanSwap => ValidItem != null; // 是否存在可交换的单个物品。

        public static SpaceQueryResult<TItem> Empty => new SpaceQueryResult<TItem>(false, null, NoIndex); // 空结果：无空间、无候选、索引 -1。

        /// <summary>调试文本：可放置性、挡路物品、上左索引与能否交换。</summary>
        public override string ToString() =>
            $"SpaceQueryResult(hasSpace={HasSpace}, validItem={(ValidItem == null ? "null" : ValidItem.ToString())}, " +
            $"upperLeftIndex={UpperLeftIndex}, canSwap={CanSwap})";
    }
}
