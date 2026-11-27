using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;

namespace RPG4.LoadoutTemplate
{
    /// <summary>为本地拥有者维护角色会话的固定端点和当前搜刮会话的动态端点。</summary>
    internal sealed class ClientLoadoutProjection
    {
        private const int SlotCount = 10, StorageCount = 4;
        private readonly ProjectionEndpoint[] role = new ProjectionEndpoint[SlotCount + StorageCount];
        private readonly Dictionary<string, ProjectionEndpoint> loot = new Dictionary<string, ProjectionEndpoint>(StringComparer.Ordinal);
        private readonly List<EventSubscription> roleSubscriptions = new List<EventSubscription>();
        private readonly List<EventSubscription> lootSubscriptions = new List<EventSubscription>();
        private object listener;
        private Action<LoadoutOperationRequest> onOperation;
        internal LoadoutSlotId SelectedWeapon { get; private set; } = LoadoutSlotId.PrimaryWeapon1;

        /// <summary>持有独立的事件地址和该地址最新的只读快照。</summary>
        private sealed class ProjectionEndpoint
        {
            internal readonly object Source = new object();
            internal ContainerSnapshot Snapshot;
        }

        /// <summary>为本次拥有者会话建立十四个稳定地址并注册角色连接入口。</summary>
        internal void BeginRoleSession(object owner, Action<LoadoutOperationRequest> forward,
            Action<LoadoutRoleConnectRequest> connect)
        {
            listener = owner;
            onOperation = forward;
            for (int i = 0; i < role.Length; i++)
                role[i] = Register(roleSubscriptions, "库存尚未同步。");
            roleSubscriptions.Add(EventBus.Global.Subscribe(LoadoutChannels.RoleConnectRequested,
                EventBus.AnySource, owner, connect));
        }

        /// <summary>把快照查询和操作转发绑定到同一地址，订阅归所属会话释放。</summary>
        private ProjectionEndpoint Register(List<EventSubscription> subscriptions, string emptyError)
        {
            var endpoint = new ProjectionEndpoint();
            subscriptions.Add(EventBus.Global.Subscribe<LoadoutSnapshotRequest>(LoadoutChannels.SnapshotRequested,
                endpoint.Source, listener, request => EventBus.Global.Publish(LoadoutChannels.SnapshotReceived,
                    request.ReplySource, new LoadoutSnapshotReply(request.RequestId, endpoint.Snapshot,
                        endpoint.Snapshot == null ? emptyError : null))));
            subscriptions.Add(EventBus.Global.Subscribe(LoadoutChannels.OperationRequested, endpoint.Source, listener, onOperation));
            return endpoint;
        }

        /// <summary>提供角色展示连接需要的槽位及其附属容器地址。</summary>
        internal List<LoadoutRoleBinding> GetRoleBindings()
        {
            var result = new List<LoadoutRoleBinding>(SlotCount);
            for (int i = 0; i < SlotCount; i++)
            {
                ProjectionEndpoint storage = i < StorageCount ? role[SlotCount + i] : null;
                result.Add(new LoadoutRoleBinding((LoadoutSlotId)i, role[i].Source, storage?.Source,
                    role[i].Snapshot, storage?.Snapshot));
            }
            return result;
        }

        /// <summary>按角色槽位读取最近一次应用的投影。</summary>
        internal ContainerSnapshot GetSlot(LoadoutSlotId slot) =>
            (int)slot >= 0 && (int)slot < SlotCount ? role[(int)slot]?.Snapshot : null;

        /// <summary>按事件地址查找操作端点，同时标明该地址是否属于当前搜刮会话。</summary>
        internal ContainerSnapshot FindSnapshot(object address, out bool isLoot)
        {
            foreach (ProjectionEndpoint endpoint in loot.Values)
                if (ReferenceEquals(endpoint.Source, address)) { isLoot = true; return endpoint.Snapshot; }
            isLoot = false;
            foreach (ProjectionEndpoint endpoint in role)
                if (endpoint != null && ReferenceEquals(endpoint.Source, address)) return endpoint.Snapshot;
            return null;
        }

        /// <summary>在原有地址上写完角色快照，再按槽位和容器顺序通知所有端点。</summary>
        internal void ApplyRole(Candidate candidate, IReadOnlyList<RoleSlotConfiguration> configuration)
        {
            SelectedWeapon = candidate.selectedWeapon;
            foreach (RoleSlotConfiguration config in configuration)
            {
                int i = (int)config.slotId;
                ItemDisplayData item = candidate.items[i];
                SlotWire slot = candidate.slots[i];
                role[i].Snapshot = new ContainerSnapshot(slot.id, item?.ContainerType ?? ContainerKind.None, item?.Icon,
                    item == null ? Array.Empty<ItemDisplayData>() : new[] { item }, eventSource: role[i].Source,
                    revision: slot.revision, isSlot: true, rule: config.rule, rejectContainers: false);
                if (i >= StorageCount) continue;
                BackpackContainerData storage = item?.Storage;
                ProjectionEndpoint grid = role[SlotCount + i];
                grid.Snapshot = new ContainerSnapshot(storage?.Id ?? slot.id + "/empty",
                    storage?.Kind ?? config.containerController.ConfiguredKind, storage?.Icon, storage?.Items,
                    storage?.ValidCells, grid.Source, slot.storageRevision, isReady: storage != null);
            }
            foreach (ProjectionEndpoint endpoint in role) EventBus.Global.Publish(LoadoutChannels.Changed, endpoint.Source);
        }

        /// <summary>一次写入所有搜刮分区，并返回可供展示绑定的投影。</summary>
        internal List<LootSectionSnapshot> ApplyLoot(DecodedLootState decoded)
        {
            var sections = new List<LootSectionSnapshot>(decoded.Sections.Count);
            foreach (DecodedLootSection section in decoded.Sections)
                sections.Add(new LootSectionSnapshot(section.Kind, SetLootSnapshot(section.Grid),
                    section.Body == null ? null : SetLootSnapshot(section.Body)));
            return sections;
        }

        /// <summary>在当前搜刮会话内按容器编号复用地址，并替换其快照。</summary>
        private ContainerSnapshot SetLootSnapshot(ContainerSnapshot value)
        {
            if (!loot.TryGetValue(value.Id, out ProjectionEndpoint endpoint))
            {
                endpoint = Register(lootSubscriptions, null);
                loot.Add(value.Id, endpoint);
            }
            endpoint.Snapshot = new ContainerSnapshot(value.Id, value.Kind, value.Icon, value.Items,
                value.ValidCells, endpoint.Source, value.Revision, value.IsSlot, rejectContainers: value.RejectContainers,
                isReady: value.IsReady, withdrawalOnly: value.WithdrawalOnly);
            return endpoint.Snapshot;
        }

        /// <summary>在搜刮展示绑定成功后发布本次更新。</summary>
        internal void NotifyLootChanged()
        {
            foreach (ProjectionEndpoint endpoint in loot.Values) EventBus.Global.Publish(LoadoutChannels.Changed, endpoint.Source);
        }

        /// <summary>释放当前搜刮会话的订阅和地址。</summary>
        internal void CloseLoot()
        {
            foreach (EventSubscription subscription in lootSubscriptions) subscription.Dispose();
            lootSubscriptions.Clear();
            loot.Clear();
        }

        /// <summary>释放角色会话的稳定地址和连接订阅。</summary>
        internal void EndRoleSession()
        {
            foreach (EventSubscription subscription in roleSubscriptions) subscription.Dispose();
            roleSubscriptions.Clear();
            Array.Clear(role, 0, role.Length);
            listener = null;
            onOperation = null;
        }
    }
}
