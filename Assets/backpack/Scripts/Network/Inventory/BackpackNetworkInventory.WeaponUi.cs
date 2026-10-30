using System.Collections.Generic;
using Mirror;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    public sealed partial class BackpackNetworkInventory
    {
        private EventSubscription networkWeaponSnapshotRequest;
        private EventSubscription networkWeaponInventoryChanged;
        private readonly Dictionary<int, WeaponSnapshotRequest> networkWeaponInspections = new Dictionary<int, WeaponSnapshotRequest>();
        private readonly List<RuntimeItem> networkWeaponUiItems = new List<RuntimeItem>();
        private object networkWeaponUiSource;
        private bool networkWeaponUiDirty;

        private void RequestWeaponOperation(WeaponOperation operation)
        {
            if (!isClient || !isOwned || !IsReady || !operation.TargetItemId.IsValid)
            {
                core?.PublishWeaponOperationResult(new WeaponOperationResult(operation.RequestId, operation.TargetItemId,
                    false, "当前客户端不能提交此武器操作。"));
                return;
            }
            CmdExecuteWeaponOperation(operation.RequestId, (int)operation.Kind, operation.TargetItemId.Value,
                operation.CandidateItemId.Value, operation.Count, (int)operation.Slot);
        }

        // requiresAuthority 验证所属连接；核心仅解析此网络背包的根物品，不接受客户端提供的容器。
        [Command(requiresAuthority = true)]
        private void CmdExecuteWeaponOperation(int requestId, int kind, ulong targetId, ulong candidateId, int count, int slot)
        {
            WeaponOperationResult result = IsServerInventory
                ? core.ExecuteWeaponOperation(new WeaponOperation(requestId, (WeaponOperationKind)kind,
                    new ItemId(targetId), new ItemId(candidateId), count, (WeaponAttachmentSlot)slot))
                : new WeaponOperationResult(requestId, new ItemId(targetId), false, "服务端库存尚未就绪。");
            TargetWeaponOperationCompleted(connectionToClient, result.RequestId, result.TargetItemId.Value, result.Success, result.Error);
        }

        [TargetRpc]
        private void TargetWeaponOperationCompleted(NetworkConnectionToClient target, int requestId, ulong targetItemId, bool success, string error)
            => core?.PublishWeaponOperationResult(new WeaponOperationResult(requestId, new ItemId(targetItemId), success, error));

        private void BindNetworkWeaponUi()
        {
            UnbindNetworkWeaponUi();
            networkWeaponUiSource = EventSource;
            networkWeaponSnapshotRequest = EventBus.Global.Subscribe<WeaponSnapshotRequest>(CmdChannels.WeaponSnapshot,
                networkWeaponUiSource, this, OnNetworkWeaponSnapshotRequest);
            networkWeaponInventoryChanged = EventBus.Global.Subscribe(InvChannels.Changed,
                networkWeaponUiSource, this, MarkNetworkWeaponUiDirty);
            networkWeaponUiDirty = true;
        }

        private void OnNetworkWeaponSnapshotRequest(WeaponSnapshotRequest request)
        {
            if (!request.HudOnly)
            {
                if (request.InspectedItemId.IsNone) networkWeaponInspections.Remove(request.SessionId);
                else networkWeaponInspections[request.SessionId] = request;
                PublishNetworkWeaponInspection(request);
            }
            PublishNetworkWeaponHud();
        }

        private string NetworkWeaponRejection(WeaponOperation operation)
        {
            if (!IsReady || (isClient && !isOwned)) return "此网络背包不可由当前玩家操作。";
            if (IsServerInventory) return core.CanExecuteWeaponOperation(operation, out string error) ? null : error;
            // 镜像用于展示兼容性；真正归属、返回容量和数量仍由服务器原子事务复核。
            if (!TryGetItem(operation.TargetItemId, out RuntimeItem item)) return "目标物品已离开此背包。";
            if (operation.Kind == WeaponOperationKind.LoadMagazine)
                return operation.Count > 0 ? null : "弹匣已满或没有可装填弹药。";
            if (item.WeaponState == null) return "所选物品不是武器。";
            RuntimeItem outgoing = null;
            if (operation.Kind == WeaponOperationKind.ExchangeMagazine) outgoing = item.WeaponState.Magazine;
            else item.WeaponState.Attachments.TryGetValue(operation.Slot, out outgoing);
            if (outgoing == null) return operation.CandidateItemId.IsNone ? "槽位为空。" : null;
            // 已同步的占格足以预估卸下/交换后旧子物品是否有空位；最终仍以权威复核为准。
            GridCoord size = outgoing.Size;
            for (int index = 0; index < Rows * Columns; index++)
            {
                int x = index % Columns, y = index / Columns;
                if (x + size.X > Columns || y + size.Y > Rows) continue;
                bool free = true;
                foreach (RuntimeItem root in networkWeaponUiItems)
                {
                    if (root.ItemId == operation.CandidateItemId || !TryGetPlacement(root.ItemId, out InventoryPlacement placement)) continue;
                    int px = placement.UpperLeftIndex % Columns, py = placement.UpperLeftIndex / Columns;
                    if (x < px + placement.Dimensions.X && x + size.X > px && y < py + placement.Dimensions.Y && y + size.Y > py)
                    { free = false; break; }
                }
                if (free) return null;
            }
            return "背包空间不足，无法接回原子物品。";
        }

        private void PublishNetworkWeaponInspection(WeaponSnapshotRequest request)
        {
            CopyItems(networkWeaponUiItems);
            TryGetItem(request.InspectedItemId, out RuntimeItem item);
            if (!IsReady || (item != null && item.WeaponState == null && item.MagazineState == null)) item = null;
            string reason = !IsReady ? "网络库存尚未同步就绪。" : !isOwned && isClient
                ? "此网络背包不可由当前玩家操作。" : item == null && request.InspectedItemId.IsValid ? "所选物品已离开此背包。" : null;
            EventBus.Global.Publish(InvChannels.WeaponInspectionChanged, networkWeaponUiSource,
                WeaponUiSnapshots.Inspection(item, networkWeaponUiItems, catalog, ItemId.None, request, NetworkWeaponRejection, reason));
        }

        private void PublishNetworkWeaponHud()
        {
            // 此网络宿主目前只有背包投影，没有装备槽/战斗时序的网络投影。
            EventBus.Global.Publish(InvChannels.WeaponHudChanged, networkWeaponUiSource,
                new WeaponHudSnapshot(null, 0, 0, 0, 0f, 0f, WeaponUiActionStage.None, 0d, 0f,
                    statusSummary: "网络装备槽尚未接入武器 HUD。"));
        }

        private void MarkNetworkWeaponUiDirty() => networkWeaponUiDirty = true;
        private void FlushNetworkWeaponUi()
        {
            if (!networkWeaponUiDirty || networkWeaponUiSource == null) return;
            networkWeaponUiDirty = false;
            PublishNetworkWeaponHud();
            foreach (WeaponSnapshotRequest request in new List<WeaponSnapshotRequest>(networkWeaponInspections.Values))
                PublishNetworkWeaponInspection(request);
        }

        private void UnbindNetworkWeaponUi()
        {
            networkWeaponSnapshotRequest.Dispose(); networkWeaponInventoryChanged.Dispose();
            networkWeaponSnapshotRequest = networkWeaponInventoryChanged = default;
            if (networkWeaponUiSource != null)
            {
                PublishNetworkWeaponHud();
                foreach (WeaponSnapshotRequest request in new List<WeaponSnapshotRequest>(networkWeaponInspections.Values))
                    EventBus.Global.Publish(InvChannels.WeaponInspectionChanged, networkWeaponUiSource,
                        WeaponUiSnapshots.Inspection(null, networkWeaponUiItems, catalog, ItemId.None, request, NetworkWeaponRejection));
            }
            networkWeaponUiSource = null;
            networkWeaponInspections.Clear(); networkWeaponUiDirty = false;
        }
    }
}
