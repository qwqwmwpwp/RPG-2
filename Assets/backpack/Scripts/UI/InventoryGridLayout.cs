using System;
using System.Collections.Generic;
using UnityEngine;
using RPG4.Backpack.Items;

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

    /// <summary>仅用于本机 UI 的占位排布，不改变库存容器。</summary>
    public sealed class InventoryGridLayout
    {
        private const int NoOwner = -1;

        private readonly Dictionary<ItemId, GridDisplayPlacement> previous = new Dictionary<ItemId, GridDisplayPlacement>();
        private readonly List<GridDisplayItem> sorted = new List<GridDisplayItem>();
        private readonly HashSet<ItemId> seen = new HashSet<ItemId>();
        private bool[] occupied;
        private ItemId[] ownerIds;
        private int[] ownerUpperLefts;

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
            previous.Clear();
            Placed.Clear();
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

        /// <summary>按「上次位置优先、其余行优先寻位」排布。</summary>
        public void Arrange(IReadOnlyList<GridDisplayItem> items) => Arrange(items, null);

        /// <summary>排布物品，显式指定位置优先于上次位置，放不下则回退到上次位置或自动寻位；位置是纯本地 UI 状态，不参与网络复制，也不改变库存容器。</summary>
        public void Arrange(IReadOnlyList<GridDisplayItem> items, IReadOnlyDictionary<ItemId, Vector2Int> preferred)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            Placed.Clear();
            Overflow.Clear();
            sorted.Clear();
            seen.Clear();
            Array.Clear(occupied, 0, occupied.Length);
            Array.Clear(ownerIds, 0, ownerIds.Length);
            for (int i = 0; i < ownerUpperLefts.Length; i++) ownerUpperLefts[i] = NoOwner;
            for (int i = 0; i < items.Count; i++)
            {
                if (!seen.Add(items[i].Id)) throw new ArgumentException("物品标识重复。", nameof(items));
                sorted.Add(items[i]);
            }
            sorted.Sort((a, b) => a.Id.CompareTo(b.Id));
            var pending = new List<GridDisplayItem>();
            foreach (GridDisplayItem item in sorted)
            {
                if (preferred != null
                    && preferred.TryGetValue(item.Id, out Vector2Int pinned)
                    && Fits(item, pinned))
                {
                    Place(item, pinned);
                    continue;
                }

                if (previous.TryGetValue(item.Id, out GridDisplayPlacement old)
                    && Fits(item, old.Position))
                    Place(item, old.Position);
                else
                    pending.Add(item);
            }
            foreach (GridDisplayItem item in pending)
            {
                bool found = false;
                for (int y = 0; y < Rows && !found; y++)
                    for (int x = 0; x < Columns && !found; x++)
                    {
                        var position = new Vector2Int(x, y);
                        if (!Fits(item, position)) continue;
                        Place(item, position);
                        found = true;
                    }
                if (!found) Overflow.Add(item);
            }
            previous.Clear();
            foreach (GridDisplayPlacement placement in Placed) previous.Add(placement.Item.Id, placement);
            Placed.Sort((a, b) => a.Item.Id.CompareTo(b.Item.Id));
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
                    ownerIds[cell] = item.Id;
                    ownerUpperLefts[cell] = upperLeft;
                }

            Placed.Add(new GridDisplayPlacement(item, position));
        }
    }
}
