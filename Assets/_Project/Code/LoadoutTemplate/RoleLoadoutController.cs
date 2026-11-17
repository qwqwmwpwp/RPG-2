using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    [Serializable]
    public sealed class RoleSlotConfiguration
    {
        public LoadoutSlotId slotId;
        public BackpackContainerController containerController;
        public LoadoutSlotRule rule;
        public LoadoutItemDefinition initialItem;
        public string initialItemId;
    }

    /// <summary>十个槽位共用的数据端点。</summary>
    public sealed class LoadoutSlotEndpoint : ILoadoutEndpoint
    {
        private string id;
        private readonly Func<bool> available;
        private readonly Action<ItemDisplayData> committed;
        private readonly Action changed;
        private readonly LoadoutEndpointAddress eventSource;
        private ItemDisplayData item;
        private int revision;
        private RoleLoadoutController roleOwner;
        private LoadoutSlotRule independentRule;
        internal LoadoutSlotEndpoint(string id, LoadoutSlotRule rule, ItemDisplayData item,
            Func<bool> available, Action<ItemDisplayData> committed = null, Action changed = null)
        {
            this.id = id; Rule = rule; this.item = item?.At(Vector2Int.zero);
            this.available = available; this.committed = committed; this.changed = changed;
            eventSource = new LoadoutEndpointAddress(this);
        }
        public object EventSource => eventSource;
        public LoadoutSlotRule Rule { get; private set; }
        public ItemDisplayData Item => item;
        internal RoleLoadoutController RoleOwner => roleOwner;
        internal bool IsAvailable => available() && (roleOwner == null || roleOwner.isActiveAndEnabled);
        public ContainerSnapshot Snapshot => new ContainerSnapshot(id, item?.ContainerType ?? ContainerKind.None,
            item?.Icon, item == null ? Array.Empty<ItemDisplayData>() : new[] { item }, eventSource: EventSource,
            revision: revision, isSlot: true, rule: Rule, rejectContainers: false, isReady: IsAvailable);
        internal void ApplyNetworkState(string instanceId, ItemDisplayData value, int version)
        { id = instanceId; item = value; revision = version; committed?.Invoke(item); }
        internal bool CanAttachRole(RoleLoadoutController owner) => roleOwner == null || roleOwner == owner;
        internal void AttachRole(RoleLoadoutController owner, LoadoutSlotRule rule)
        {
            if (roleOwner == null) independentRule = Rule;
            roleOwner = owner; Rule = rule; revision++;
        }
        internal void DetachRole(RoleLoadoutController owner)
        {
            if (!ReferenceEquals(roleOwner, owner)) return;
            roleOwner = null; Rule = independentRule; independentRule = null; revision++;
            PublishChanged();
        }
        public bool TryPrepare(IReadOnlyList<ItemDisplayData> items, out object prepared, out string error)
        {
            prepared = null;
            if (!LoadoutOperations.Validate(Snapshot, items, out error)) return false;
            prepared = items.Count == 0 ? null : items[0].At(Vector2Int.zero); return true;
        }
        public void Commit(object prepared) { item = (ItemDisplayData)prepared; revision++; committed?.Invoke(item); }
        public void PublishChanged() { EventBus.Global.Publish(LoadoutChannels.Changed, EventSource); changed?.Invoke(); roleOwner?.PublishRuntimeChanged(); }
    }

    /// <summary>持有十个槽位及四个稳定容器入口的角色控制器。</summary>
    [DisallowMultipleComponent]
    public sealed partial class RoleLoadoutController : MonoBehaviour
    {
        [Tooltip("与背包 UI 和交换器配置相同角色标识，通过事件发现对应实例。")]
        [SerializeField] private string ownerKey = "local-player";
        [SerializeField] private RoleSlotConfiguration[] slots = Array.Empty<RoleSlotConfiguration>();
        private readonly Dictionary<LoadoutSlotId, LoadoutSlotEndpoint> endpoints = new Dictionary<LoadoutSlotId, LoadoutSlotEndpoint>();
        private readonly List<LoadoutEndpointEvents> events = new List<LoadoutEndpointEvents>();
        private readonly object providerSource = new object();
        private EventSubscription connectionRequests;
        private bool initialized;
        internal bool NetworkManaged => GetComponentInParent<NetworkRoleLoadout>(true) != null;
        internal IReadOnlyList<RoleSlotConfiguration> Configuration => slots;
        private void Awake() { if (NetworkManaged) return; if (!EnsureInitialized(out string error)) Debug.LogWarning(error, this); }
        private void OnEnable()
        {
            if (NetworkManaged) return;
            connectionRequests.Dispose();
            connectionRequests = EventBus.Global.Subscribe<LoadoutRoleConnectRequest>(
                LoadoutChannels.RoleConnectRequested, EventBus.AnySource, this, OnConnectionRequested);
            if (!EnsureInitialized(out string error)) { Debug.LogWarning(error, this); return; }
            BindEvents();
            foreach (LoadoutSlotEndpoint endpoint in endpoints.Values) endpoint.PublishChanged();
            EventBus.Global.Publish(LoadoutChannels.RoleAvailable, providerSource, ownerKey);
        }
        private void OnDisable()
        {
            connectionRequests.Dispose();
            EventBus.Global.Publish(LoadoutChannels.RoleDisconnected, providerSource);
            foreach (LoadoutSlotEndpoint endpoint in endpoints.Values) endpoint.PublishChanged();
            // 保留查询订阅，使尚未刷新的 UI 能读取不可用快照。
        }
        private void OnDestroy()
        {
            ClearRuntimeReservations();
            connectionRequests.Dispose();
            EventBus.Global.Publish(LoadoutChannels.RoleDisconnected, providerSource);
            foreach (LoadoutSlotEndpoint endpoint in endpoints.Values) endpoint.DetachRole(this);
            ClearEvents();
        }
        private void OnConnectionRequested(LoadoutRoleConnectRequest request)
        {
            if (request.Session == null || !request.Session.IsActive || request.ReplySource == null
                || !string.Equals(request.Session.OwnerKey, ownerKey, StringComparison.Ordinal)) return;
            string error = null;
            List<LoadoutRoleBinding> bindings = null;
            if (!isActiveAndEnabled) error = "角色装备控制器当前不可用。";
            else if (EnsureInitialized(out error))
            {
                bindings = new List<LoadoutRoleBinding>(10);
                foreach (RoleSlotConfiguration slot in slots)
                {
                    LoadoutSlotEndpoint endpoint = endpoints[slot.slotId];
                    BackpackContainerController storage = (int)slot.slotId <= (int)LoadoutSlotId.SafeBox
                        ? slot.containerController : null;
                    bindings.Add(new LoadoutRoleBinding(slot.slotId, endpoint.EventSource,
                        storage != null ? storage.EventSource : null, endpoint.Snapshot,
                        storage != null ? storage.Snapshot : null));
                }
            }
            EventBus.Global.Publish(LoadoutChannels.RoleConnected, request.ReplySource,
                new LoadoutRoleConnectReply(request, providerSource, bindings, error));
        }
        public bool EnsureInitialized(out string error)
        {
            error = null;
            if (initialized) return true;
            if (NetworkManaged && !Mirror.NetworkServer.active) return Fail("网络角色只允许服务器初始化权威库存。", out error);
            if (string.IsNullOrWhiteSpace(ownerKey)) return Fail("角色连接标识不能为空。", out error);
            if (slots == null || slots.Length != 10) return Fail("角色控制器需要配置十个槽位。", out error);
            var candidate = new Dictionary<LoadoutSlotId, LoadoutSlotEndpoint>();
            var controllerIds = new HashSet<BackpackContainerController>();
            var storageIds = new HashSet<string>(StringComparer.Ordinal);
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            var endpointIds = new HashSet<string>(StringComparer.Ordinal);
            string instanceId = "loadout@" + Guid.NewGuid().ToString("N");
            foreach (RoleSlotConfiguration slot in slots)
            {
                if (slot == null || (int)slot.slotId < 0 || (int)slot.slotId > 9 || candidate.ContainsKey(slot.slotId) || slot.rule == null)
                    return Fail("槽位编号重复、无效或未配置标签规则。", out error);
                LoadoutSlotEndpoint endpoint;
                if ((int)slot.slotId <= (int)LoadoutSlotId.SafeBox)
                {
                    if (slot.containerController == null || !controllerIds.Add(slot.containerController))
                        return Fail("四个容器槽需要各自独立的容器控制器。", out error);
                    if (!slot.containerController.EnsureInitialized(out error)) return false;
                    endpoint = (LoadoutSlotEndpoint)slot.containerController.BodyEndpoint;
                    if (!endpoint.CanAttachRole(this)) return Fail("容器控制器已经被其他角色占用。", out error);
                    BackpackContainerData storage = endpoint.Item?.Storage;
                    if (storage != null)
                    {
                        if (!storageIds.Add(storage.Id) || !endpointIds.Add(storage.Id))
                            return Fail("储物容器和槽位的实例编号必须唯一。", out error);
                        foreach (ItemDisplayData stored in storage.Items)
                            if (!itemIds.Add(stored.Id)) return Fail("容器内容存在重复物品实例编号。", out error);
                    }
                }
                else
                {
                    string itemId = instanceId + "/" + slot.slotId + "/" + (string.IsNullOrWhiteSpace(slot.initialItemId)
                        ? "item" : slot.initialItemId);
                    ItemDisplayData item = slot.initialItem == null ? null : slot.initialItem.Create(itemId, Vector2Int.zero);
                    endpoint = new LoadoutSlotEndpoint(instanceId + "/slot/" + slot.slotId, slot.rule, item,
                        () => this != null && isActiveAndEnabled);
                }
                ContainerSnapshot snapshot = endpoint.Snapshot;
                if (!endpointIds.Add(snapshot.Id)) return Fail("槽位实例编号必须唯一。", out error);
                foreach (ItemDisplayData item in snapshot.Items)
                    if (!itemIds.Add(item.Id)) return Fail("角色槽位存在重复物品实例编号。", out error);
                var configured = new ContainerSnapshot(snapshot.Id, snapshot.Kind, snapshot.Icon, snapshot.Items,
                    eventSource: snapshot.EventSource, revision: snapshot.Revision, isSlot: true,
                    rule: slot.rule, rejectContainers: false, isReady: snapshot.IsReady);
                if (!LoadoutOperations.Validate(configured, configured.Items, out error)) return false;
                candidate.Add(slot.slotId, endpoint);
            }
            // 全部配置及初始内容通过后，才变更共享容器槽的归属和规则。
            foreach (RoleSlotConfiguration slot in slots)
            {
                LoadoutSlotEndpoint endpoint = candidate[slot.slotId];
                endpoint.AttachRole(this, slot.rule);
                if ((int)slot.slotId < 4) slot.containerController.AttachRuntimeOwner(this);
                endpoints.Add(slot.slotId, endpoint);
            }
            initialized = true;
            if (isActiveAndEnabled) BindEvents();
            return true;
        }
        public ILoadoutEndpoint GetSlot(LoadoutSlotId id)
        {
            if (!EnsureInitialized(out _) || !endpoints.TryGetValue(id, out LoadoutSlotEndpoint endpoint)) return null;
            return endpoint;
        }
        private void BindEvents()
        {
            if (NetworkManaged) return;
            ClearEvents();
            foreach (var entry in endpoints)
                if ((int)entry.Key > (int)LoadoutSlotId.SafeBox) events.Add(new LoadoutEndpointEvents(entry.Value));
        }
        private void ClearEvents() { foreach (LoadoutEndpointEvents subscription in events) subscription.Dispose(); events.Clear(); }
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}



