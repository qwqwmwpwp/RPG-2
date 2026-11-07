using System;
using System.Collections.Generic;
using UnityEngine;
using RPG4.Backpack.Items;
using RPG4.Backpack.Inventory;

namespace RPG4.Backpack.UI
{
    /// <summary>单个物品在网格中的展示标识与单位格尺寸。</summary>
    public readonly struct GridDisplayItem
    {
        public readonly ItemId Id;
        public readonly int Width;
        public readonly int Height;

        /// <summary>创建展示项；标识无效或尺寸小于 1 时抛出异常。</summary>
        public GridDisplayItem(ItemId id, int width, int height)
        {
            if (!id.IsValid) throw new ArgumentException("物品标识不可为空。", nameof(id));
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
            Id = id;
            Width = width;
            Height = height;
        }
    }

    /// <summary>展示项与其在网格中的落点。</summary>
    public readonly struct GridDisplayPlacement
    {
        public readonly GridDisplayItem Item;
        public readonly Vector2Int Position;

        /// <summary>创建物品与落点的绑定。</summary>
        public GridDisplayPlacement(GridDisplayItem item, Vector2Int position)
        {
            Item = item;
            Position = position;
        }
    }

    /// <summary>权威放置记录的只读 UI 投影；不自动寻位，也不保存独立位置。</summary>
    public sealed class InventoryGridLayout
    {
        private const int NoOwner = -1;

        private bool[] occupied;
        private ItemId[] ownerIds;
        private int[] ownerUpperLefts;
        private readonly Dictionary<ItemId, int> placedIndices = new Dictionary<ItemId, int>();
        private readonly HashSet<int> changedCells = new HashSet<int>();
        public IReadOnlyCollection<int> ChangedCells => changedCells;
        public int OccupiedCellCount { get; private set; }

        public int Columns { get; } // 网格列数。
        public int Rows { get; } // 网格行数。
        public List<GridDisplayPlacement> Placed { get; } = new List<GridDisplayPlacement>(); // 本帧成功排布的物品及落点。
        public List<GridDisplayItem> Overflow { get; } = new List<GridDisplayItem>(); // 本帧放不下、未参与排布的物品。
        public IReadOnlyList<bool> Occupied => occupied; // 各格是否被占用（行优先线性索引）。

        /// <summary>创建指定行列的本地占位网格；行列非正或总格数超过 65536 时抛出异常。</summary>
        public InventoryGridLayout(int columns, int rows)
        {
            if (columns < 1 || rows < 1 || (long)columns * rows > 65536)
                throw new ArgumentOutOfRangeException(nameof(columns));
            Columns = columns;
            Rows = rows;
            occupied = new bool[columns * rows];
            ownerIds = new ItemId[columns * rows];
            ownerUpperLefts = new int[columns * rows];
        }

        /// <summary>清空排布结果与占用标记。</summary>
        public void Clear()
        {
            Placed.Clear();
            placedIndices.Clear();
            changedCells.Clear();
            OccupiedCellCount = 0;
            Overflow.Clear();
            Array.Clear(occupied, 0, occupied.Length);
            Array.Clear(ownerIds, 0, ownerIds.Length);
            for (int i = 0; i < ownerUpperLefts.Length; i++) ownerUpperLefts[i] = NoOwner;
        }

        /// <summary>取某格当前被哪个物品占用及其上左线性索引；越界或空格返回 false，此时标识为 ItemId.None、索引为 -1。</summary>
        public bool TryGetCellOwner(int index, out ItemId ownerId, out int upperLeftIndex)
        {
            if (index < 0 || index >= ownerIds.Length)
            {
                ownerId = ItemId.None;
                upperLeftIndex = NoOwner;
                return false;
            }

            ownerId = ownerIds[index];
            upperLeftIndex = ownerUpperLefts[index];
            return ownerId.IsValid;
        }

