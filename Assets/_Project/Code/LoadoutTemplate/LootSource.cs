using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    public enum LootSectionKind { Equipment, ChestRig, Pocket, Backpack, FixedGrid }
    public enum LootAccessPolicy { WithdrawalOnly, ReadWrite }

    /// <summary>顺序展示入口。本体与储物共用独立数据，不引用死亡角色组件。</summary>
    public sealed class LootSection
    {
        internal LootSection(LootSectionKind kind, ILoadoutEndpoint grid, ILoadoutEndpoint body = null)
        { Kind = kind; Grid = grid; Body = body; }
        public LootSectionKind Kind { get; }
        public ILoadoutEndpoint Grid { get; }
        public ILoadoutEndpoint Body { get; }
    }

    /// <summary>角色销毁后仍持有原物品和储物关联的战利品源。</summary>
    public sealed class LootSource
    {
        private readonly List<LootSection> sections = new List<LootSection>();
        private readonly Dictionary<string, ILoadoutEndpoint> endpoints = new Dictionary<string, ILoadoutEndpoint>(StringComparer.Ordinal);
        internal RoleLoadoutController PreparedOwner;
        internal readonly Dictionary<ILoadoutEndpoint, ContainerSnapshot> PreparedSlots = new Dictionary<ILoadoutEndpoint, ContainerSnapshot>();
        internal readonly Dictionary<BackpackContainerData, int> PreparedStorages = new Dictionary<BackpackContainerData, int>();
        public string Id { get; } = "loot@" + Guid.NewGuid().ToString("N");
        public int Revision { get; private set; }
        public LootAccessPolicy AccessPolicy { get; }
        internal GroundLootSession GroundSession;
        internal void AddGround(ILoadoutEndpoint endpoint)
        { sections.Add(new LootSection(LootSectionKind.FixedGrid, endpoint)); endpoints.Add(endpoint.Snapshot.Id, endpoint); }
        internal void Deactivate() { IsActive = false; }
        public LootSource(LootAccessPolicy accessPolicy = LootAccessPolicy.WithdrawalOnly)
        {
            if (accessPolicy != LootAccessPolicy.WithdrawalOnly && accessPolicy != LootAccessPolicy.ReadWrite)
                throw new ArgumentOutOfRangeException(nameof(accessPolicy));
            AccessPolicy = accessPolicy;
        }
        public bool IsActive { get; private set; }
        public IReadOnlyList<LootSection> Sections => sections.AsReadOnly();
        public bool IsEmpty
        {
            get
            {
                foreach (LootSection section in sections)
                    if ((section.Grid.Snapshot.IsReady && section.Grid.Snapshot.Items.Count != 0) || section.Body?.Snapshot.Items.Count > 0) return false;
                return true;
            }
        }
        public event Action Changed;
        internal void Add(LootSectionKind kind, BackpackContainerData data, ItemDisplayData body = null)
        {
            if (AccessPolicy == LootAccessPolicy.ReadWrite && (kind != LootSectionKind.FixedGrid || body != null))
                throw new ArgumentException("可写战利品源只允许固定网格。");
            var grid = new LootEndpoint(this, data);
            LootEndpoint bodyEndpoint = body == null ? null : new LootEndpoint(this, body);
            if (bodyEndpoint != null) grid.ParentBody = bodyEndpoint;
            sections.Add(new LootSection(kind, grid, bodyEndpoint));
            endpoints.Add(grid.Snapshot.Id, grid);
            if (bodyEndpoint != null) endpoints.Add(bodyEndpoint.Snapshot.Id, bodyEndpoint);
        }
        internal void Activate() { IsActive = true; PreparedOwner = null; PreparedSlots.Clear(); PreparedStorages.Clear(); }
        public bool TryGetEndpoint(string id, out ILoadoutEndpoint endpoint)
        { endpoint = null; if (id == null || !endpoints.TryGetValue(id, out ILoadoutEndpoint found)) return false; endpoint = found; return true; }
        internal bool Owns(ILoadoutEndpoint endpoint) => endpoint != null && endpoints.TryGetValue(endpoint.Snapshot.Id, out ILoadoutEndpoint value) && ReferenceEquals(value, endpoint);
        internal void Bump() { Revision++; }
        internal void Publish()
        {
            GroundLootSession.PublishPending();
            foreach (ILoadoutEndpoint endpoint in endpoints.Values)
                try { endpoint.PublishChanged(); } catch (Exception exception) { Debug.LogException(exception); }
            if (Changed != null) foreach (Action subscriber in Changed.GetInvocationList())
                try { subscriber(); } catch (Exception exception) { Debug.LogException(exception); }
        }
    }

    internal interface IExternalLootEndpoint : ILoadoutEndpoint { }

    internal sealed class LootEndpoint : IExternalLootEndpoint
    {
        internal readonly LootSource Owner;
        internal LootEndpoint ParentBody;
        private readonly BackpackContainerData data;
        private readonly string id;
        private ItemDisplayData body;
        private readonly bool isBody;
        private readonly LoadoutEndpointAddress address;
        internal LootEndpoint(LootSource owner, BackpackContainerData data)
        { Owner = owner; this.data = data; id = data.Id; address = new LoadoutEndpointAddress(this); }
        internal LootEndpoint(LootSource owner, ItemDisplayData body)
        { Owner = owner; this.body = body.At(Vector2Int.zero); isBody = true; id = owner.Id + "/body/" + (int)body.ContainerType; address = new LoadoutEndpointAddress(this); }
        public object EventSource => address;
        public ContainerSnapshot Snapshot => new ContainerSnapshot(id, isBody ? body?.ContainerType ?? ContainerKind.None : data.Kind,
            isBody ? body?.Icon : data.Icon, isBody ? body == null ? Array.Empty<ItemDisplayData>() : new[] { body } : ParentBody != null && ParentBody.body == null ? Array.Empty<ItemDisplayData>() : data.Items,
            isBody ? null : data.ValidCells, EventSource, Owner.Revision, isSlot: isBody, rejectContainers: !isBody,
            isReady: Owner.IsActive && (ParentBody == null || ParentBody.body != null), withdrawalOnly: Owner.AccessPolicy == LootAccessPolicy.WithdrawalOnly);
        public bool TryPrepare(IReadOnlyList<ItemDisplayData> items, out object prepared, out string error)
        {
            prepared = null; error = null;
            // 战利品本体只允许被取走；不会在此端点生成或换入新实例。
            if (isBody)
            {
                if (items.Count != 0) { error = "战利品容器本体只允许取走。"; return false; }
                return true;
            }
            if (!data.TryPrepare(items, out BackpackContainerData.PreparedContents contents, out error)) return false;
            prepared = contents; return true;
        }
        public void Commit(object prepared)
        { if (isBody) body = null; else data.Commit((BackpackContainerData.PreparedContents)prepared); Owner.Bump(); }
        public void PublishChanged() => EventBus.Global.Publish(LoadoutChannels.Changed, EventSource);
    }

    public sealed partial class RoleLoadoutController
    {
        /// <summary>仅准备；生成实体失败时角色库存没有变化。</summary>
        public bool TryPrepareCorpseLoot(out LootSource source, out string error)
        {
            source = null;
            if (!EnsureInitialized(out error)) return false;
            var candidate = new LootSource { PreparedOwner = this };
            var equipment = new List<ItemDisplayData>();
            foreach (LoadoutSlotId id in Enum.GetValues(typeof(LoadoutSlotId)))
            {
                if (id == LoadoutSlotId.SafeBox) continue;
                ILoadoutEndpoint endpoint = GetSlot(id);
                ContainerSnapshot snapshot = endpoint.Snapshot;
                candidate.PreparedSlots.Add(endpoint, snapshot);
                foreach (ItemDisplayData item in snapshot.Items)
                {
                    if ((int)id >= 4) equipment.Add(item);
                    else if (item.Storage != null) candidate.PreparedStorages.Add(item.Storage, item.Storage.Revision);
                }
            }
            if (!EquipmentRectanglePacking.TryPack(equipment, out Vector2Int size, out List<ItemDisplayData> packed, out error)) return false;
            var cells = new List<Vector2Int>();
            for (int y = 0; y < size.y; y++) for (int x = 0; x < size.x; x++) cells.Add(new Vector2Int(x, y));
            if (!BackpackContainerData.TryCreate(candidate.Id + "/equipment", ContainerKind.None, null, cells, packed,
                out BackpackContainerData grid, out error)) return false;
            candidate.Add(LootSectionKind.Equipment, grid);
            for (int i = 0; i < 3; i++)
            {
                var item = ((LoadoutSlotEndpoint)GetSlot((LoadoutSlotId)i)).Item;
                if (item?.Storage != null) candidate.Add((LootSectionKind)(i + 1), item.Storage, i == 1 ? null : item);
            }
            source = candidate; return true;
        }

        /// <summary>预检全部版本后一次提交；提交后广播异常不改变成功结果。</summary>
        public bool CommitCorpseLoot(LootSource source, out string error)
        {
            error = null;
            if (source == null || source.IsActive || !ReferenceEquals(source.PreparedOwner, this))
                return Fail("尸体战利品准备令牌无效。", out error);
            if (!CommitCoordinator.TryEnter(out IDisposable transaction, out LoadoutFailure failure))
                return Fail(failure.Reason, out error);
            using var transactionScope = transaction;
            LoadoutCommitRecord commit = null;
            try
            {
                LoadoutStateVersion before = CaptureStateVersion(source);
                var states = new Dictionary<ILoadoutEndpoint, object>();
                var candidates = new Dictionary<ILoadoutEndpoint, List<ItemDisplayData>>();
                var dependencies = new Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot>();
                foreach (var entry in source.PreparedSlots)
                {
                    ContainerSnapshot current = entry.Key.Snapshot;
                    if (current.Id != entry.Value.Id || current.Revision != entry.Value.Revision
                        || current.Revision == int.MaxValue)
                        return Fail("角色库存已变化，尸体战利品需要重新准备。", out error);
                    dependencies.Add(entry.Key, LoadoutRuleSnapshot.CaptureEndpoint(entry.Key, current));
                    if (!entry.Key.TryPrepare(Array.Empty<ItemDisplayData>(), out object empty, out error)) return false;
                    states.Add(entry.Key, empty);
                    candidates.Add(entry.Key, new List<ItemDisplayData>());
                }
                foreach (RoleSlotConfiguration configuration in slots)
                {
                    if ((int)configuration.slotId >= 3) continue;
                    ILoadoutEndpoint grid = configuration.containerController;
                    ContainerSnapshot current = grid.Snapshot;
                    if (current.Revision == int.MaxValue)
                        return Fail("角色储物网格版本已达到上限。", out error);
                    dependencies.Add(grid, LoadoutRuleSnapshot.CaptureEndpoint(grid, current));
                }
                foreach (var entry in source.PreparedStorages)
                    if (entry.Key.Revision != entry.Value) return Fail("角色储物内容已变化。", out error);
                var prepared = new PreparedLoadoutOperation(candidates, states, dependencies);
                if (Network != null && !Network.TryPrepareCorpseChanges(candidates, source, out error)) return false;
                // 安全箱从未纳入候选。清空角色端点、激活新归属并推进角色版本后才发布。
                if (!CommitCoordinator.TryCommit(prepared, CreateOperationKey(LoadoutOperationChannel.System), before,
                    source, out commit, out failure, completeModel: () =>
                    {
                        ClearRuntimeReservations();
                        source.Activate();
                    })) return Fail(failure.Reason, out error);
                PublishRuntimeTransaction(commit, () => prepared.PublishChanges(source));
                return true;
            }
            catch (Exception exception)
            {
                error = commit == null ? exception.Message : null;
                if (commit != null) Debug.LogException(exception, this);
                return commit != null;
            }
        }

        internal bool OwnsEndpoint(ILoadoutEndpoint endpoint)
        {
            foreach (RoleSlotConfiguration configuration in slots)
                if (ReferenceEquals(GetSlot(configuration.slotId), endpoint)
                    || ((int)configuration.slotId < 4 && ReferenceEquals(configuration.containerController, endpoint))) return true;
            return false;
        }
    }
}
