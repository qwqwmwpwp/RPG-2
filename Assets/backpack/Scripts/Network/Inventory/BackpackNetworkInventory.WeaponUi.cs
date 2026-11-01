using System.Collections.Generic;
using Mirror;
using UnityEngine;
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
        private BackpackNetworkEquipment networkWeaponEquipment;
        private WeaponUiActionStage networkWeaponActionStage;
        private double networkWeaponActionStart;
        private float networkWeaponActionDuration;
        private bool networkWeaponReloading;
        private ItemId networkWeaponActionItemId;

        /// <summary>联网角色的只读动作快照入口；不推进换匣/上膛，不修改物品状态。</summary>
        public void UpdateWeaponAction(bool reloading, float phase, double serverTime, float duration)
        {
            phase = Mathf.Clamp01(phase);
            WeaponDefinitionFragment definition = NetworkEquippedItem?.GetFragment<WeaponDefinitionFragment>();
            WeaponUiActionStage stage = reloading
                ? definition != null && phase >= definition.ChamberFraction ? WeaponUiActionStage.Chambered
                    : definition != null && phase >= definition.ReloadCommitFraction ? WeaponUiActionStage.MagazineCommitted : WeaponUiActionStage.Started
                : networkWeaponReloading ? phase >= 1f ? WeaponUiActionStage.Completed : WeaponUiActionStage.Cancelled : networkWeaponActionStage;
            double start = reloading ? serverTime - phase * duration : networkWeaponActionStart;
            if (stage != networkWeaponActionStage || reloading != networkWeaponReloading || System.Math.Abs(start - networkWeaponActionStart) > 0.05d)
                networkWeaponUiDirty = true;
            networkWeaponReloading = reloading; networkWeaponActionStage = stage;
            networkWeaponActionStart = start; networkWeaponActionDuration = Mathf.Max(0f, duration);
        }

        private RuntimeItem NetworkEquippedItem => networkWeaponEquipment != null && networkWeaponEquipment.IsReady ? networkWeaponEquipment.EquippedItem : null;

        private bool TryResolveNetworkWeaponItem(ItemId id, out RuntimeItem item)
        {
            if (TryGetItem(id, out item)) return true;
            item = NetworkEquippedItem;
            if (item != null && item.ItemId == id) return true;
            item = null;
            return false;
        }

        private void RequestWeaponOperation(WeaponOperation operation)
        {
            if (!isClient || !isOwned || !IsReady || !operation.TargetItemId.IsValid)
            {
                core?.PublishWeaponOperationResult(new WeaponOperationResult(operation.RequestId, operation.TargetItemId,
                    false, "当前客户端不能提交此武器操作。"));
                return;
            }
            CmdExecuteWeaponOperation(NextClientTicket(), operation.RequestId, (int)operation.Kind, operation.TargetItemId.Value,
                operation.CandidateItemId.Value, operation.Count, (int)operation.Slot);
        }

        // requiresAuthority 验证所属连接；核心仅解析此网络背包的根物品，不接受客户端提供的容器。
        [Command(requiresAuthority = true)]
        private void CmdExecuteWeaponOperation(ulong ticket, int requestId, int kind, ulong targetId, ulong candidateId, int count, int slot)
        {
            NetworkConnectionToClient conn = connectionToClient;
            if (!BeginTicket(ticket, requestId, true, () => TargetWeaponOperationCompleted(conn, requestId, targetId, false, "请求已过期或队列已满。"))) return;
            void Complete(WeaponOperationResult result)
            {
                if (connectionToClient != conn || operationOwner != conn) return;
                CompleteTicket(ticket, () => TargetWeaponOperationCompleted(conn, result.RequestId, result.TargetItemId.Value, result.Success, result.Error));
            }
            ScheduleOperation(() =>
            {
                if (!IsAuthenticatedIngress(conn))
                { Complete(new WeaponOperationResult(requestId, new ItemId(targetId), false, "角色已不能操作库存。")); return; }
                var operation = new WeaponOperation(requestId, (WeaponOperationKind)kind,
                    new ItemId(targetId), new ItemId(candidateId), count, (WeaponAttachmentSlot)slot);
                ServerBeforeWeaponOperation?.Invoke(operation);
                Complete(core.ExecuteWeaponOperation(operation));
            }, reason => Complete(new WeaponOperationResult(requestId, new ItemId(targetId), false, reason)));
        }

        [TargetRpc]
        private void TargetWeaponOperationCompleted(NetworkConnectionToClient target, int requestId, ulong targetItemId, bool success, string error)
            => core?.PublishWeaponOperationResult(new WeaponOperationResult(requestId, new ItemId(targetItemId), success, error));

        private void BindNetworkWeaponUi()
        {
            UnbindNetworkWeaponUi();
            networkWeaponUiSource = EventSource;
            networkWeaponEquipment = GetComponent<BackpackNetworkEquipment>();
            if (networkWeaponEquipment != null) networkWeaponEquipment.Changed += MarkNetworkWeaponUiDirty;
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
            if (!TryResolveNetworkWeaponItem(operation.TargetItemId, out RuntimeItem item)) return "目标物品已离开此背包。";
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
            TryResolveNetworkWeaponItem(request.InspectedItemId, out RuntimeItem item);
            if (!IsReady || (item != null && item.WeaponState == null && item.MagazineState == null)) item = null;
            string reason = !IsReady ? "网络库存尚未同步就绪。" : !isOwned && isClient
                ? "此网络背包不可由当前玩家操作。" : item == null && request.InspectedItemId.IsValid ? "所选物品已离开此背包。" : null;
            EventBus.Global.Publish(InvChannels.WeaponInspectionChanged, networkWeaponUiSource,
                WeaponUiSnapshots.Inspection(item, networkWeaponUiItems, catalog, NetworkEquippedItem?.ItemId ?? ItemId.None, request, NetworkWeaponRejection, reason));
        }

        private void PublishNetworkWeaponHud()
        {
            RuntimeItem item = NetworkEquippedItem;
            WeaponItemState state = item?.WeaponState;
            MagazineItemState magazine = state?.Magazine?.MagazineState;
            int spareMagazines = 0, looseAmmo = 0;
            if (state != null)
            {
                CopyItems(networkWeaponUiItems);
                foreach (RuntimeItem candidate in networkWeaponUiItems)
                {
                    if (candidate.MagazineState?.RoundCount > 0 && state.IsCompatibleMagazine(candidate, catalog)) spareMagazines++;
                    AmmoDefinitionFragment ammo = candidate.GetFragment<AmmoDefinitionFragment>();
                    if (ammo != null && candidate.WeaponState == null && candidate.MagazineState == null
                        && ammo.AmmoType.MatchesTagExact(state.Definition.AmmoType)) looseAmmo += candidate.StackCount;
                }
            }
            string status = state == null ? null : state.Durability <= 0f ? "武器已损坏"
                : state.Durability < state.Definition.DurabilityPerShot ? "耐久不足，无法开火"
                : state.ChamberAmmo.IsEmpty ? state.Magazine?.MagazineState.RoundCount > 0 ? "未上膛"
                    : state.Magazine == null ? "未安装弹匣" : "弹药耗尽"
                : state.CanConsumeShot(catalog) ? null : "当前弹药不可用";
            EventBus.Global.Publish(InvChannels.WeaponHudChanged, networkWeaponUiSource,
                new WeaponHudSnapshot(WeaponUiSnapshots.Describe(item, catalog),
                    state == null || state.ChamberAmmo.IsEmpty ? 0 : 1, magazine?.RoundCount ?? 0, magazine?.Capacity ?? 0,
                    state?.Durability ?? 0f, state?.Definition.MaxDurability ?? 0f,
                    item == null ? WeaponUiActionStage.None : networkWeaponActionStage,
                    networkWeaponActionStart, networkWeaponActionDuration, spareMagazines, looseAmmo,
                    state == null ? null : state.Definition.FireMode == WeaponFireMode.Automatic ? "全自动" : "半自动", status));
        }

        private void MarkNetworkWeaponUiDirty()
        {
            ItemId current = NetworkEquippedItem?.ItemId ?? ItemId.None;
            if (current != networkWeaponActionItemId)
            {
                networkWeaponActionItemId = current;
                networkWeaponActionStage = WeaponUiActionStage.None;
                networkWeaponActionStart = 0d; networkWeaponActionDuration = 0f; networkWeaponReloading = false;
            }
            networkWeaponUiDirty = true;
        }
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
            if (networkWeaponEquipment != null) networkWeaponEquipment.Changed -= MarkNetworkWeaponUiDirty;
            networkWeaponEquipment = null;
            networkWeaponActionStage = WeaponUiActionStage.None;
            networkWeaponReloading = false;
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
