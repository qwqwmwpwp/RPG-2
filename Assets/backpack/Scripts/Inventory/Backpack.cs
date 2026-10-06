using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>背包类：长度等于「宽 × 高」的格子数组到物品集合的只读映射，每格记录该格物品在 <see cref="BackpackData"/> 中的下标，<see cref="NoIndex"/> 表示空格；多格物品的每个占格写同一下标，构造注入为唯一入口，不提供任何变更操作。</summary>
    public sealed class Backpack
    {
        public const int NoIndex = -1; // 空格标记与无效下标。

        public const int MaxCellCount = 65536; // 格子总数上限（256 × 256）。

        private readonly BackpackData data; // 被映射的物品集合。
        private readonly int[] cells; // 逐格物品下标，NoIndex 为空。

        /// <summary>以物品集合、背包宽高与逐格物品下标数组构造；宽高非正、格子总数超限、数组长度不符或下标超出集合范围时抛出异常；数组会被复制，外部后续改动不影响本实例。</summary>
        public Backpack(BackpackData data, int width, int height, int[] cellItemIndices)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "背包宽度必须大于 0。");
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "背包高度必须大于 0。");

            long cellCount = (long)width * height;
            if (cellCount > MaxCellCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width), width, $"格子总数 {cellCount} 超过上限 {MaxCellCount}（{width} × {height}）。");
            }

            if (cellItemIndices == null) throw new ArgumentNullException(nameof(cellItemIndices));
            if (cellItemIndices.Length != (int)cellCount)
            {
                throw new ArgumentException(
                    $"格子数组长度 {cellItemIndices.Length} 与背包尺寸 {width} × {height} = {cellCount} 不一致。",
                    nameof(cellItemIndices));
            }

            cells = new int[(int)cellCount];
            int occupiedCellCount = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                int itemIndex = cellItemIndices[i];
                if (itemIndex != NoIndex && (itemIndex < 0 || itemIndex >= data.Count))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(cellItemIndices), itemIndex,
                        $"第 {i} 格的物品下标 {itemIndex} 超出物品集合范围 0..{data.Count - 1}。");
                }

                cells[i] = itemIndex;
                if (itemIndex != NoIndex) occupiedCellCount++;
            }

            this.data = data;
            Width = width;
            Height = height;
            OccupiedCellCount = occupiedCellCount;
        }

        public BackpackData Data => data; // 被映射的物品集合。
        public int Width { get; } // 背包宽度（列数）。
        public int Height { get; } // 背包高度（行数）。
        public int CellCount => cells.Length; // 格子总数（宽 × 高）。
        public int OccupiedCellCount { get; } // 被占用的格子数（构造期预计算）。
        public int EmptyCellCount => cells.Length - OccupiedCellCount; // 空格数。

        /// <summary>线性格子下标是否落在背包范围内。</summary>
        public bool IsCellIndexValid(int cellIndex) => cellIndex >= 0 && cellIndex < cells.Length;

        /// <summary>该格是否被物品占用；下标非法返回 false。</summary>
        public bool IsCellOccupied(int cellIndex) => IsCellIndexValid(cellIndex) && cells[cellIndex] != NoIndex;

        /// <summary>该格物品在物品集合中的下标；空格或下标非法返回 NoIndex。</summary>
        public int GetItemIndexAt(int cellIndex) => IsCellIndexValid(cellIndex) ? cells[cellIndex] : NoIndex;

        /// <summary>该格上的物品；空格或下标非法返回 null。</summary>
        public BackpackItem GetItemAt(int cellIndex)
        {
            return data.TryGetItem(GetItemIndexAt(cellIndex), out BackpackItem item) ? item : null;
        }

        /// <summary>该网格坐标上的物品；坐标越界返回 null。</summary>
        public BackpackItem GetItemAt(GridCoord coord)
        {
            if (coord.X < 0 || coord.X >= Width || coord.Y < 0 || coord.Y >= Height) return null;

            return GetItemAt(GridUtils.GetIndexFromPosition(coord, Width));
        }

        /// <summary>物品是否在本背包中（已在集合登记且至少占一格）。</summary>
        public bool Contains(BackpackItem item)
        {
            int itemIndex = data.IndexOf(item);
            if (itemIndex < 0) return false;

            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == itemIndex) return true;
            }

            return false;
        }

        /// <summary>物品占格中的上左格线性索引（占格中的最小索引，行优先下即矩形左上角）；不在背包返回 false 且 upperLeftIndex 为 NoIndex。</summary>
        public bool TryGetUpperLeftIndex(BackpackItem item, out int upperLeftIndex)
        {
            upperLeftIndex = NoIndex;
            int itemIndex = data.IndexOf(item);
            if (itemIndex < 0) return false;

            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] != itemIndex) continue;

                upperLeftIndex = i;
                return true;
            }

            return false;
        }

        /// <summary>物品占用的全部格子下标（升序）：buffer 先清空再填充；物品不在背包时清空并返回 false。</summary>
        public bool TryGetCellIndices(BackpackItem item, List<int> buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            buffer.Clear();
            int itemIndex = data.IndexOf(item);
            if (itemIndex < 0) return false;

            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == itemIndex) buffer.Add(i);
            }

            return buffer.Count > 0;
        }

        /// <summary>映射一致性校验（只读）：每个有占格的物品，其占格必须与自身尺寸构成完整矩形且不越界；未出现在背包中的物品跳过不算错。</summary>
        public bool TryValidateMapping(out string error)
        {
            for (int itemIndex = 0; itemIndex < data.Count; itemIndex++)
            {
                if (!data.TryGetItem(itemIndex, out BackpackItem item)) continue;

                int firstCell = NoIndex;
                int occupiedCellCount = 0;
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] != itemIndex) continue;

                    if (firstCell == NoIndex) firstCell = i;
                    occupiedCellCount++;
                }

                if (firstCell == NoIndex) continue;

                GridCoord size = item.Size;
                if (!GridUtils.IsInGridBounds(firstCell, size, Width, Height))
                {
                    error = $"物品下标 {itemIndex} 的占格越界：上左格 {firstCell} 与尺寸 {size} 超出 {Width} × {Height} 的背包范围。";
                    return false;
                }

                if (occupiedCellCount != size.X * size.Y)
                {
                    error = $"物品下标 {itemIndex} 的占格数 {occupiedCellCount} 与尺寸 {size} 不一致。";
                    return false;
                }

                GridCoord origin = GridUtils.GetPositionFromIndex(firstCell, Width);
                for (int row = 0; row < size.Y; row++)
                {
                    for (int column = 0; column < size.X; column++)
                    {
                        int index = GridUtils.GetIndexFromPosition(new GridCoord(origin.X + column, origin.Y + row), Width);
                        if (cells[index] != itemIndex)
                        {
                            error = $"物品下标 {itemIndex} 的占格不构成完整矩形：第 {index} 格不属于该物品。";
                            return false;
                        }
                    }
                }
            }

            error = null;
            return true;
        }
    }
}
