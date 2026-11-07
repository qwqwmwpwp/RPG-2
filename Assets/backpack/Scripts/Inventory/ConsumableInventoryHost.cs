using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    public sealed partial class InventoryHostCore
    {
        public Action<ConsumableUseOperation> ForwardConsumableOperation { get; set; }
        public Action<ConsumableUseOperation> AuthorityConsumableOperationDispatcher { get; set; }
        public Func<ConsumableUseViewState> ConsumableStateProvider { get; set; }

        public ConsumableUseViewState GetConsumableUseState() => ConsumableStateProvider?.Invoke() ?? default;

        public static bool TryGetCommandHost(object sourceKey, out InventoryHostCore host)
        {
            host = null;
            return sourceKey != null && CommandHosts.TryGetValue(sourceKey, out host);
        }

        private void OnConsumableOperationCommand(ConsumableUseOperation operation)
        {
            if (ResolveAuthority() && AuthorityConsumableOperationDispatcher != null)
                AuthorityConsumableOperationDispatcher(operation);
            else if (!ResolveAuthority() && ForwardConsumableOperation != null)
                ForwardConsumableOperation(operation);
            else PublishConsumableOperationResult(new ConsumableUseResult(operation.RequestId, false,
                operation.ItemId, 0, "当前宿主不支持使用消耗品。"));
        }

        public void PublishConsumableOperationResult(ConsumableUseResult result)
        {
            if (commandSource != null)
                EventBus.Global.Publish(InvChannels.ConsumableOperationCompleted, commandSource, result);
        }

        /// <summary>接收已经生成的世界实例；不重新分配标识或随机化。返回 true 即完整接收，全部合堆同样成功。</summary>
        public bool TryReceiveExistingItem(RuntimeItem item, out string error)
        {
            if (!ResolveAuthority() || !EnsureContainer(out error))
            { error = "仅可写权威库存可以接收世界物品。"; return false; }
            if (item == null || !item.ItemId.IsValid)
            { error = "世界物品实例或标识无效。"; return false; }
            return Container.TryReceiveItem(item, CommitParticipant, out error);
        }

        public bool TryReserveConsumable(ItemId itemId, ulong actionId, out RuntimeItem item, out string error)
        {
            item = null;
            if (!ResolveAuthority() || !EnsureContainer(out error))
            { error = "仅可写权威库存可以占用消耗品。"; return false; }
            return Container.TryReserveConsumable(itemId, actionId, out item, out error);
        }

        public bool ReleaseConsumableReservation(ItemId itemId, ulong actionId)
            => ResolveAuthority() && Container != null && Container.ReleaseConsumableReservation(itemId, actionId);

        public bool TryConsumeReserved(ItemId itemId, ulong actionId, IReservedConsumeEffect effect, out string error)
        {
            if (!ResolveAuthority() || !EnsureContainer(out error))
            { error = "仅可写权威库存可以提交消耗。"; return false; }
            return Container.TryConsumeReserved(itemId, actionId, effect, CommitParticipant, out error);
        }
    }
}
