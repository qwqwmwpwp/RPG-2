using System;
using Mirror;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Interaction;
using UnityEngine;

namespace RPG4.Backpack.Network
{
    public sealed partial class BackpackNetworkInventory
    {
        public Func<ConsumableUseOperation, ConsumableUseResult> ServerConsumableUse { get; set; }
        public Func<ConsumableUseViewState> ConsumableStateProvider { get; set; }
        private int pickupRequest;
        private void RequestConsumableOperation(ConsumableUseOperation operation)
        {
            if (!isClient || !isOwned || !IsReady || !operation.ItemId.IsValid)
            { core?.PublishConsumableOperationResult(new ConsumableUseResult(operation.RequestId, false, operation.ItemId, 0, "当前客户端不能使用消耗品。")); return; }
            CmdUseConsumable(NextClientTicket(), operation.RequestId, operation.ItemId.Value);
        }
        private void DispatchAuthorityConsumableOperation(ConsumableUseOperation operation)
        {
            if (isClient) { RequestConsumableOperation(operation); return; }
            ScheduleResultOperation(() => ExecuteConsumable(operation), core.PublishConsumableOperationResult,
                reason => new ConsumableUseResult(operation.RequestId, false, operation.ItemId, 0, reason));
        }
        private ConsumableUseResult ExecuteConsumable(ConsumableUseOperation operation)
            => ServerConsumableUse?.Invoke(operation) ?? new ConsumableUseResult(operation.RequestId, false, operation.ItemId, 0, "角色动作尚未就绪。");
        [Command(requiresAuthority = true)]
        private void CmdUseConsumable(ulong ticket, int requestId, ulong itemId)
        {
            NetworkConnectionToClient conn = connectionToClient;
            if (!BeginTicket(ticket, requestId, RequestCategory.Consumable,
                () => TargetConsumableCompleted(conn, requestId, itemId, false, 0, "请求已过期或队列已满。"))) return;
            void Complete(ConsumableUseResult result)
            {
                if (connectionToClient != conn || operationOwner != conn) return;
                CompleteTicket(ticket, () => TargetConsumableCompleted(conn, result.RequestId, result.ItemId.Value, result.Success, result.ActionId, result.Error));
            }
            var operation = new ConsumableUseOperation(requestId, new ItemId(itemId));
            ScheduleResultOperation(() => IsAuthenticatedIngress(conn) ? ExecuteConsumable(operation)
                : new ConsumableUseResult(requestId, false, operation.ItemId, 0, "角色已不能使用物品。"), Complete,
                reason => new ConsumableUseResult(requestId, false, operation.ItemId, 0, reason));
        }
        [TargetRpc]
        private void TargetConsumableCompleted(NetworkConnectionToClient target, int requestId, ulong itemId, bool success, ulong actionId, string error)
            => core?.PublishConsumableOperationResult(new ConsumableUseResult(requestId, success, new ItemId(itemId), actionId, error));

        public void RequestPickup(NetworkWorldPickup pickup)
        {
            if (pickup == null || !isClient || !isOwned || !IsReady || pickupRequest == int.MaxValue) return;
            CmdPickup(NextClientTicket(), ++pickupRequest, pickup.netId);
        }
        [Command(requiresAuthority = true)]
        private void CmdPickup(ulong ticket, int requestId, uint targetId)
        {
            NetworkConnectionToClient conn = connectionToClient;
            if (!BeginTicket(ticket, requestId, RequestCategory.Pickup,
                () => TargetPickupCompleted(conn, false, "拾取请求已过期或队列已满。"))) return;
            void Complete(bool success, string error)
            {
                if (connectionToClient != conn || operationOwner != conn) return;
                CompleteTicket(ticket, () => TargetPickupCompleted(conn, success, error));
            }
            ScheduleOperation(() =>
            {
                if (!IsAuthenticatedIngress(conn) || !NetworkServer.spawned.TryGetValue(targetId, out NetworkIdentity identity)
                    || identity == null || !identity.TryGetComponent(out NetworkWorldPickup pickup))
                { Complete(false, "角色或世界物品已不可交互。"); return; }
                bool accepted = pickup.TryServerReceive(this, out string error);
                Complete(accepted, accepted ? "已拾取物品。" : error);
            }, reason => Complete(false, reason));
        }
        [TargetRpc]
        private void TargetPickupCompleted(NetworkConnectionToClient target, bool success, string message)
            => GetComponent<PlayerInteraction>()?.ShowInteractionFeedback(message, success);
    }
}
