using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

[assembly: InternalsVisibleTo("RPG4.Backpack.Network")]

namespace RPG4.Backpack.Inventory
{
    public delegate bool ItemAdmissionHandler(RuntimeItem item, out string error);

    /// <summary>库存真相源：统一承担物品集合、格子映射与读写查询，唯一权威地持有「物品集合 + 上左位置 + 格子占用」，布局由唯一写入口同源更新，准备阶段保护写入，失败按事务记录恢复；物品下标按 ItemId 排序并按需缓存，不另建权威数据；堆叠数不缓存（唯一来源 RuntimeItem.StackCount）；纯 C# 数据层，不引用 UI / MonoBehaviour / 网络；按主线程单线程访问、不加锁，查询复用轮次戳缓冲做到零 GC；变更成功后经 F6 事件总线广播（source 为容器实例）。</summary>
    public sealed partial class InventoryContainer
    {
        public const int NoIndex = InventoryPlacement.NoIndex; // 无效索引常量。

        public const int MaxSlotCount = 65536; // 槽位总数上限（256 × 256），用于拒绝非正尺寸、整数溢出与不可接受的分配规模。

        private static readonly Comparison<RuntimeItem> ItemIdComparison = static (left, right) => left.ItemId.CompareTo(right.ItemId); // 确定性排序比较器，静态缓存避免每次调用分配委托。

        private readonly InventoryPlacement[] cells; // 逐格放置记录，空置为 Invalid。
        private readonly Dictionary<RuntimeItem, InventoryPlacement> placements; // 物品 → 放置记录，供 O(1) 定位与枚举。
        private readonly IReadOnlyCollection<RuntimeItem> itemsView; // placements 键的只读视图，避免每次枚举重新分配。
        private readonly List<RuntimeItem> indexedItems = new List<RuntimeItem>(); // 按 ItemId 排序的查询缓存，下标口径与 FillItemsByItemId 一致。
        private readonly int[] claimed; // 查询标记缓冲：值等于 claimStamp 才算「本轮已检查」。

        private readonly HashSet<ItemId> pendingViewChanges = new HashSet<ItemId>();

        private int claimStamp; // 查询轮次戳，每轮自增；初值 0 与缓冲初值相同，故必须先 BeginClaimPass。
        private readonly ItemAdmissionHandler admission;
        public bool SingleCellItems { get; }
        private bool itemIndicesDirty = true; // 提交变更后失效，首次下标查询时重建。

        /// <summary>创建指定行列数的容器；行列非正或 rows × columns 超过 MaxSlotCount 时抛 ArgumentOutOfRangeException（用长整型探测溢出）。</summary>
        public InventoryContainer(int rows, int columns, bool singleCellItems = false, ItemAdmissionHandler admission = null)
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
            SingleCellItems = singleCellItems;
            this.admission = admission;
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

        // 仅用于派生缓存失效：静默提交和回滚都可能触发，订阅者不得在这里执行业务。
        internal event Action<RuntimeItem, bool> ItemTouched;

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

        public GridCoord ResolveItemDimensions(RuntimeItem item) => SingleCellItems ? GridCoord.One : GetItemDimensions(item);

