using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>库存真相源：统一承担物品集合、格子映射与读写查询，唯一权威地持有「物品集合 + 上左位置 + 格子占用」，两份状态只在提交阶段同源写入、不存在中间态；物品下标按 ItemId 排序并按需缓存，不另建权威数据；堆叠数不缓存（唯一来源 RuntimeItem.StackCount）；纯 C# 数据层，不引用 UI / MonoBehaviour / 网络；按主线程单线程访问、不加锁，查询复用轮次戳缓冲做到零 GC；变更成功后经 F6 事件总线广播（source 为容器实例）。</summary>
    public sealed class InventoryContainer
    {
        public const int NoIndex = InventoryPlacement.NoIndex; // 无效索引常量。

        public const int MaxSlotCount = 65536; // 槽位总数上限（256 × 256），用于拒绝非正尺寸、整数溢出与不可接受的分配规模。

        private static readonly Comparison<RuntimeItem> ItemIdComparison = static (left, right) => left.ItemId.CompareTo(right.ItemId); // 确定性排序比较器，静态缓存避免每次调用分配委托。

        private readonly InventoryPlacement[] cells; // 逐格放置记录，空置为 Invalid。
        private readonly Dictionary<RuntimeItem, InventoryPlacement> placements; // 物品 → 放置记录，供 O(1) 定位与枚举。
        private readonly IReadOnlyCollection<RuntimeItem> itemsView; // placements 键的只读视图，避免每次枚举重新分配。
        private readonly List<RuntimeItem> indexedItems = new List<RuntimeItem>(); // 按 ItemId 排序的查询缓存，下标口径与 FillItemsByItemId 一致。
        private readonly int[] claimed; // 查询标记缓冲：值等于 claimStamp 才算「本轮已检查」。
        private readonly List<StackStep> stackSteps = new List<StackStep>(); // 接收规划复用的合并缓冲，成功规划后复制到独立计划以隔离事件重入。

        private int claimStamp; // 查询轮次戳，每轮自增；初值 0 与缓冲初值相同，故必须先 BeginClaimPass。
        private bool itemIndicesDirty = true; // 提交变更后失效，首次下标查询时重建。

        /// <summary>创建指定行列数的容器；行列非正或 rows × columns 超过 MaxSlotCount 时抛 ArgumentOutOfRangeException（用长整型探测溢出）。</summary>
        public InventoryContainer(int rows, int columns)
        {
            if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows), rows, "行数必须大于 0。");
            if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns), columns, "列数必须大于 0。");

            long slotCount = (long)rows * columns;
            if (slotCount > MaxSlotCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rows), rows, $"槽位总数 {slotCount} 超过上限 {MaxSlotCount}（{rows} × {columns}）。");
            }

            Rows = rows;
            Columns = columns;
            cells = new InventoryPlacement[(int)slotCount];
            claimed = new int[(int)slotCount];

            // default(InventoryPlacement) 的上左索引是 0 而非 -1，不显式清零就无法区分「空置」与「上左格在第 0 格」。
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = InventoryPlacement.Invalid;
            }

            placements = new Dictionary<RuntimeItem, InventoryPlacement>();
            itemsView = placements.Keys;
        }

        public int Rows { get; } // 行数。

        public int Columns { get; } // 列数。

        public int SlotCount => cells.Length; // 槽位总数（行 × 列）。

        /// <summary>已占用的格子数：按权威放置记录的冻结尺寸求和，不维护第二份计数状态。</summary>
        public int OccupiedCellCount
        {
            get
            {
                int count = 0;
                foreach (InventoryPlacement placement in placements.Values)
                {
                    count += placement.CellCount;
                }

                return count;
            }
        }

        public int EmptyCellCount => SlotCount - OccupiedCellCount; // 空格数。

        public int ItemCount => placements.Count; // 当前容器内的物品数量。

        public IReadOnlyCollection<RuntimeItem> Items => itemsView; // 当前容器内的物品集合（枚举顺序不保证，勿依赖）。

        public int Revision { get; private set; } // 状态版本号：每次成功提交变更（放置 / 移除 / 合并 / 收编）后 +1、失败路径不变，供网络投影做廉价的「是否有外部变更」判断（比较相等而非大小，溢出回绕不影响语义）。

        /// <summary>按 RuntimeItem.ItemId 升序把物品写入 destination（先清空），顺序与物品下标查询一致；变更后首次读取重建排序缓存，destination 由调用方持有以复用。</summary>
        public void FillItemsByItemId(List<RuntimeItem> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            EnsureItemIndices();
            destination.Clear();
            destination.AddRange(indexedItems);
        }

        // 只读查询

        /// <summary>按物品下标取实例；下标基于当前容器按 ItemId 升序的集合，提交变更后应重新查询；越界返回 false 且 item 为 null。</summary>
        public bool TryGetItem(int itemIndex, out RuntimeItem item)
        {
            EnsureItemIndices();
            if (itemIndex < 0 || itemIndex >= indexedItems.Count)
            {
                item = null;
                return false;
            }

            item = indexedItems[itemIndex];
            return true;
        }

        /// <summary>物品实例在当前 ItemId 升序集合中的下标（按引用判等）；空引用或不在容器中返回 NoIndex；持久标识请使用 ItemId。</summary>
        public int IndexOf(RuntimeItem item)
        {
            if (!Contains(item)) return NoIndex;

            EnsureItemIndices();
            for (int i = 0; i < indexedItems.Count; i++)
            {
                if (ReferenceEquals(indexedItems[i], item)) return i;
            }

            return NoIndex;
        }

        /// <summary>线性索引是否落在此容器内。</summary>
        public bool IsIndexValid(int index) => index >= 0 && index < cells.Length;

        /// <summary>该格是否被物品占用。索引非法时返回 false。</summary>
        public bool IsIndexOccupied(int index) => IsIndexValid(index) && cells[index].IsValid;

        /// <summary>该格物品在当前 ItemId 升序集合中的下标；空格或格子下标非法返回 NoIndex。</summary>
        public int GetItemIndexAt(int cellIndex) => IndexOf(GetItemAt(cellIndex));

        /// <summary>该格所属物品的上左格索引；空置或索引非法时返回 NoIndex。</summary>
        public int GetUpperLeftIndexAt(int index) =>
            IsIndexValid(index) ? cells[index].UpperLeftIndex : NoIndex;

        /// <summary>该格上的物品；空置或索引非法时返回 null。</summary>
        public RuntimeItem GetItemAt(int index) => IsIndexValid(index) ? cells[index].Item : null;

        /// <summary>该网格坐标上的物品；空格或坐标越界返回 null。</summary>
        public RuntimeItem GetItemAt(GridCoord coord)
        {
            if (coord.X < 0 || coord.X >= Columns || coord.Y < 0 || coord.Y >= Rows) return null;

            return GetItemAt(GridUtils.GetIndexFromPosition(coord, Columns));
        }

        /// <summary>物品是否在容器内。</summary>
        public bool Contains(RuntimeItem item) => item != null && placements.ContainsKey(item);

        /// <summary>取物品的放置记录。</summary>
        public bool TryGetPlacement(RuntimeItem item, out InventoryPlacement placement)
        {
            if (item == null)
            {
                placement = InventoryPlacement.Invalid;
                return false;
            }

            return placements.TryGetValue(item, out placement);
        }

        /// <summary>物品占格的上左格线性索引；物品不在容器中时返回 false 且 upperLeftIndex 为 NoIndex。</summary>
        public bool TryGetUpperLeftIndex(RuntimeItem item, out int upperLeftIndex)
        {
            upperLeftIndex = NoIndex;
            if (!TryGetPlacement(item, out InventoryPlacement placement)) return false;

            upperLeftIndex = placement.UpperLeftIndex;
            return true;
        }

        /// <summary>按放置记录冻结的尺寸，把物品全部占格下标升序写入 buffer（先清空）；物品不在容器中时返回 false。</summary>
        public bool TryGetCellIndices(RuntimeItem item, List<int> buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            buffer.Clear();
            if (!TryGetPlacement(item, out InventoryPlacement placement)) return false;

            RegionCursor cursor = new RegionCursor(Columns, placement.UpperLeftIndex, placement.Dimensions);
            while (cursor.MoveNext(out int index))
            {
                buffer.Add(index);
            }

            return true;
        }

        /// <summary>只读映射一致性查询：每条放置记录按冻结尺寸完整占据矩形，全部格子与对应记录一致且无额外占格；不修改状态或广播事件。</summary>
        public bool TryValidateMapping(out string error)
        {
            foreach (KeyValuePair<RuntimeItem, InventoryPlacement> pair in placements)
            {
                InventoryPlacement placement = pair.Value;
                if (!placement.IsValid || !ReferenceEquals(pair.Key, placement.Item))
                {
                    error = "物品集合与放置记录不一致。";
                    return false;
                }

                GridCoord dimensions = placement.Dimensions;
                if (dimensions.X < 1 || dimensions.Y < 1
                    || !GridUtils.IsInGridBounds(placement.UpperLeftIndex, dimensions, Columns, Rows))
                {
                    error = $"物品 {pair.Key.ItemId} 的上左格 {placement.UpperLeftIndex} 与尺寸 {dimensions} 超出容器范围。";
                    return false;
                }

                RegionCursor cursor = new RegionCursor(Columns, placement.UpperLeftIndex, dimensions);
                while (cursor.MoveNext(out int index))
                {
                    if (cells[index].Equals(placement)) continue;

                    error = $"物品 {pair.Key.ItemId} 的占格不构成完整矩形：第 {index} 格与放置记录不一致。";
                    return false;
                }
            }

            for (int index = 0; index < cells.Length; index++)
            {
                InventoryPlacement cell = cells[index];
                if (!cell.IsValid)
                {
                    if (cell.Equals(InventoryPlacement.Invalid)) continue;

                    error = $"第 {index} 格的空格标记不一致。";
                    return false;
                }

                if (!placements.TryGetValue(cell.Item, out InventoryPlacement placement) || !cell.Equals(placement))
                {
                    error = $"第 {index} 格没有对应的物品放置记录。";
                    return false;
                }

                GridCoord coord = GridUtils.GetPositionFromIndex(index, Columns);
                GridCoord origin = GridUtils.GetPositionFromIndex(placement.UpperLeftIndex, Columns);
                if (coord.X < origin.X || coord.X >= origin.X + placement.Dimensions.X
                    || coord.Y < origin.Y || coord.Y >= origin.Y + placement.Dimensions.Y)
                {
                    error = $"第 {index} 格位于物品 {cell.Item.ItemId} 的记录占格范围之外。";
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>按标识查找容器内物品（线性扫描，物品数通常很少，仅低频操作调用）；标识无效或不在容器返回 false。</summary>
        public bool TryGetItemById(ItemId itemId, out RuntimeItem item)
        {
            item = null;
            if (!itemId.IsValid) return false;

            foreach (RuntimeItem candidate in placements.Keys)
            {
                if (candidate.ItemId.Equals(itemId))
                {
                    item = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>物品的占位尺寸：读 GridFragment 的宽高，缺该 Fragment 或物品为空时按 1×1 处理，与源 UMIS_InventoryGrid::GetItemDimensions 一致。</summary>
        public static GridCoord GetItemDimensions(RuntimeItem item)
        {
            GridFragment grid = item?.GetFragment<GridFragment>();
            return grid == null ? GridCoord.One : new GridCoord(grid.Width, grid.Height);
        }

        /// <summary>悬停落点空间查询：以 startIndex 为上左格扫描 dimensions，整块全空返回 HasSpace；被挡且所有被占格同属一个物品时该物品作为 ValidItem（可交换候选，锚点取该物品上左格）；越界 / 尺寸非法 / 多个不同物品挡路返回空结果；只读。</summary>
        public SpaceQueryResult<RuntimeItem> QuerySpace(int startIndex, GridCoord dimensions)
        {
            if (dimensions.X < 1 || dimensions.Y < 1) return SpaceQueryResult<RuntimeItem>.Empty;
            if (!GridUtils.IsInGridBounds(startIndex, dimensions, Columns, Rows)) return SpaceQueryResult<RuntimeItem>.Empty;

            bool hasSpace = true;
            RuntimeItem occupant = null;
            int occupantUpperLeft = NoIndex;
            bool multipleOccupants = false;

            RegionCursor cursor = new RegionCursor(Columns, startIndex, dimensions);
            while (cursor.MoveNext(out int index))
            {
                InventoryPlacement cell = cells[index];
                if (!cell.IsValid) continue;

                hasSpace = false;
                if (occupant == null)
                {
                    occupant = cell.Item;
                    occupantUpperLeft = cell.UpperLeftIndex;
                }
                else if (!ReferenceEquals(occupant, cell.Item))
                {
                    multipleOccupants = true;
                }
            }

            if (multipleOccupants) return new SpaceQueryResult<RuntimeItem>(false, null, NoIndex);

            return new SpaceQueryResult<RuntimeItem>(
                hasSpace, occupant, occupant != null ? occupantUpperLeft : NoIndex);
        }

        /// <summary>悬停落点空间查询（尺寸取物品自身占位尺寸）。</summary>
        public SpaceQueryResult<RuntimeItem> QuerySpace(int startIndex, RuntimeItem item) =>
            item == null
                ? SpaceQueryResult<RuntimeItem>.Empty
                : QuerySpace(startIndex, GetItemDimensions(item));

        // 容量查询（放得下吗 / 放几个 / 剩多少）

        /// <summary>全网格行优先容量查询（分配一个新结果实例）。</summary>
        public SlotAvailabilityResult<RuntimeItem> QueryAvailability(RuntimeItem item, int amountOverride = -1)
        {
            SlotAvailabilityResult<RuntimeItem> result = new SlotAvailabilityResult<RuntimeItem>();
            FillAvailability(item, amountOverride, result);
            return result;
        }

        /// <summary>全网格行优先容量查询并把结果写入调用方提供的 result（先 Clear，热路径零分配）：明细按行优先追加，每个成功区域只记一条，不可堆叠物品的 AmountToFill 恒为 0、容量由 TotalRoomToFill 每格计 1，无法整体容纳的区域整体跳过且其格子仍可作后续新区域起点；只读。</summary>
        public void FillAvailability(RuntimeItem item, int amountOverride, SlotAvailabilityResult<RuntimeItem> result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            result.Clear();
            if (item == null) return;

            GridCoord dimensions = GetItemDimensions(item);
            int amountToFill = amountOverride >= 0 ? amountOverride : item.StackCount;
            if (dimensions.X < 1 || dimensions.Y < 1 || amountToFill <= 0) return;

            bool isStackable = item.IsStackable;
            int incomingMaxStack = item.MaxStackSize;

            result.Item = item;
            result.IsStackable = isStackable;
            result.Remainder = amountToFill;

            BeginClaimPass();

            for (int index = 0; index < cells.Length && amountToFill > 0; index++)
            {
                if (IsClaimed(index)) continue;

                // 已被占用但不是某物品的上左格 → 该格不可能是任何合法区域的起点，直接跳过。
                InventoryPlacement self = cells[index];
                if (self.IsValid && self.UpperLeftIndex != index) continue;
                if (!GridUtils.IsInGridBounds(index, dimensions, Columns, Rows)) continue;
                if (!TryValidateRegion(index, dimensions, item, isStackable, out RuntimeItem occupant)) continue;

                int room = occupant != null ? occupant.MaxStackSize - occupant.StackCount : incomingMaxStack;
                int fill = isStackable ? Math.Min(amountToFill, room) : 1;
                if (fill <= 0) continue;

                result.TotalRoomToFill += fill;
                result.Add(new SlotAvailability(
                    occupant != null ? cells[index].UpperLeftIndex : index,
                    isStackable ? fill : 0,
                    occupant != null));

                amountToFill -= fill;
                result.Remainder = amountToFill;
                ClaimRegion(index, dimensions);
            }
        }

        /// <summary>校验以 startIndex 为起点的区域能否容纳待放物品：全空通过且 occupant 为 null；整块同属一个物品且其上左格正好是区域起点、双方可堆叠 + 类型精确匹配 + 该堆叠未满则通过并回传该物品；只读。</summary>
        private bool TryValidateRegion(
            int startIndex, GridCoord dimensions, RuntimeItem item, bool isStackable, out RuntimeItem occupant)
        {
            occupant = null;

            RegionCursor cursor = new RegionCursor(Columns, startIndex, dimensions);
            while (cursor.MoveNext(out int index))
            {
                InventoryPlacement cell = cells[index];
                if (!cell.IsValid) continue;

                // 被占格必须同属「上左格正好是区域起点」的同一个物品，否则本区域整体不可用。
                if (cell.UpperLeftIndex != startIndex) return false;
                if (occupant != null && !ReferenceEquals(occupant, cell.Item)) return false;

                occupant = cell.Item;
            }

            return occupant == null || (isStackable
                && occupant.IsStackable
                && occupant.ItemType.MatchesTagExact(item.ItemType)
                && occupant.StackCount < occupant.MaxStackSize);
        }

        /// <summary>把区域内的格子标记为「本轮已检查」。</summary>
        private void ClaimRegion(int startIndex, GridCoord dimensions)
        {
            RegionCursor cursor = new RegionCursor(Columns, startIndex, dimensions);
            while (cursor.MoveNext(out int index))
            {
                claimed[index] = claimStamp;
            }
        }

        /// <summary>开始新一轮查询：自增轮次戳（回绕时清零缓冲），使上一轮的标记全部失效。</summary>
        private void BeginClaimPass()
        {
            if (claimStamp == int.MaxValue)
            {
                Array.Clear(claimed, 0, claimed.Length);
                claimStamp = 0;
            }

            claimStamp++;
        }

        /// <summary>该格在本轮查询中是否已被某个成功区域占用。</summary>
        private bool IsClaimed(int index) => claimed[index] == claimStamp;

        // 原子变更（先完整校验 / 规划，再一次性提交）

        /// <summary>在指定落点放置物品（不做堆叠合并）：要求物品不在容器内且落点整块为空；失败时容器与物品状态完全不变；不支持原地移动，搬运请先 TryRemoveItem。</summary>
        public bool TryPlaceItem(RuntimeItem item, int startIndex, out string error)
        {
            if (!CanInsert(item, nameof(TryPlaceItem), out error)) return false;

            GridCoord dimensions = GetItemDimensions(item);
            if (!GridUtils.IsInGridBounds(startIndex, dimensions, Columns, Rows))
            {
                return Reject(out error, nameof(TryPlaceItem), $"落点 {startIndex} + 尺寸 {dimensions} 越界。");
            }

            if (!IsRegionEmpty(startIndex, dimensions))
            {
                return Reject(out error, nameof(TryPlaceItem), $"落点 {startIndex} 的 {dimensions} 区域已被占用。");
            }

            CommitPlacement(item, startIndex, dimensions);
            BumpRevision();
            error = null;
            NotifyItemAdded(item);
            return true;
        }

        /// <summary>从容器移除物品并释放其全部格子，失败时状态不变。</summary>
        public bool TryRemoveItem(RuntimeItem item, out string error)
        {
            if (item == null) return Reject(out error, nameof(TryRemoveItem), "物品为空。");

            if (!placements.TryGetValue(item, out InventoryPlacement placement))
            {
                return Reject(out error, nameof(TryRemoveItem), "物品不在容器中。");
            }

            ClearCells(placement);
            placements.Remove(item);
            BumpRevision();
            error = null;
            NotifyItemRemoved(item);
            return true;
        }

        /// <summary>把 incoming 并入容器内的 target 堆叠，失败时双方状态完全不变；双方须都可堆叠且类型标签精确一致，可部分并入（受单堆上限限制）；movedCount 等于来源堆叠数时视为来源被完全消费、其字段不清零（不允许堆叠数为 0）而由调用方丢弃，否则来源保留剩余量可再次调用。</summary>
        public bool TryStackItem(RuntimeItem target, RuntimeItem incoming, out int movedCount, out string error)
        {
            movedCount = 0;

            if (target == null || incoming == null) return Reject(out error, nameof(TryStackItem), "物品为空。");
            if (ReferenceEquals(target, incoming)) return Reject(out error, nameof(TryStackItem), "目标与来源是同一个物品实例。");
            if (!placements.ContainsKey(target)) return Reject(out error, nameof(TryStackItem), "目标物品不在容器中。");
            if (!CanInsert(incoming, nameof(TryStackItem), out error)) return false;
            if (!target.IsStackable || !incoming.IsStackable) return Reject(out error, nameof(TryStackItem), "双方必须都可堆叠才能合并。");
            if (!target.ItemType.MatchesTagExact(incoming.ItemType)) return Reject(out error, nameof(TryStackItem), "物品类型不一致（需整串精确匹配）。");

            int room = target.MaxStackSize - target.StackCount;
            if (room <= 0) return Reject(out error, nameof(TryStackItem), "目标堆叠已满。");

            movedCount = Math.Min(room, incoming.StackCount);

            // 提交：数值已校验，不会触及 SetStackCount 的取值范围约束。
            target.SetStackCount(target.StackCount + movedCount);
            if (movedCount < incoming.StackCount)
            {
                incoming.SetStackCount(incoming.StackCount - movedCount);
            }

            BumpRevision();
            error = null;
            NotifyStackChanged(target);
            return true;
        }

        /// <summary>修改容器内物品的堆叠数量：校验归属与 1..单堆上限后直改，数量未变化时不提交；成功提交推进版本号并广播堆叠变化，失败零变更。堆叠不缓存在容器，唯一来源仍是 RuntimeItem.StackCount。</summary>
        public bool TrySetStackCount(RuntimeItem item, int stackCount, out string error)
        {
            if (item == null) return Reject(out error, nameof(TrySetStackCount), "物品为空。");
            if (!placements.ContainsKey(item)) return Reject(out error, nameof(TrySetStackCount), "物品不在容器中。");

            if (stackCount < 1 || stackCount > item.MaxStackSize)
            {
                return Reject(out error, nameof(TrySetStackCount), $"堆叠数量 {stackCount} 必须在 1..{item.MaxStackSize} 范围内。");
            }

            if (item.StackCount == stackCount)
            {
                error = null;
                return true;
            }

            item.SetStackCount(stackCount);
            BumpRevision();
            error = null;
            NotifyStackChanged(item);
            return true;
        }

        /// <summary>同一容器内合并或交换堆叠数；双方静默修改，确认成功后才推进版本和广播。</summary>
        internal bool TryChangeStackPair(RuntimeItem source, RuntimeItem target, int count, bool swap,
            CommitConfirmHandler confirm, out int movedCount, out string error)
        {
            movedCount = 0;
            const string operation = "堆叠操作";
            if (source == null || target == null || ReferenceEquals(source, target)
                || !placements.TryGetValue(source, out InventoryPlacement sourcePlacement) || !Contains(target))
                return Reject(out error, operation, "来源与目标必须是库存内的不同物品。");
            if (!source.IsStackable || !target.IsStackable || !source.ItemType.MatchesTagExact(target.ItemType))
                return Reject(out error, operation, "双方必须是同类型的可堆叠物品。");

            int sourceCount = source.StackCount;
            int targetCount = target.StackCount;
            if (swap)
            {
                if (sourceCount > target.MaxStackSize || targetCount > source.MaxStackSize)
                    return Reject(out error, operation, "交换后的数量超过单堆上限。");
                if (sourceCount == targetCount)
                {
                    error = null;
                    return true;
                }
                source.SetStackCount(targetCount);
                target.SetStackCount(sourceCount);
            }
            else
            {
                if (count < 1 || count > sourceCount)
                    return Reject(out error, operation, "合并数量必须大于零且不超过来源数量。");
                movedCount = Math.Min(count, target.MaxStackSize - targetCount);
                if (movedCount == 0) return Reject(out error, operation, "目标堆叠已满。");
                target.SetStackCount(targetCount + movedCount);
                if (movedCount == sourceCount)
                {
                    ClearCells(sourcePlacement);
                    placements.Remove(source);
                    itemIndicesDirty = true;
                }
                else source.SetStackCount(sourceCount - movedCount);
            }

            error = null;
            bool confirmed = confirm == null || confirm(target.ItemId, out error);
            if (confirmed && Contains(source) && confirm != null) confirmed = confirm(source.ItemId, out error);
            if (!confirmed)
            {
                source.SetStackCount(sourceCount);
                target.SetStackCount(targetCount);
                if (!Contains(source)) CommitPlacement(source, sourcePlacement.UpperLeftIndex, sourcePlacement.Dimensions);
                itemIndicesDirty = true;
                confirm?.Invoke(ItemId.None, out _);
                movedCount = 0;
                return false;
            }

            BumpRevision();
            NotifyStackChanged(target);
            if (Contains(source)) NotifyStackChanged(source);
            else NotifyItemRemoved(source);
            return true;
        }

        /// <summary>兼容原有添加入口，接收规则统一委托 TryReceiveItem；仍为整批成功或失败，remainder 保留旧契约恒为 0。</summary>
        public bool TryAddItem(RuntimeItem item, out int remainder, out string error)
        {
            remainder = 0;
            return TryReceiveItem(item, out error);
        }

        /// <summary>整批接收外部物品，可供拾取使用：先按行优先合并已有同类型堆叠，再将剩余量放入首个合法空白区域；空间不足时容器与物品完全不变。调用方须持有尚未入包且尚未被消费的实例，包内物品请用 TryTransferItem；成功后交出实例并移除对应拾取物，即使全部并入既有堆叠、此实例没有成为容器条目也不能再次接收（StackCount 不清零）。</summary>
        public bool TryReceiveItem(RuntimeItem item, out string error)
        {
            if (!TryPlanReceiveItem(item, nameof(TryReceiveItem), out ReceivePlan plan, out error)) return false;

            CommitReceiveItem(item, plan);
            BumpRevision();
            NotifyReceivedItem(item, plan);
            return true;
        }

        /// <summary>将容器内物品整件或整堆转移到 destination，目标使用与拾取相同的接收规则；先完整规划再提交双方归属、占格与数量，失败双方零变更。保留未被完全合并的原实例与标识，不拆分、不重新随机化；双方版本与查询缓存均更新后才广播通知。</summary>
        public bool TryTransferItem(RuntimeItem item, InventoryContainer destination, out string error)
            => TryTransferItemCore(item, destination, true, nameof(TryTransferItem), out error);

        /// <summary>将来源物品整件或整堆转入目标首个合法空白矩形，不合并任何已有堆叠；保留原实例、标识与数量。UI 展示落点不作为权威容器坐标，失败双方零变更，成功在双方版本与缓存更新后广播通知。</summary>
        public bool TryTransferItemToEmptySpace(RuntimeItem item, InventoryContainer destination, out string error)
            => TryTransferItemCore(item, destination, false, nameof(TryTransferItemToEmptySpace), out error);

        internal bool TryTransferItemToEmptySpace(RuntimeItem item, InventoryContainer destination,
            CommitConfirmHandler confirm, CommitConfirmHandler destinationConfirm, out string error)
            => TryTransferItemCore(item, destination, false, nameof(TryTransferItemToEmptySpace), out error, confirm, destinationConfirm);

        /// <summary>从来源堆叠扣除 splitItem 的数量，并将调用方恢复出的新实例放入目标首个空白矩形；支持同容器，不合堆。新实例必须具有未在双方出现的有效标识，失败时数量、占格与版本均不变。</summary>
        public bool TrySplitStackToEmptySpace(RuntimeItem source, RuntimeItem splitItem, InventoryContainer destination, out string error)
            => TrySplitStackToEmptySpace(source, splitItem, destination, null, null, out error);

        /// <summary>宿主拆分使用的确认入口：在广播前确认双方投影，确认失败静默还原并补偿投影；确认钩子只负责投影，不得重入修改容器，同容器只执行来源确认。</summary>
        internal bool TrySplitStackToEmptySpace(RuntimeItem source, RuntimeItem splitItem, InventoryContainer destination, CommitConfirmHandler confirm, CommitConfirmHandler destinationConfirm, out string error)
        {
            const string operation = nameof(TrySplitStackToEmptySpace);
            if (destination == null) return Reject(out error, operation, "目标容器为空。");
            if (source == null || !placements.ContainsKey(source)) return Reject(out error, operation, "物品不在来源容器中。");
            if (!source.IsStackable) return Reject(out error, operation, "来源物品不可堆叠。");
            if (!CanInsert(splitItem, operation, out error)) return false;
            if (!splitItem.ItemId.IsValid) return Reject(out error, operation, "拆分实例缺少有效标识。");
            if (!splitItem.IsStackable || !source.ItemType.MatchesTagExact(splitItem.ItemType))
                return Reject(out error, operation, "拆分实例与来源堆叠类型不一致。");
            if (splitItem.StackCount < 1 || splitItem.StackCount >= source.StackCount)
                return Reject(out error, operation, "拆分数量必须大于零且小于来源堆叠数。");
            if (!destination.TryPlanReceiveItem(splitItem, operation, out ReceivePlan plan, out error, false)) return false;

            bool sameContainer = ReferenceEquals(this, destination);
            int previousStack = source.StackCount;
            source.SetStackCount(previousStack - splitItem.StackCount);
            destination.CommitReceiveItem(splitItem, plan);
            itemIndicesDirty = true;
            destination.itemIndicesDirty = true;

            ItemId requiredItem = sameContainer ? splitItem.ItemId : source.ItemId;
            bool confirmed = confirm == null || confirm(requiredItem, out error);
            if (confirmed && !sameContainer && destinationConfirm != null)
                confirmed = destinationConfirm(splitItem.ItemId, out error);
            if (!confirmed)
            {
                destination.ClearCells(destination.placements[splitItem]);
                destination.placements.Remove(splitItem);
                source.SetStackCount(previousStack);
                itemIndicesDirty = true;
                destination.itemIndicesDirty = true;
                confirm?.Invoke(ItemId.None, out _);
                if (!sameContainer) destinationConfirm?.Invoke(ItemId.None, out _);
                return false;
            }

            AdvanceRevision();
            if (!sameContainer) destination.AdvanceRevision();
            NotifyChanged();
            if (!sameContainer) destination.NotifyChanged();
            NotifyStackChanged(source);
            destination.NotifyReceivedItem(splitItem, plan);
            error = null;
            return true;
        }

        private bool TryTransferItemCore(RuntimeItem item, InventoryContainer destination, bool mergeStacks, string operation,
            out string error, CommitConfirmHandler confirm = null, CommitConfirmHandler destinationConfirm = null)
        {
            if (destination == null) return Reject(out error, operation, "目标容器为空。");
            if (ReferenceEquals(this, destination)) return Reject(out error, operation, "来源与目标是同一个容器。");
            if (item == null) return Reject(out error, operation, "物品为空。");
            if (!placements.TryGetValue(item, out InventoryPlacement placement))
            {
                return Reject(out error, operation, "物品不在来源容器中。");
            }

            if (!destination.TryPlanReceiveItem(item, operation, out ReceivePlan plan, out error, mergeStacks)) return false;

            // 双方静默提交：此段无回调，不能通过两个公开增删操作拼接转移。
            ClearCells(placement);
            placements.Remove(item);
            int previousStack = item.StackCount;
            destination.CommitReceiveItem(item, plan);
            itemIndicesDirty = true;
            destination.itemIndicesDirty = true;

            bool confirmed = confirm == null || confirm(ItemId.None, out error);
            if (confirmed && destinationConfirm != null)
                confirmed = destinationConfirm(plan.PlaceIndex != NoIndex ? item.ItemId : ItemId.None, out error);
            if (!confirmed)
            {
                if (plan.PlaceIndex != NoIndex)
                {
                    destination.ClearCells(destination.placements[item]);
                    destination.placements.Remove(item);
                }
                for (int i = 0; i < plan.StackSteps.Length; i++)
                {
                    StackStep step = plan.StackSteps[i];
                    step.Target.SetStackCount(step.Target.StackCount - step.Amount);
                }
                item.SetStackCount(previousStack);
                CommitPlacement(item, placement.UpperLeftIndex, placement.Dimensions);
                itemIndicesDirty = true;
                destination.itemIndicesDirty = true;
                confirm?.Invoke(ItemId.None, out _);
                destinationConfirm?.Invoke(ItemId.None, out _);
                return false;
            }

            AdvanceRevision();
            destination.AdvanceRevision();

            NotifyChanged();
            destination.NotifyChanged();
            NotifyItemRemoved(item);
            destination.NotifyReceivedItem(item, plan);
            return true;
        }

        // 内部实现

        /// <summary>只读规划接收：默认完整优先扫描已有堆叠，再寻找剩余量的空白矩形；空位转移关闭合堆，直接为整件寻找空白矩形。不能用会在早期空格处提前结束的容量查询代替。</summary>
        private bool TryPlanReceiveItem(RuntimeItem item, string operation, out ReceivePlan plan, out string error, bool mergeStacks = true)
        {
            plan = default;
            if (!CanInsert(item, operation, out error)) return false;

            GridCoord dimensions = GetItemDimensions(item);
            if (dimensions.X < 1 || dimensions.Y < 1)
            {
                return Reject(out error, operation, $"物品占格尺寸 {dimensions} 非法。");
            }

            stackSteps.Clear();
            int remaining = item.StackCount;

            if (mergeStacks && item.IsStackable)
            {
                for (int index = 0; index < cells.Length && remaining > 0; index++)
                {
                    InventoryPlacement placement = cells[index];
                    if (!placement.IsValid || placement.UpperLeftIndex != index) continue;

                    RuntimeItem occupant = placement.Item;
                    if (!occupant.IsStackable || !occupant.ItemType.MatchesTagExact(item.ItemType)) continue;

                    int move = Math.Min(remaining, occupant.MaxStackSize - occupant.StackCount);
                    if (move <= 0) continue;

                    stackSteps.Add(new StackStep(occupant, move));
                    remaining -= move;
                }
            }

            int placeIndex = NoIndex;
            if (remaining > 0)
            {
                if (remaining > item.MaxStackSize)
                {
                    return Reject(out error, operation, $"剩余量 {remaining} 超过单堆上限 {item.MaxStackSize}，需先拆分。");
                }

                for (int index = 0; index < cells.Length; index++)
                {
                    if (!GridUtils.IsInGridBounds(index, dimensions, Columns, Rows)
                        || !IsRegionEmpty(index, dimensions)) continue;

                    placeIndex = index;
                    break;
                }

                if (placeIndex == NoIndex)
                {
                    return Reject(out error, operation, "容器空间不足，无法接收整批物品。");
                }
            }

            plan = new ReceivePlan(dimensions, remaining, placeIndex, stackSteps.ToArray());
            error = null;
            return true;
        }

        /// <summary>应用已校验的接收计划，不推进版本或通知；转移用此入口确保双方状态先全部落盘。</summary>
        private void CommitReceiveItem(RuntimeItem item, ReceivePlan plan)
        {
            for (int i = 0; i < plan.StackSteps.Length; i++)
            {
                StackStep step = plan.StackSteps[i];
                step.Target.SetStackCount(step.Target.StackCount + step.Amount);
            }

            if (plan.PlaceIndex != NoIndex)
            {
                item.SetStackCount(plan.Remaining);
                CommitPlacement(item, plan.PlaceIndex, plan.Dimensions);
            }
        }

        /// <summary>按独立计划广播接收明细；Changed 回调即使重入接收，也不会覆盖当前操作的通知列表。</summary>
        private void NotifyReceivedItem(RuntimeItem item, ReceivePlan plan)
        {
            if (plan.PlaceIndex != NoIndex) NotifyItemAdded(item);

            for (int i = 0; i < plan.StackSteps.Length; i++)
            {
                NotifyStackChanged(plan.StackSteps[i].Target);
            }
        }

        /// <summary>仅从权威物品集合重建排序缓存；缓存只供读取，不参与放置、移除或堆叠决策。</summary>
        private void EnsureItemIndices()
        {
            if (!itemIndicesDirty) return;

            indexedItems.Clear();
            indexedItems.AddRange(placements.Keys);
            indexedItems.Sort(ItemIdComparison);
            itemIndicesDirty = false;
        }

        /// <summary>插入前置校验：非空、不在容器内且有效标识不与已有实例重复；None 保留离线无标识口径。</summary>
        private bool CanInsert(RuntimeItem item, string operation, out string error)
        {
            if (item == null) return Reject(out error, operation, "物品为空。");
            if (placements.ContainsKey(item)) return Reject(out error, operation, "物品已在容器中。");
            if (item.ItemId.IsValid && TryGetItemById(item.ItemId, out _))
            {
                return Reject(out error, operation, $"容器中已有标识为 {item.ItemId} 的另一物品实例。");
            }

            error = null;
            return true;
        }

        /// <summary>区域是否全空。调用方需先做越界守卫。</summary>
        private bool IsRegionEmpty(int startIndex, GridCoord dimensions)
        {
            RegionCursor cursor = new RegionCursor(Columns, startIndex, dimensions);
            while (cursor.MoveNext(out int index))
            {
                if (cells[index].IsValid) return false;
            }

            return true;
        }

        /// <summary>提交一条放置记录：索引与占用矩阵的唯一写入口（保证两份状态同源）。</summary>
        private void CommitPlacement(RuntimeItem item, int startIndex, GridCoord dimensions)
        {
            InventoryPlacement placement = new InventoryPlacement(item, startIndex, dimensions);
            placements.Add(item, placement);
            FillCells(startIndex, dimensions, placement);
        }

        /// <summary>把放置记录占用的格子清空（用记录内冻结的尺寸，不重新向物品取尺寸）。</summary>
        private void ClearCells(InventoryPlacement placement)
        {
            FillCells(placement.UpperLeftIndex, placement.Dimensions, InventoryPlacement.Invalid);
        }

        /// <summary>把区域内所有格子统一写成 value：放置铺满与释放清空共用的唯一矩阵写入点。</summary>
        private void FillCells(int startIndex, GridCoord dimensions, InventoryPlacement value)
        {
            RegionCursor cursor = new RegionCursor(Columns, startIndex, dimensions);
            while (cursor.MoveNext(out int index))
            {
                cells[index] = value;
            }
        }

        /// <summary>区域游标：以「起点索引 + 尺寸」按行优先推进一块矩形区域，栈上结构、零堆分配；尺寸非正时遍历为空，越界守卫由调用方负责。</summary>
        private ref struct RegionCursor
        {
            private readonly int columns;
            private readonly GridCoord dimensions;
            private readonly int originX;
            private readonly int originY;
            private int row;
            private int column;

            public RegionCursor(int columns, int startIndex, GridCoord dimensions)
            {
                this.columns = columns;
                this.dimensions = dimensions;
                originX = startIndex % columns;
                originY = startIndex / columns;
                row = 0;
                column = -1;
            }

            /// <summary>推进到下一格并输出其线性索引；遍历结束时返回 false。</summary>
            public bool MoveNext(out int index)
            {
                index = InventoryContainer.NoIndex;
                if (dimensions.X < 1 || row >= dimensions.Y) return false;

                if (++column >= dimensions.X)
                {
                    column = 0;
                    row++;
                }

                if (row >= dimensions.Y) return false;

                index = (originY + row) * columns + originX + column;
                return true;
            }
        }

        /// <summary>写入并返回拒绝原因（只用于低频的变更类操作，查询不写日志）。</summary>
        private static bool Reject(out string error, string operation, string reason)
        {
            string message = $"{operation} 被拒绝：{reason}";
            BackpackLog.Warn(LogModule.Inventory, message);
            error = message;
            return false;
        }

        // F6 广播（M2 偏差：蓝本 M2 不接事件总线，本工程在变更成功后广播，source 用容器实例）

        /// <summary>推进版本号并广播粗粒度「数据已变」信号，供单容器操作使用。</summary>
        private void BumpRevision()
        {
            AdvanceRevision();
            NotifyChanged();
        }

        /// <summary>版本与下标缓存的唯一更新入口；跨容器转移先更新双方，再发送任何同步通知。</summary>
        private void AdvanceRevision()
        {
            itemIndicesDirty = true;
            Revision++;
        }

        /// <summary>广播粗粒度变更信号（无载荷）：表现层据此置脏、帧末合并刷新一次，不区分具体变了什么。</summary>
        private void NotifyChanged()
        {
            EventBus.Global.Publish(InvChannels.Changed, this);
        }

        /// <summary>广播「物品进入容器」：放置成功或被收编进容器时。</summary>
        private void NotifyItemAdded(RuntimeItem item)
        {
            EventBus.Global.Publish(InvChannels.ItemAdded, this, item);
        }

        /// <summary>广播「物品离开容器」：移除成功时。</summary>
        private void NotifyItemRemoved(RuntimeItem item)
        {
            EventBus.Global.Publish(InvChannels.ItemRemoved, this, item);
        }

        /// <summary>广播「堆叠数量变化」：载荷为变更后的容量查询结果（只读扫描，仅变更路径触发）。</summary>
        private void NotifyStackChanged(RuntimeItem item)
        {
            EventBus.Global.Publish(InvChannels.StackChanged, this, QueryAvailability(item));
        }

        /// <summary>一笔接收操作的独立计划；步骤数组不与后续或重入操作共用。</summary>
        private readonly struct ReceivePlan
        {
            public ReceivePlan(GridCoord dimensions, int remaining, int placeIndex, StackStep[] stackSteps)
            {
                Dimensions = dimensions;
                Remaining = remaining;
                PlaceIndex = placeIndex;
                StackSteps = stackSteps;
            }

            public GridCoord Dimensions { get; }
            public int Remaining { get; }
            public int PlaceIndex { get; }
            public StackStep[] StackSteps { get; }
        }

        /// <summary>一条待提交的堆叠合并步骤（规划阶段产出、提交阶段执行）。</summary>
        private readonly struct StackStep
        {
            /// <summary>以目标堆叠与并入量构造。</summary>
            public StackStep(RuntimeItem target, int amount)
            {
                Target = target;
                Amount = amount;
            }

            public RuntimeItem Target { get; } // 目标堆叠。

            public int Amount { get; } // 并入量。
        }
    }
}
