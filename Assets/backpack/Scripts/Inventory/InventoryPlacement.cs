using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>不可变放置记录：一个运行时物品在容器中的上左格索引与冻结的占位尺寸；同一物品只存在一条记录且只在提交阶段整体替换，尺寸在记录内冻结以保证占用矩阵重建与网络投影基于同一份历史尺寸，不含堆叠数（唯一来源为 RuntimeItem.StackCount）。</summary>
    public readonly struct InventoryPlacement : IEquatable<InventoryPlacement>
    {
        public const int NoIndex = -1; // 无效上左索引。

        public static readonly InventoryPlacement Invalid = new InventoryPlacement(null, NoIndex, GridCoord.One); // 无效记录：无物品、索引 -1、尺寸 1×1。

        /// <summary>以物品、上左格索引与占位尺寸构造记录。</summary>
        public InventoryPlacement(RuntimeItem item, int upperLeftIndex, GridCoord dimensions)
        {
            Item = item;
            UpperLeftIndex = upperLeftIndex;
            Dimensions = dimensions;
        }

        public RuntimeItem Item { get; } // 被放置的运行时物品；无效记录为 null。

        public int UpperLeftIndex { get; } // 上左格线性索引（行优先）；无效为 NoIndex。

        public GridCoord Dimensions { get; } // 占位尺寸（宽 X、高 Y，单位格）。

        public bool IsValid => Item != null && UpperLeftIndex >= 0; // 记录是否有效（有物品且索引非负）。

        public int CellCount => Dimensions.X * Dimensions.Y; // 占用格数（宽 × 高）。

        /// <inheritdoc/>
        public bool Equals(InventoryPlacement other) =>
            ReferenceEquals(Item, other.Item) &&
            UpperLeftIndex == other.UpperLeftIndex &&
            Dimensions == other.Dimensions;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is InventoryPlacement other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            int itemHash = Item == null ? 0 : Item.GetHashCode();
            return ((itemHash * 397) ^ UpperLeftIndex) * 397 ^ Dimensions.GetHashCode();
        }

        /// <summary>可诊断文本。</summary>
        public override string ToString() =>
            $"InventoryPlacement(item={(Item == null ? "null" : Item.ItemType.ToString())}, " +
            $"upperLeftIndex={UpperLeftIndex}, dimensions={Dimensions})";
    }
}
