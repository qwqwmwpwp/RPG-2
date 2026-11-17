using RPG4.Backpack.Items;

namespace RPG4.LoadoutContracts
{
    public readonly struct ConsumableUseOperation
    {
        public ConsumableUseOperation(int requestId, ItemId itemId) { RequestId = requestId; ItemId = itemId; }
        public int RequestId { get; }
        public ItemId ItemId { get; }
    }

    public readonly struct ConsumableUseResult
    {
        public ConsumableUseResult(int requestId, bool success, ItemId itemId, ulong actionId, string error)
        { RequestId = requestId; Success = success; ItemId = itemId; ActionId = actionId; Error = error; }
        public int RequestId { get; }
        public bool Success { get; }
        public ItemId ItemId { get; }
        public ulong ActionId { get; }
        public string Error { get; }
    }

    public readonly struct ConsumableUseViewState
    {
        public ConsumableUseViewState(ulong actionId, ItemId itemId, bool active, float duration, float elapsed)
        { ActionId = actionId; ItemId = itemId; Active = active; Duration = duration; Elapsed = elapsed; }
        public ulong ActionId { get; }
        public ItemId ItemId { get; }
        public bool Active { get; }
        public float Duration { get; }
        public float Elapsed { get; }
    }

}