        public bool CanAcceptItem(RuntimeItem item, out string error)
        {
            error = null;
            if (item == null) { error = "物品为空。"; return false; }
            if (SingleCellItems && item.StackCount != 1) { error = "装备格只能容纳一件物品。"; return false; }
            return admission == null || admission(item, out error);
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
                : QuerySpace(startIndex, ResolveItemDimensions(item));

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

            if (!CanAcceptItem(item, out _)) return;
            GridCoord dimensions = ResolveItemDimensions(item);
            int amountToFill = amountOverride >= 0 ? amountOverride : item.StackCount;
            if (dimensions.X < 1 || dimensions.Y < 1 || amountToFill <= 0) return;

            bool isStackable = !SingleCellItems && item.IsStackable;
            int incomingMaxStack = SingleCellItems ? 1 : item.MaxStackSize;

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
                && !IsReserved(occupant.ItemId)
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

        // 原子变更：规划不写状态，所有公开写入口共用一次事务。

        public bool TryPlaceItem(RuntimeItem item, int startIndex, out string error)
            => InventoryTransactions.Execute(this, null, (InventoryTransactions tx, out string failure) =>
            {
                if (item != null && item.RootOwner != null) return Reject(out failure, nameof(TryPlaceItem), "物品已有根容器，请使用转移事务。");
                if (!CanInsert(item, nameof(TryPlaceItem), out failure)) return false;
                GridCoord size = ResolveItemDimensions(item);
                if (!IsRegionAvailable(startIndex, size, null, null)) return Reject(out failure, nameof(TryPlaceItem), "目标矩形越界或已被占用。");
                tx.Track(item);
                CommitPlacement(item, startIndex, size);
                if (!tx.Prepare(CommitParticipant, item.ItemId, out failure)) return false;
                return tx.Commit(() => { NotifyChanged(); NotifyItemAdded(item); }, out failure);
            }, out error);

        public bool TryMoveItem(RuntimeItem item, int upperLeftIndex, out string error)
            => TryMoveItem(item, upperLeftIndex, CommitParticipant, out error);

        internal bool TryMoveItem(RuntimeItem item, int upperLeftIndex, IInventoryCommitParticipant confirm, out string error)
            => InventoryTransactions.Execute(this, null, (InventoryTransactions tx, out string failure) =>
            {
                if (item == null || !placements.TryGetValue(item, out InventoryPlacement previous))
                    return Reject(out failure, nameof(TryMoveItem), "物品不在容器中。");
                if (!CanMutate(item, out failure) || !CanAcceptItem(item, out failure)) return false;
                if (!IsRegionAvailable(upperLeftIndex, previous.Dimensions, item, null))
                    return Reject(out failure, nameof(TryMoveItem), "目标矩形越界或被其他物品占用。");
                if (previous.UpperLeftIndex == upperLeftIndex) { failure = null; return true; }
                tx.Track(item);
                ClearCells(previous); RemovePlacement(item);
                CommitPlacement(item, upperLeftIndex, previous.Dimensions);
                if (!tx.Prepare(confirm, item.ItemId, out failure)) return false;
                return tx.Commit(NotifyChanged, out failure);
            }, out error);

        public bool TrySwapItems(RuntimeItem source, RuntimeItem target, InventoryContainer destination, out string error)
            => TrySwapItems(source, target, destination, CommitParticipant, destination?.CommitParticipant, out error);

        internal bool TrySwapItems(RuntimeItem source, RuntimeItem target, InventoryContainer destination,
            IInventoryCommitParticipant confirm, IInventoryCommitParticipant destinationConfirm, out string error)
        {
            if (destination == null) return Reject(out error, nameof(TrySwapItems), "目标容器为空。");
            return InventoryTransactions.Execute(this, destination, (InventoryTransactions tx, out string failure) =>
            {
                if (source == null || target == null || ReferenceEquals(source, target)
                    || !placements.TryGetValue(source, out InventoryPlacement first)
                    || !destination.placements.TryGetValue(target, out InventoryPlacement second))
                    return Reject(out failure, nameof(TrySwapItems), "来源与目标必须是各自库存内的不同物品。");
                bool same = ReferenceEquals(this, destination);
                if (!CanMutate(source, out failure) || !destination.CanMutate(target, out failure)) return false;
                if (!destination.CanAcceptItem(source, out failure) || !CanAcceptItem(target, out failure)) return false;
                if (!same && (!destination.CanInsertGraph(source, target, out failure) || !CanInsertGraph(target, source, out failure))) return false;
                if (!InventoryOperationPlanner.TrySwap(this, destination, source, target, first, second,
                    out int sourceIndex, out GridCoord sourceSize, out int targetIndex, out GridCoord targetSize, out failure)) return false;
                tx.Track(source); tx.Track(target);
                ClearCells(first); destination.ClearCells(second);
                RemovePlacement(source); destination.RemovePlacement(target);
                CommitPlacement(target, targetIndex, targetSize);
                destination.CommitPlacement(source, sourceIndex, sourceSize);
                if (!tx.Prepare(confirm, target.ItemId, out failure)
                    || !tx.Prepare(same ? confirm : destinationConfirm, source.ItemId, out failure)) return false;
                return tx.Commit(() =>
                {
                    NotifyChanged();
                    if (!same)
                    {
                        destination.NotifyChanged(); NotifyItemRemoved(source); destination.NotifyItemRemoved(target);
                        NotifyItemAdded(target); destination.NotifyItemAdded(source);
                    }
                }, out failure);
            }, out error);
        }

        private int FindSwapRegion(int preferredIndex, GridCoord dimensions, RuntimeItem outgoing)
            => InventoryOperationPlanner.FindSwapRegion(this, preferredIndex, dimensions, outgoing);

        internal bool IsRegionAvailable(int startIndex, GridCoord dimensions, RuntimeItem ignored, RuntimeItem secondIgnored)
        {
            if (dimensions.X < 1 || dimensions.Y < 1 || !GridUtils.IsInGridBounds(startIndex, dimensions, Columns, Rows)) return false;
            RegionCursor cursor = new RegionCursor(Columns, startIndex, dimensions);
            while (cursor.MoveNext(out int index))
            {
                RuntimeItem occupant = cells[index].Item;
                if (occupant != null && !ReferenceEquals(occupant, ignored) && !ReferenceEquals(occupant, secondIgnored)) return false;
            }
            return true;
        }

        public bool TryRemoveItem(RuntimeItem item, out string error) => TryRemoveItem(item, CommitParticipant, out error);
        internal bool TryRemoveItem(RuntimeItem item, IInventoryCommitParticipant confirm, out string error)
            => InventoryTransactions.Execute(this, null, (InventoryTransactions tx, out string failure) =>
            {
                if (item == null || !placements.TryGetValue(item, out InventoryPlacement placement))
                    return Reject(out failure, nameof(TryRemoveItem), "物品不在容器中。");
                if (!CanMutate(item, out failure)) return false;
                tx.Track(item); ClearCells(placement); RemovePlacement(item);
                if (!tx.Prepare(confirm, ItemId.None, out failure)) return false;
                return tx.Commit(() => { NotifyChanged(); NotifyItemRemoved(item); }, out failure);
            }, out error);

        public bool TryStackItem(RuntimeItem target, RuntimeItem incoming, out int movedCount, out string error)
        {
            int moved = 0;
            bool accepted = InventoryTransactions.Execute(this, null, (InventoryTransactions tx, out string failure) =>
            {
                if (incoming != null && incoming.RootOwner != null) return Reject(out failure, nameof(TryStackItem), "来源物品已有根容器。");
                if (target == null || incoming == null || ReferenceEquals(target, incoming) || !Contains(target))
                    return Reject(out failure, nameof(TryStackItem), "目标与来源必须是不同实例，且目标须在容器中。");
                if (!CanInsert(incoming, nameof(TryStackItem), out failure)
                    || !CanMutate(target, out failure) || !CanAcceptItem(target, out failure)) return false;
                if (SingleCellItems || !InventoryOperationPlanner.TryStackAmount(target, incoming, incoming.StackCount, out moved))
                    return Reject(out failure, nameof(TryStackItem), "双方不能合堆或目标堆叠已满。");
                tx.Track(target); tx.Track(incoming);
                SetItemStack(target, target.StackCount + moved);
                if (moved < incoming.StackCount) SetItemStack(incoming, incoming.StackCount - moved);
                if (!tx.Prepare(CommitParticipant, target.ItemId, out failure)) return false;
                return tx.Commit(() => { NotifyChanged(); NotifyStackChanged(target); }, out failure);
            }, out error);
            movedCount = accepted ? moved : 0;
            return accepted;
        }

        public bool TrySetStackCount(RuntimeItem item, int stackCount, out string error)
            => TrySetStackCount(item, stackCount, CommitParticipant, out error);
        internal bool TrySetStackCount(RuntimeItem item, int stackCount, IInventoryCommitParticipant confirm, out string error)
            => InventoryTransactions.Execute(this, null, (InventoryTransactions tx, out string failure) =>
            {
                if (item == null || !Contains(item)) return Reject(out failure, nameof(TrySetStackCount), "物品不在容器中。");
                if (!CanMutate(item, out failure) || !CanAcceptItem(item, out failure)) return false;
                if ((SingleCellItems && stackCount != 1) || stackCount < 1 || stackCount > item.MaxStackSize)
                    return Reject(out failure, nameof(TrySetStackCount), "堆叠数量超出容器或物品允许范围。");
                if (item.StackCount == stackCount) { failure = null; return true; }
                tx.Track(item); SetItemStack(item, stackCount);
                if (!tx.Prepare(confirm, item.ItemId, out failure)) return false;
                return tx.Commit(() => { NotifyChanged(); NotifyStackChanged(item); }, out failure);
            }, out error);

        internal bool TryChangeStackPair(RuntimeItem source, RuntimeItem target, int count, bool swap,
            IInventoryCommitParticipant confirm, out int movedCount, out string error,
            InventoryContainer destination = null, IInventoryCommitParticipant destinationConfirm = null)
        {
            destination = destination ?? this;
            InventoryContainer receiver = destination;
            int moved = 0;
            bool accepted = InventoryTransactions.Execute(this, receiver, (InventoryTransactions tx, out string failure) =>
            {
                bool same = ReferenceEquals(this, receiver);
                if (source == null || target == null || ReferenceEquals(source, target) || !Contains(source) || !receiver.Contains(target))
                    return Reject(out failure, "堆叠操作", "来源与目标必须是库存内的不同物品。");
                if (!CanMutate(source, out failure) || !receiver.CanMutate(target, out failure)
                    || !CanAcceptItem(source, out failure) || !receiver.CanAcceptItem(target, out failure)) return false;
                if (SingleCellItems || receiver.SingleCellItems || !source.IsStackable || !target.IsStackable
                    || !source.ItemType.MatchesTagExact(target.ItemType))
                    return Reject(out failure, "堆叠操作", "双方必须是同类型可堆叠物品，且不能位于装备格。");
                int sourceCount = source.StackCount, targetCount = target.StackCount;
                if (swap)
                {
                    if (sourceCount > target.MaxStackSize || targetCount > source.MaxStackSize)
                        return Reject(out failure, "堆叠操作", "交换后的数量超过单堆上限。");
                    if (sourceCount == targetCount) { failure = null; return true; }
                }
                else if (!InventoryOperationPlanner.TryStackAmount(target, source, count, out moved))
                    return Reject(out failure, "堆叠操作", "合并数量非法或目标堆叠已满。");
                tx.Track(source); tx.Track(target);
                bool sourceRemoved = !swap && moved == sourceCount;
                if (swap) { SetItemStack(source, targetCount); receiver.SetItemStack(target, sourceCount); }
                else
                {
                    receiver.SetItemStack(target, targetCount + moved);
                    if (sourceRemoved) { ClearCells(placements[source]); RemovePlacement(source); }
                    else SetItemStack(source, sourceCount - moved);
                }
                if (!tx.Prepare(confirm, same ? target.ItemId : sourceRemoved ? ItemId.None : source.ItemId, out failure)) return false;
                if (same && !sourceRemoved && !tx.Prepare(confirm, source.ItemId, out failure)) return false;
                if (!same && !tx.Prepare(destinationConfirm, target.ItemId, out failure)) return false;
                return tx.Commit(() =>
                {
                    NotifyChanged(); if (!same) receiver.NotifyChanged(); receiver.NotifyStackChanged(target);
                    if (sourceRemoved) NotifyItemRemoved(source); else NotifyStackChanged(source);
                }, out failure);
            }, out error);
            movedCount = accepted ? moved : 0;
            return accepted;
        }

        public bool TryAddItem(RuntimeItem item, out int remainder, out string error)
        { remainder = 0; return TryReceiveItem(item, out error); }

        /// <summary>整批接收；全部合堆时交出原实例，成功后不得重复接收。</summary>
        public bool TryReceiveItem(RuntimeItem item, out string error) => TryReceiveItem(item, CommitParticipant, out error);
        internal bool TryReceiveItem(RuntimeItem item, IInventoryCommitParticipant confirm, out string error)
            => InventoryTransactions.Execute(this, null, (InventoryTransactions tx, out string failure) =>
            {
                if (item != null && item.RootOwner != null) return Reject(out failure, nameof(TryReceiveItem), "物品已有根容器，请使用转移事务。");
                if (!TryPlanReceiveItem(item, nameof(TryReceiveItem), out ReceivePlan plan, out failure)) return false;
                TrackReceive(tx, item, plan); CommitReceiveItem(item, plan);
                if (!tx.Prepare(confirm, plan.PlaceIndex != NoIndex ? item.ItemId : ItemId.None, out failure)) return false;
                return tx.Commit(() => { NotifyChanged(); NotifyReceivedItem(item, plan); }, out failure,
                    () => { if (plan.PlaceIndex == NoIndex) item.RootOwner = ConsumedItemOwner; });
            }, out error);

        public bool TryTransferItem(RuntimeItem item, InventoryContainer destination, out string error)
            => TryTransferItemCore(item, destination, true, nameof(TryTransferItem), out error, CommitParticipant, destination?.CommitParticipant);
        public bool TryTransferItemToEmptySpace(RuntimeItem item, InventoryContainer destination, out string error)
            => TryTransferItemCore(item, destination, false, nameof(TryTransferItemToEmptySpace), out error, CommitParticipant, destination?.CommitParticipant);
        internal bool TryTransferItemToEmptySpace(RuntimeItem item, InventoryContainer destination,
            IInventoryCommitParticipant confirm, IInventoryCommitParticipant destinationConfirm, out string error)
            => TryTransferItemCore(item, destination, false, nameof(TryTransferItemToEmptySpace), out error, confirm, destinationConfirm);
        internal bool TryTransferItem(RuntimeItem item, InventoryContainer destination, int upperLeftIndex,
            IInventoryCommitParticipant confirm, IInventoryCommitParticipant destinationConfirm, out string error)
            => TryTransferItemCore(item, destination, upperLeftIndex == NoIndex, nameof(TryTransferItem), out error, confirm, destinationConfirm, upperLeftIndex);

        internal bool TrySplitStackToEmptySpace(RuntimeItem source, RuntimeItem splitItem, InventoryContainer destination,
            IInventoryCommitParticipant confirm, IInventoryCommitParticipant destinationConfirm, out string error, int upperLeftIndex = NoIndex)
        {
            if (destination == null) return Reject(out error, nameof(TrySplitStackToEmptySpace), "目标容器为空。");
            return InventoryTransactions.Execute(this, destination, (InventoryTransactions tx, out string failure) =>
            {
                const string operation = nameof(TrySplitStackToEmptySpace);
                if (source == null || !Contains(source) || !source.IsStackable) return Reject(out failure, operation, "来源必须是库存内可堆叠物品。");
                if (!CanMutate(source, out failure) || !CanInsert(splitItem, operation, out failure) || !CanAcceptItem(source, out failure)) return false;
                if (!splitItem.ItemId.IsValid || !splitItem.IsStackable || !source.ItemType.MatchesTagExact(splitItem.ItemType)
                    || splitItem.StackCount < 1 || splitItem.StackCount >= source.StackCount)
                    return Reject(out failure, operation, "拆分实例标识、类型或数量非法。");
                GridCoord splitSize = SingleCellItems != destination.SingleCellItems ? destination.ResolveItemDimensions(splitItem) : placements[source].Dimensions;
                if (!destination.TryPlanReceiveItem(splitItem, operation, out ReceivePlan plan, out failure, false, upperLeftIndex, splitSize)) return false;
                bool same = ReferenceEquals(this, destination);
                tx.Track(source); TrackReceive(tx, splitItem, plan);
                SetItemStack(source, source.StackCount - splitItem.StackCount); destination.CommitReceiveItem(splitItem, plan);
                if (!tx.Prepare(confirm, same ? splitItem.ItemId : source.ItemId, out failure)
                    || (!same && !tx.Prepare(destinationConfirm, splitItem.ItemId, out failure))) return false;
                return tx.Commit(() =>
                {
                    NotifyChanged(); if (!same) destination.NotifyChanged(); NotifyStackChanged(source); destination.NotifyReceivedItem(splitItem, plan);
                }, out failure);
            }, out error);
        }

        private bool TryTransferItemCore(RuntimeItem item, InventoryContainer destination, bool mergeStacks, string operation,
            out string error, IInventoryCommitParticipant confirm = null, IInventoryCommitParticipant destinationConfirm = null, int upperLeftIndex = NoIndex)
        {
            if (destination == null || ReferenceEquals(this, destination)) return Reject(out error, operation, "来源与目标必须是不同的有效容器。");
            return InventoryTransactions.Execute(this, destination, (InventoryTransactions tx, out string failure) =>
            {
                if (item == null || !placements.TryGetValue(item, out InventoryPlacement placement)) return Reject(out failure, operation, "物品不在来源容器中。");
                if (!CanMutate(item, out failure)) return false;
                GridCoord size = SingleCellItems != destination.SingleCellItems ? destination.ResolveItemDimensions(item) : placement.Dimensions;
                if (!destination.TryPlanReceiveItem(item, operation, out ReceivePlan plan, out failure,
                    mergeStacks && !SingleCellItems && !destination.SingleCellItems, upperLeftIndex, size)) return false;
                TrackReceive(tx, item, plan); ClearCells(placement); RemovePlacement(item); destination.CommitReceiveItem(item, plan);
                if (!tx.Prepare(confirm, ItemId.None, out failure)
                    || !tx.Prepare(destinationConfirm, plan.PlaceIndex != NoIndex ? item.ItemId : ItemId.None, out failure)) return false;
                return tx.Commit(() =>
                {
                    NotifyChanged(); destination.NotifyChanged(); NotifyItemRemoved(item); destination.NotifyReceivedItem(item, plan);
                }, out failure, () => { if (plan.PlaceIndex == NoIndex) item.RootOwner = ConsumedItemOwner; });
            }, out error);
        }

        private static void TrackReceive(InventoryTransactions transaction, RuntimeItem item, ReceivePlan plan)
        { transaction.Track(item); foreach (StackStep step in plan.StackSteps) transaction.Track(step.Target); }

        // 内部实现

        /// <summary>只读规划接收：默认完整优先扫描已有堆叠，再寻找剩余量的空白矩形；空位转移关闭合堆，直接为整件寻找空白矩形。不能用会在早期空格处提前结束的容量查询代替。</summary>
        private bool TryPlanReceiveItem(RuntimeItem item, string operation, out ReceivePlan plan, out string error,
            bool mergeStacks = true, int upperLeftIndex = NoIndex, GridCoord? frozenDimensions = null)
            => InventoryOperationPlanner.TryReceive(this, item, operation, out plan, out error, mergeStacks, upperLeftIndex, frozenDimensions);

        /// <summary>应用已校验的接收计划，只使派生缓存失效，不推进业务版本或广播；转移用此入口确保双方状态先全部落盘。</summary>
        private void CommitReceiveItem(RuntimeItem item, ReceivePlan plan)
        {
            for (int i = 0; i < plan.StackSteps.Length; i++)
            {
                StackStep step = plan.StackSteps[i];
                SetItemStack(step.Target, step.Target.StackCount + step.Amount);
            }

            if (plan.PlaceIndex != NoIndex)
            {
                SetItemStack(item, plan.Remaining);
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
        internal bool CanInsert(RuntimeItem item, string operation, out string error)
        {
            if (item == null) return Reject(out error, operation, "物品为空。");
            if (!CanMutate(item, out error)) return false;
            if (item.ParentItem != null) return Reject(out error, operation, "已安装子物品不可作为根条目插入。");
            if (!CanAcceptItem(item, out error)) return false;
            if (placements.ContainsKey(item)) return Reject(out error, operation, "物品已在容器中。");
            if (!CanInsertGraph(item, null, out error)) return false;
            if (item.ItemId.IsValid && TryGetItemById(item.ItemId, out _))
            {
                return Reject(out error, operation, $"容器中已有标识为 {item.ItemId} 的另一物品实例。");
            }

            error = null;
            return true;
        }

        /// <summary>提交一条放置记录：索引与占用矩阵的唯一写入口（保证两份状态同源）。</summary>
        private void CommitPlacement(RuntimeItem item, int startIndex, GridCoord dimensions)
        {
            InventoryPlacement placement = new InventoryPlacement(item, startIndex, dimensions);
            placements.Add(item, placement);
            itemIndicesDirty = true;
            item.RootOwner = this;
            item.Changed += OnItemStateChanged;
            FillCells(startIndex, dimensions, placement);
            TouchItem(item, false);
        }

        private void RemovePlacement(RuntimeItem item)
        {
            if (!placements.Remove(item)) return;
            itemIndicesDirty = true;
            item.Changed -= OnItemStateChanged;
            item.RootOwner = null;
            TouchItem(item, false);
        }

        private void OnItemStateChanged(RuntimeItem item)
        {
            if (!Contains(item)) return;
            TouchItem(item, true);
            if (!committedItemNotifications.Remove(item)) BumpRevision();
        }

        private void TouchItem(RuntimeItem item, bool payloadChanged)
        {
            pendingViewChanges.Add(item.ItemId);
            ItemTouched?.Invoke(item, payloadChanged);
        }

        // 同程序集事务回滚也使用此入口，保持缓存失效但不追加业务通知。
        internal void SetItemStack(RuntimeItem item, int count)
        {
            if (item.StackCount == count) return;
            item.SetStackCount(count);
            if (Contains(item)) TouchItem(item, true);
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
            EventBus bus = EventBus.Global;
            bool detailed = bus.SubscriptionCount(InvChannels.ItemsChanged, this) > 0
                || bus.SubscriptionCount(InvChannels.ItemsChanged, EventBus.AnySource) > 0;
            ItemId[] changed = null;
            if (detailed && pendingViewChanges.Count > 0 && !pendingViewChanges.Contains(ItemId.None))
            {
                changed = new ItemId[pendingViewChanges.Count];
                pendingViewChanges.CopyTo(changed);
            }
            // 先清空再回调：重入的下一次事务拥有独立的变更集合。
            pendingViewChanges.Clear();
            if (detailed) bus.Publish<ItemId[]>(InvChannels.ItemsChanged, this, changed);
            bus.Publish(InvChannels.Changed, this);
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

    }
}
