using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>库存真相源：单一矩形网格容器，唯一权威地持有「物品集合 + 上左位置 + 格子占用」，两份状态只在提交阶段同源写入、不存在中间态；堆叠数不缓存（唯一来源 RuntimeItem.StackCount）；纯 C# 数据层，不引用 UI / MonoBehaviour / 网络；按主线程单线程访问、不加锁，查询复用轮次戳缓冲做到零 GC；变更成功后经 F6 事件总线广播（source 为容器实例）。</summary>
    public sealed class InventoryContainer
    {
        public const int NoIndex = InventoryPlacement.NoIndex; // 无效索引常量。

        public const int MaxSlotCount = 65536; // 槽位总数上限（256 × 256），用于拒绝非正尺寸、整数溢出与不可接受的分配规模。

        private static readonly Comparison<RuntimeItem> ItemIdComparison = static (left, right) => left.ItemId.CompareTo(right.ItemId); // 确定性排序比较器，静态缓存避免每次调用分配委托。

        private readonly InventoryPlacement[] cells; // 逐格放置记录，空置为 Invalid。
        private readonly Dictionary<RuntimeItem, InventoryPlacement> placements; // 物品 → 放置记录，供 O(1) 定位与枚举。
        private readonly IReadOnlyCollection<RuntimeItem> itemsView; // placements 键的只读视图，避免每次枚举重新分配。
        private readonly int[] claimed; // 查询标记缓冲：值等于 claimStamp 才算「本轮已检查」。
        private readonly SlotAvailabilityResult<RuntimeItem> scratchAvailability = new SlotAvailabilityResult<RuntimeItem>(); // 内部复用的容量查询结果，供 TryAddItem 规划阶段使用。
        private readonly List<StackStep> stackSteps = new List<StackStep>(); // 内部复用的堆叠合并步骤缓冲。

        private int claimStamp; // 查询轮次戳，每轮自增；初值 0 与缓冲初值相同，故必须先 BeginClaimPass。

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

        public int ItemCount => placements.Count; // 当前容器内的物品数量。

        public IReadOnlyCollection<RuntimeItem> Items => itemsView; // 当前容器内的物品集合（枚举顺序不保证，勿依赖）。

        public int Revision { get; private set; } // 状态版本号：每次成功提交变更（放置 / 移除 / 合并 / 收编）后 +1、失败路径不变，供网络投影做廉价的「是否有外部变更」判断（比较相等而非大小，溢出回绕不影响语义）。

        /// <summary>按 RuntimeItem.ItemId 升序把物品写入 destination（先清空），供网络初始快照、日志与测试取确定性顺序；首次代价 O(n log n)，标识唯一故顺序确定，destination 由调用方持有以复用、避免每帧分配。</summary>
        public void FillItemsByItemId(List<RuntimeItem> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            destination.Clear();
            destination.AddRange(placements.Keys);
            destination.Sort(ItemIdComparison);
        }

        // 只读查询

        /// <summary>线性索引是否落在此容器内。</summary>
        public bool IsIndexValid(int index) => index >= 0 && index < cells.Length;

        /// <summary>该格是否被物品占用。索引非法时返回 false。</summary>
        public bool IsIndexOccupied(int index) => IsIndexValid(index) && cells[index].IsValid;

        /// <summary>该格所属物品的上左格索引；空置或索引非法时返回 NoIndex。</summary>
        public int GetUpperLeftIndexAt(int index) =>
            IsIndexValid(index) ? cells[index].UpperLeftIndex : NoIndex;

        /// <summary>该格上的物品；空置或索引非法时返回 null。</summary>
        public RuntimeItem GetItemAt(int index) => IsIndexValid(index) ? cells[index].Item : null;

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
            if (placements.ContainsKey(incoming)) return Reject(out error, nameof(TryStackItem), "来源物品已在容器中，需先取出。");
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

        /// <summary>一次性收编：先按行优先把物品并入容器内已有的同类型堆叠，剩余部分再放入首个合法空白区域；容器不持有物品拷贝工厂，无法把一部分放进容器、另一部分留在手上，因此要求整批数量都能被吸收，否则整体失败且不改变任何状态（需要部分接收时改用 QueryAvailability 的 Remainder 与 TryStackItem 的 movedCount）。</summary>
        public bool TryAddItem(RuntimeItem item, out int remainder, out string error)
        {
            remainder = 0;
            if (!CanInsert(item, nameof(TryAddItem), out error)) return false;

            GridCoord dimensions = GetItemDimensions(item);

            // 只读规划
            FillAvailability(item, -1, scratchAvailability);
            if (!scratchAvailability.HasRoom)
            {
                return Reject(out error, nameof(TryAddItem), "容器没有可用空间。");
            }

            IReadOnlyList<SlotAvailability> slots = scratchAvailability.SlotAvailabilities;
            stackSteps.Clear();
            int remaining = item.StackCount;

            for (int i = 0; i < slots.Count && remaining > 0; i++)
            {
                SlotAvailability slot = slots[i];
                if (!slot.HasItemAtIndex) continue;

                RuntimeItem occupant = GetItemAt(slot.Index);
                if (occupant == null) continue;

                int move = Math.Min(remaining, slot.AmountToFill);
                if (move <= 0) continue;

                stackSteps.Add(new StackStep(occupant, move));
                remaining -= move;
            }

            int placeIndex = NoIndex;
            if (remaining > 0)
            {
                if (remaining > item.MaxStackSize)
                {
                    return Reject(out error, nameof(TryAddItem), $"剩余量 {remaining} 超过单堆上限 {item.MaxStackSize}，需先拆分。");
                }

                for (int i = 0; i < slots.Count; i++)
                {
                    SlotAvailability slot = slots[i];
                    if (slot.HasItemAtIndex || IsIndexOccupied(slot.Index)) continue;

                    placeIndex = slot.Index;
                    break;
                }

                if (placeIndex == NoIndex)
                {
                    return Reject(out error, nameof(TryAddItem), "没有可用的空白区域。");
                }
            }

            if (stackSteps.Count == 0 && placeIndex == NoIndex)
            {
                return Reject(out error, nameof(TryAddItem), "容器没有可用空间。");
            }

            // 提交
            for (int i = 0; i < stackSteps.Count; i++)
            {
                StackStep step = stackSteps[i];
                step.Target.SetStackCount(step.Target.StackCount + step.Amount);
            }

            if (placeIndex != NoIndex)
            {
                item.SetStackCount(remaining);
                CommitPlacement(item, placeIndex, dimensions);
            }

            BumpRevision();
            error = null;

            if (placeIndex != NoIndex) NotifyItemAdded(item);

            for (int i = 0; i < stackSteps.Count; i++)
            {
                NotifyStackChanged(stackSteps[i].Target);
            }

            return true;
        }

        // 内部实现

        /// <summary>插入前置校验：非空且不在容器内。</summary>
        private bool CanInsert(RuntimeItem item, string operation, out string error)
        {
            if (item == null) return Reject(out error, operation, "物品为空。");
            if (placements.ContainsKey(item)) return Reject(out error, operation, "物品已在容器中。");

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

        /// <summary>推进版本号并广播粗粒度「数据已变」信号：容器内所有成功提交路径的唯一自增入口。</summary>
        private void BumpRevision()
        {
            Revision++;
            NotifyChanged();
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
