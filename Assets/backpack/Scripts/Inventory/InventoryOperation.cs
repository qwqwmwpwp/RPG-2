using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    public enum InventoryOperationKind
    {
        MergeStack,
        SwapStackCounts,
        SplitStack,
        TransferItem
    }

    /// <summary>一次完整的库存意图；Destination 留空表示来源库存。</summary>
    public readonly struct InventoryOperation
    {
        public InventoryOperation(int requestId, InventoryOperationKind kind, ItemId sourceItemId,
            ItemId targetItemId = default, int count = 0, IBackpackView destination = null)
        {
            RequestId = requestId;
            Kind = kind;
            SourceItemId = sourceItemId;
            TargetItemId = targetItemId;
            Count = count;
            Destination = destination;
        }

        public int RequestId { get; }
        public InventoryOperationKind Kind { get; }
        public ItemId SourceItemId { get; }
        public ItemId TargetItemId { get; }
        public int Count { get; }
        public IBackpackView Destination { get; }
    }

    public readonly struct InventoryOperationResult
    {
        public InventoryOperationResult(int requestId, bool success, ItemId resultItemId, int movedCount, string error,
            int sourceStackCount = -1, int resultStackCount = -1)
        {
            RequestId = requestId;
            Success = success;
            ResultItemId = resultItemId;
            MovedCount = movedCount;
            Error = error;
            SourceStackCount = sourceStackCount;
            ResultStackCount = resultStackCount;
        }

        public int RequestId { get; }
        public bool Success { get; }
        public ItemId ResultItemId { get; }
        public int MovedCount { get; }
        public string Error { get; }
        public int SourceStackCount { get; }
        public int ResultStackCount { get; }
    }

    /// <summary>供交互层查询写入能力；读取物品仍使用 IBackpackView。</summary>
    public interface IInventoryOperations
    {
        bool CanSubmitTo(IBackpackView destination);
        bool HasEmptySpace(GridCoord dimensions);
    }
}
