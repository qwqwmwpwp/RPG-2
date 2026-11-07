using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>只读规划；计划中的集合属于本次操作，不与后续操作共用。</summary>
    internal static class InventoryOperationPlanner
    {
        internal static bool TryReceive(InventoryContainer container, RuntimeItem item, string operation,
            out ReceivePlan plan, out string error, bool mergeStacks = true,
            int upperLeftIndex = InventoryContainer.NoIndex, GridCoord? frozenDimensions = null)
        {
            plan = default;
            if (!container.CanInsert(item, operation, out error)) return false;
            GridCoord dimensions = frozenDimensions ?? container.ResolveItemDimensions(item);
            if (upperLeftIndex < InventoryContainer.NoIndex || dimensions.X < 1 || dimensions.Y < 1)
            { error = "目标索引或物品尺寸非法。"; return false; }
            var steps = new List<StackStep>();
            int remaining = item.StackCount;
            if (mergeStacks && !container.SingleCellItems && item.IsStackable)
                for (int index = 0; index < container.SlotCount && remaining > 0; index++)
                {
                    if (container.GetUpperLeftIndexAt(index) != index) continue;
                    RuntimeItem occupant = container.GetItemAt(index);
                    if (occupant == null || container.IsReserved(occupant.ItemId)
                        || !TryStackAmount(occupant, item, remaining, out int amount)) continue;
                    steps.Add(new StackStep(occupant, amount)); remaining -= amount;
                }
            int placeIndex = InventoryContainer.NoIndex;
            if (remaining > 0)
            {
                if (remaining > item.MaxStackSize) { error = "剩余数量超过单堆上限，需先拆分。"; return false; }
                if (upperLeftIndex != InventoryContainer.NoIndex)
                {
                    if (container.IsRegionAvailable(upperLeftIndex, dimensions, null, null)) placeIndex = upperLeftIndex;
                }
                else placeIndex = FindSwapRegion(container, 0, dimensions, null);
                if (placeIndex == InventoryContainer.NoIndex) { error = "容器空间不足，无法接收整批物品。"; return false; }
            }
            plan = new ReceivePlan(dimensions, remaining, placeIndex, steps.ToArray());
            error = null; return true;
        }

        internal static bool TryStackAmount(RuntimeItem target, RuntimeItem source, int count, out int amount)
        {
            amount = 0;
            if (!target.IsStackable || !source.IsStackable || !target.ItemType.MatchesTagExact(source.ItemType)
                || count < 1 || count > source.StackCount) return false;
            amount = Math.Min(count, target.MaxStackSize - target.StackCount);
            return amount > 0;
        }

        internal static int FindSwapRegion(InventoryContainer container, int preferred, GridCoord dimensions, RuntimeItem outgoing)
        {
            if (container.IsRegionAvailable(preferred, dimensions, outgoing, null)) return preferred;
            for (int index = 0; index < container.SlotCount; index++)
                if (container.IsRegionAvailable(index, dimensions, outgoing, null)) return index;
            return InventoryContainer.NoIndex;
        }

        internal static bool TrySwap(InventoryContainer sourceContainer, InventoryContainer destination,
            RuntimeItem source, RuntimeItem target, InventoryPlacement first, InventoryPlacement second,
            out int sourceIndex, out GridCoord sourceSize, out int targetIndex, out GridCoord targetSize, out string error)
        {
            bool same = ReferenceEquals(sourceContainer, destination);
            bool changesLayout = !same && sourceContainer.SingleCellItems != destination.SingleCellItems;
            sourceSize = changesLayout ? destination.ResolveItemDimensions(source) : first.Dimensions;
            targetSize = changesLayout ? sourceContainer.ResolveItemDimensions(target) : second.Dimensions;
            sourceIndex = second.UpperLeftIndex; targetIndex = first.UpperLeftIndex;
            if (changesLayout && !sourceContainer.SingleCellItems) targetIndex = FindSwapRegion(sourceContainer, targetIndex, targetSize, source);
            if (changesLayout && !destination.SingleCellItems) sourceIndex = FindSwapRegion(destination, sourceIndex, sourceSize, target);
            if (!same && ((source.ItemId.IsValid && destination.TryGetItemById(source.ItemId, out _))
                || (target.ItemId.IsValid && sourceContainer.TryGetItemById(target.ItemId, out _))))
            { error = "目标容器已有相同物品标识。"; return false; }
            if (!destination.IsRegionAvailable(sourceIndex, sourceSize, target, same ? source : null)
                || !sourceContainer.IsRegionAvailable(targetIndex, targetSize, source, same ? target : null)
                || (same && RegionsOverlap(sourceContainer.Columns, sourceIndex, sourceSize, targetIndex, targetSize)))
            { error = "交换后的矩形越界、重叠或被占用。"; return false; }
            error = null; return true;
        }

        private static bool RegionsOverlap(int columns, int firstIndex, GridCoord firstSize, int secondIndex, GridCoord secondSize)
        {
            int firstX = firstIndex % columns, firstY = firstIndex / columns;
            int secondX = secondIndex % columns, secondY = secondIndex / columns;
            return firstX < secondX + secondSize.X && secondX < firstX + firstSize.X
                && firstY < secondY + secondSize.Y && secondY < firstY + firstSize.Y;
        }
    }

    internal readonly struct ReceivePlan
    {
        internal ReceivePlan(GridCoord dimensions, int remaining, int placeIndex, StackStep[] stackSteps)
        { Dimensions = dimensions; Remaining = remaining; PlaceIndex = placeIndex; StackSteps = stackSteps; }
        internal GridCoord Dimensions { get; }
        internal int Remaining { get; }
        internal int PlaceIndex { get; }
        internal StackStep[] StackSteps { get; }
    }
    internal readonly struct StackStep
    {
        internal StackStep(RuntimeItem target, int amount) { Target = target; Amount = amount; }
        internal RuntimeItem Target { get; }
        internal int Amount { get; }
    }
}