        /// <summary>逐项投影权威落点。缺失、越界或冲突记录只进入未显示列表，绝不猜测新位置。</summary>
        public void Project(IReadOnlyList<GridDisplayItem> items, IBackpackView inventory)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            Clear();
            foreach (GridDisplayItem item in items)
            {
                if (inventory == null || !inventory.IsReady
                    || !inventory.TryGetPlacement(item.Id, out InventoryPlacement placement)
                    || placement.UpperLeftIndex < 0 || placement.UpperLeftIndex >= ownerIds.Length
                    || placement.Dimensions.X < 1 || placement.Dimensions.Y < 1)
                {
                    Overflow.Add(item);
                    continue;
                }
                var projected = new GridDisplayItem(item.Id, placement.Dimensions.X, placement.Dimensions.Y);
                var position = new Vector2Int(placement.UpperLeftIndex % Columns, placement.UpperLeftIndex / Columns);
                if (Fits(projected, position)) Place(projected, position);
                else Overflow.Add(projected);
            }
        }

        public bool TryGetPlacement(ItemId id, out GridDisplayPlacement placement)
        {
            if (placedIndices.TryGetValue(id, out int index)) { placement = Placed[index]; return true; }
            placement = default;
            return false;
        }

        /// <summary>整批释放旧占格后投影新落点；异常或溢出时由调用方回退到完整投影。</summary>
        public bool ProjectChanges(IReadOnlyCollection<ItemId> ids, IReadOnlyDictionary<ItemId, InventoryPlacement> placements)
        {
            if (placements == null || Overflow.Count > 0) return false;
            changedCells.Clear();
            foreach (ItemId id in ids) Remove(id);
            foreach (ItemId id in ids)
            {
                if (!id.IsValid) return false;
                if (!placements.TryGetValue(id, out InventoryPlacement placement)) continue;
                if (!placement.IsValid || placement.UpperLeftIndex >= ownerIds.Length
                    || placement.Dimensions.X < 1 || placement.Dimensions.Y < 1) return false;
                var item = new GridDisplayItem(id, placement.Dimensions.X, placement.Dimensions.Y);
                var position = new Vector2Int(placement.UpperLeftIndex % Columns, placement.UpperLeftIndex / Columns);
                if (!Fits(item, position)) return false;
                Place(item, position);
            }
            return true;
        }

        private void Remove(ItemId id)
        {
            if (!placedIndices.TryGetValue(id, out int index)) return;
            GridDisplayPlacement previous = Placed[index];
            for (int y = previous.Position.y; y < previous.Position.y + previous.Item.Height; y++)
            for (int x = previous.Position.x; x < previous.Position.x + previous.Item.Width; x++)
            {
                int cell = y * Columns + x;
                occupied[cell] = false;
                ownerIds[cell] = ItemId.None;
                ownerUpperLefts[cell] = NoOwner;
                changedCells.Add(cell);
                OccupiedCellCount--;
            }
            int last = Placed.Count - 1;
            if (index != last)
            {
                Placed[index] = Placed[last];
                placedIndices[Placed[index].Item.Id] = index;
            }
            Placed.RemoveAt(last);
            placedIndices.Remove(id);
        }

        /// <summary>判断物品能否放在指定落点（不越界且覆盖范围内无占用）。</summary>
        private bool Fits(GridDisplayItem item, Vector2Int position)
        {
            if (position.x < 0 || position.y < 0 || item.Width > Columns - position.x || item.Height > Rows - position.y)
                return false;
            for (int y = position.y; y < position.y + item.Height; y++)
                for (int x = position.x; x < position.x + item.Width; x++)
                    if (occupied[y * Columns + x]) return false;
            return true;
        }

        /// <summary>在指定落点登记占用并记录放置结果。</summary>
        private void Place(GridDisplayItem item, Vector2Int position)
        {
            int upperLeft = position.y * Columns + position.x;
            for (int y = position.y; y < position.y + item.Height; y++)
                for (int x = position.x; x < position.x + item.Width; x++)
                {
                    int cell = y * Columns + x;
                    occupied[cell] = true;
                    OccupiedCellCount++;
                    changedCells.Add(cell);
                    ownerIds[cell] = item.Id;
                    ownerUpperLefts[cell] = upperLeft;
                }

            placedIndices[item.Id] = Placed.Count;
            Placed.Add(new GridDisplayPlacement(item, position));
        }
    }
}
