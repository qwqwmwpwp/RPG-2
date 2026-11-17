using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    internal sealed class DecodedLootSection
    {
        internal readonly LootSectionKind Kind;
        internal readonly ContainerSnapshot Grid, Body;
        internal DecodedLootSection(LootSectionKind kind, ContainerSnapshot grid, ContainerSnapshot body)
        { Kind = kind; Grid = grid; Body = body; }
    }

    internal sealed class DecodedLootState
    {
        internal readonly string Id;
        internal readonly int Revision;
        internal readonly bool Ground;
        internal readonly IReadOnlyList<DecodedLootSection> Sections;
        internal DecodedLootState(string id, int revision, bool ground, List<DecodedLootSection> sections)
        { Id = id; Revision = revision; Ground = ground; Sections = sections.AsReadOnly(); }
    }

    /// <summary>独立战利品 schema、容量和储物关联校验，复用角色协议的物品快照重建。</summary>
    internal sealed class LootStateCodec
    {
        // 战利品投影独立于角色持久化协议，新增访问策略不改变已保存的角色 schema。
        private const int LootSchema = 5;
        [Serializable] private sealed class LootWire
        {
            public int schema = LootSchema, revision, accessPolicy;
            public string id;
            public bool ground;
            public StorageWire[] storages;
            public LootGridWire[] sections;
        }
        [Serializable] private sealed class LootGridWire
        {
            public int kind, containerKind;
            public string gridId, bodyId;
            public bool ready, hasBody;
            public Vector2Int[] cells;
            public ItemWire[] items;
            public ItemWire body;
        }
        private const int MaxWireBytes = LoadoutStateCodec.MaxWireBytes, MaxItems = LoadoutStateCodec.MaxItems, MaxCells = LoadoutStateCodec.MaxCells;
        private readonly LoadoutCodecConfiguration configuration;
        private readonly LoadoutStateCodec itemCodec;
        internal LootStateCodec(LoadoutCodecConfiguration configuration, LoadoutStateCodec itemCodec)
        { this.configuration = configuration; this.itemCodec = itemCodec; }

        internal bool TryEncodeLoot(LootSource source, out string json, out string error,
            Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates = null)
        {
            json = null; error = null;
            if (source == null || source.Revision == int.MaxValue) return Fail("战利品源或版本无效。", out error);
            int changes = 0;
            if (candidates != null) foreach (ILoadoutEndpoint endpoint in candidates.Keys) if (source.Owns(endpoint)) changes++;
            if ((long)source.Revision + changes >= int.MaxValue) return Fail("战利品源版本已达到上限。", out error);
            var sections = new List<LootGridWire>(); int totalItems = 0, totalCells = 0;
            var groundStorages = new List<StorageWire>();
            bool ground = source.GroundSession != null;
            foreach (LootSection section in source.Sections)
            {
                ContainerSnapshot grid = section.Grid.Snapshot, body = section.Body?.Snapshot;
                IReadOnlyList<ItemDisplayData> gridItems = grid.Items, bodyItems = body?.Items;
                if (candidates != null)
                {
                    if (candidates.TryGetValue(section.Grid, out List<ItemDisplayData> candidateGrid)) gridItems = candidateGrid;
                    if (section.Body != null && candidates.TryGetValue(section.Body, out List<ItemDisplayData> candidateBody))
                    { bodyItems = candidateBody; if (bodyItems.Count == 0) gridItems = Array.Empty<ItemDisplayData>(); }
                }
                if (section.Kind == LootSectionKind.FixedGrid || section.Kind == LootSectionKind.Equipment || (int)section.Kind >= 1 && (int)section.Kind <= 3) { }
                else return Fail("战利品分区无效。", out error);
                var items = new List<ItemWire>();
                foreach (ItemDisplayData item in gridItems)
                {
                    if (item == null || !itemCodec.EncodeItem(item, out ItemWire wire, out error) || !CanDecodeDefinition(wire.definition)) return Fail(error ?? "客户端目录缺少战利品定义。", out error);
                    items.Add(wire);
                    if (ground && item.Storage != null)
                    {
                        var stored = new List<ItemWire>();
                        foreach (ItemDisplayData value in item.Storage.Items)
                        { if (!itemCodec.EncodeItem(value, out ItemWire encoded, out error)) return false; stored.Add(encoded); }
                        totalItems += stored.Count; totalCells += item.Storage.ValidCells.Count;
                        groundStorages.Add(new StorageWire { id = item.Storage.Id, kind = (int)item.Storage.Kind,
                            cells = new List<Vector2Int>(item.Storage.ValidCells).ToArray(), items = stored.ToArray() });
                    }
                }
                if (bodyItems != null && bodyItems.Count > 1) return Fail("战利品本体槽最多只能包含一个物品。", out error);
                ItemWire encodedBody = null;
                if (bodyItems != null && bodyItems.Count != 0 && (bodyItems[0] == null || !itemCodec.EncodeItem(bodyItems[0], out encodedBody, out error) || !CanDecodeDefinition(encodedBody.definition)))
                    return Fail(error ?? "客户端目录缺少战利品容器定义。", out error);
                totalItems += items.Count + (encodedBody == null ? 0 : 1); totalCells += grid.ValidCells?.Count ?? 0;
                if (totalItems > MaxItems || totalCells > MaxCells * 4) return Fail("战利品源超过协议容量。", out error);
                sections.Add(new LootGridWire { kind = (int)section.Kind, containerKind = (int)grid.Kind, gridId = grid.Id, bodyId = body?.Id,
                    ready = (grid.IsReady || !source.IsActive) && (body == null || bodyItems.Count != 0), hasBody = encodedBody != null, body = encodedBody ?? new ItemWire(),
                    cells = new List<Vector2Int>(grid.ValidCells ?? Array.Empty<Vector2Int>()).ToArray(), items = items.ToArray() });
            }
            json = JsonUtility.ToJson(new LootWire { id = source.Id, revision = source.Revision + changes,
                accessPolicy = (int)source.AccessPolicy, ground = ground, storages = groundStorages.ToArray(), sections = sections.ToArray() });
            if (!Bounded(json, MaxWireBytes)) return Fail("战利品快照超过协议容量。", out error);
            // 同样解码一次，保证提交死亡转移之前该数据可以被本角色目录重建。
            if (!TryDecodeLoot(json, out _, out error)) return false;
            return true;
        }
        private bool CanDecodeDefinition(string definition)
        {
            if (configuration.Catalog.ContainsKey(definition)) return true;
            for (int i = 1; i <= 4; i++) if (definition == "$container/" + i && ContainerConfiguration((ContainerKind)i)?.Definition == null) return true;
            return false;
        }
        internal bool TryDecodeLoot(string json, out DecodedLootState result, out string error)
        {
            result = null; error = null;
            if (!Bounded(json, MaxWireBytes)) return Fail("战利品快照长度无效。", out error);
            LootWire wire;
            try { wire = JsonUtility.FromJson<LootWire>(json); } catch (Exception) { return Fail("战利品快照无法解析。", out error); }
            if (wire == null || wire.schema != LootSchema || !ValidId(wire.id) || wire.revision < 0 || wire.sections == null || wire.sections.Length > 4
                || wire.storages == null || wire.storages.Length > MaxItems || !wire.ground && wire.storages.Length != 0
                || wire.ground && (wire.sections.Length != 1 || wire.accessPolicy != (int)LootAccessPolicy.WithdrawalOnly)
                || wire.accessPolicy < 0 || wire.accessPolicy > 1 || wire.accessPolicy == (int)LootAccessPolicy.ReadWrite && wire.sections.Length != 1)
                return Fail("战利品快照协议无效。", out error);
            var sections = new List<DecodedLootSection>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            var kinds = new HashSet<int>(); int totalItems = 0, totalCells = 0;
            var groundStorages = new Dictionary<string, BackpackContainerData>(StringComparer.Ordinal);
            var usedGroundStorages = new HashSet<string>(StringComparer.Ordinal);
            foreach (StorageWire storage in wire.storages)
            {
                if (storage == null || !ValidId(storage.id) || !ids.Add(storage.id) || storage.kind < 1 || storage.kind > 4
                    || storage.cells == null || storage.cells.Length == 0 || storage.cells.Length > MaxCells || storage.items == null
                    || (totalItems += storage.items.Length) > MaxItems || (totalCells += storage.cells.Length) > MaxCells * 4)
                    return Fail("地面容器快照超过协议范围。", out error);
                LoadoutCodecContainer container = ContainerConfiguration((ContainerKind)storage.kind);
                if (container == null)
                    return Fail(error ?? "地面容器缺少角色配置。", out error);
                var contents = new List<ItemDisplayData>();
                foreach (ItemWire stored in storage.items)
                {
                    if (stored == null || !string.IsNullOrEmpty(stored.storage)
                        || !itemCodec.DecodeItem(stored, groundStorages, ids, out ItemDisplayData value, out error) || value.IsContainer)
                        return Fail(error ?? "地面容器禁止嵌套。", out error);
                    contents.Add(value);
                }
                if (!BackpackContainerData.TryCreate(storage.id, (ContainerKind)storage.kind,
                    container.Icon,
                    storage.cells, contents, out BackpackContainerData data, out error)) return false;
                groundStorages.Add(data.Id, data);
            }
            foreach (LootGridWire section in wire.sections)
            {
                bool writable = wire.accessPolicy == (int)LootAccessPolicy.ReadWrite;
                if ((writable || wire.ground) && (section == null || section.kind != (int)LootSectionKind.FixedGrid || section.hasBody || !string.IsNullOrEmpty(section.bodyId)
                    || section.containerKind != (int)ContainerKind.None)) return Fail("可写战利品源必须是无本体固定网格。", out error);
                if (section == null || section.kind < 0 || section.kind > 4 || !kinds.Add(section.kind) || !ValidId(section.gridId)
                    || !ids.Add(section.gridId) || section.containerKind < 0 || section.containerKind > 3 || section.cells == null
                    || section.cells.Length > MaxCells || section.items == null || (totalItems += section.items.Length + (section.hasBody ? 1 : 0)) > MaxItems
                    || (totalCells += section.cells.Length) > MaxCells * 4) return Fail("战利品分区无效或超过协议容量。", out error);
                var contents = new List<ItemDisplayData>();
                var storages = wire.ground ? groundStorages : new Dictionary<string, BackpackContainerData>(StringComparer.Ordinal);
                foreach (ItemWire item in section.items)
                {
                    if (item == null || !wire.ground && !string.IsNullOrEmpty(item.storage) || !itemCodec.DecodeItem(item, storages, ids, out ItemDisplayData value, out error)
                        || !wire.ground && value.IsContainer) return Fail(error ?? "战利品储物内容禁止容器嵌套。", out error);
                    if (wire.ground && value.Storage != null && !usedGroundStorages.Add(value.Storage.Id))
                        return Fail("地面容器重复引用储物实例。", out error);
                    contents.Add(value);
                }
                ContainerKind kind = (ContainerKind)section.containerKind;
                if (!BackpackContainerData.TryCreate(section.gridId, kind, null, section.cells, contents, out BackpackContainerData data, out error, rejectContainers: !wire.ground)) return false;
                if (!wire.ground) storages.Add(data.Id, data);
                ItemDisplayData body = null;
                if (section.hasBody)
                {
                    if (section.kind != (int)LootSectionKind.ChestRig && section.kind != (int)LootSectionKind.Backpack
                        || !ValidId(section.bodyId) || !ids.Add(section.bodyId) || section.body == null
                        || !itemCodec.DecodeItem(section.body, storages, ids, out body, out error) || body.Storage != data || body.ContainerType != kind)
                        return Fail(error ?? "战利品容器本体无效。", out error);
                }
                var grid = new ContainerSnapshot(data.Id, kind, body?.Icon, data.Items, data.ValidCells, revision: wire.revision,
                    isReady: section.ready, withdrawalOnly: !writable, rejectContainers: !wire.ground);
                ContainerSnapshot bodySnapshot = string.IsNullOrEmpty(section.bodyId) ? null : new ContainerSnapshot(section.bodyId, kind, body?.Icon,
                    body == null ? Array.Empty<ItemDisplayData>() : new[] { body }, revision: wire.revision, isSlot: true, rejectContainers: false, withdrawalOnly: true);
                sections.Add(new DecodedLootSection((LootSectionKind)section.kind, grid, bodySnapshot));
            }
            if (usedGroundStorages.Count != groundStorages.Count)
                return Fail("地面快照包含未引用储物实例。", out error);
            result = new DecodedLootState(wire.id, wire.revision, wire.ground, sections); return true;
        }

        private LoadoutCodecContainer ContainerConfiguration(ContainerKind kind) => configuration.Containers[(int)kind - 1];
        private static bool ValidId(string id) => LoadoutStateCodec.ValidId(id);
        private static bool Bounded(string value, int max) => LoadoutStateCodec.Bounded(value, max);
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
