using System;
using System.Collections.Generic;
using System.Text;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    [Serializable] internal sealed class StateWire
    {
        public int schema = LoadoutStateCodec.Schema;
        public string configuration, epoch;
        public long revision;
        public bool accepting;
        public int selectedWeapon;
        public SlotWire[] slots;
        public StorageWire[] storages;
    }
    [Serializable] internal sealed class SlotWire { public int slot, revision, storageRevision; public string id; public bool hasItem; public ItemWire item; }
    [Serializable] internal sealed class StorageWire { public string id; public int kind; public Vector2Int[] cells; public ItemWire[] items; }
    [Serializable] internal sealed class ItemWire { public string id, definition, storage, runtimeState; public ulong runtimeItemId; public Vector2Int origin; public int count, runtimeRevision, structureRevision; public bool rotated; }
    internal sealed class Candidate
    {
        internal LoadoutSlotId selectedWeapon;
        internal readonly ItemDisplayData[] items = new ItemDisplayData[10];
        internal readonly SlotWire[] slots = new SlotWire[10];
        internal readonly Dictionary<string, BackpackContainerData> storages = new Dictionary<string, BackpackContainerData>(StringComparer.Ordinal);
    }
    /// <summary>编解码所需的目录、槽位规则和容器形状；不持有角色或网络组件。</summary>
    internal sealed class LoadoutCodecConfiguration
    {
        internal readonly string Signature;
        internal readonly IReadOnlyDictionary<string, LoadoutItemDefinition> Catalog;
        internal readonly ItemManifestCatalog ManifestCatalog;
        internal readonly IReadOnlyList<LoadoutSlotRule> Rules;
        internal readonly IReadOnlyList<LoadoutCodecContainer> Containers;
        internal LoadoutCodecConfiguration(string signature, IDictionary<string, LoadoutItemDefinition> catalog,
            ItemManifestCatalog manifestCatalog, LoadoutSlotRule[] rules, LoadoutCodecContainer[] containers)
        {
            Signature = signature;
            Catalog = new Dictionary<string, LoadoutItemDefinition>(catalog, StringComparer.Ordinal);
            ManifestCatalog = manifestCatalog;
            Rules = Array.AsReadOnly((LoadoutSlotRule[])rules.Clone());
            Containers = Array.AsReadOnly((LoadoutCodecContainer[])containers.Clone());
        }
    }

    internal sealed class LoadoutCodecContainer
    {
        internal readonly LoadoutItemDefinition Definition;
        internal readonly IReadOnlyList<Vector2Int> Cells;
        internal readonly Sprite Icon;
        private readonly ItemDisplayData fallback;
        internal LoadoutCodecContainer(LoadoutItemDefinition definition, IEnumerable<Vector2Int> cells, ItemDisplayData fallback)
        {
            Definition = definition; Cells = new List<Vector2Int>(cells).AsReadOnly();
            this.fallback = fallback; Icon = definition != null ? definition.Icon : fallback?.Icon;
        }
        internal ItemDisplayData CreateBody(string id, BackpackContainerData storage) => new ItemDisplayData(id,
            Vector2Int.zero, fallback.OriginalSize, fallback.Icon, name: fallback.Name, containerType: fallback.ContainerType,
            itemType: fallback.ItemType, equipmentType: fallback.EquipmentType, storage: storage);
    }

    /// <summary>网络入口采集的候选槽位；变更标记供 codec 预测提交后的端点版本。</summary>
    internal sealed class LoadoutCaptureSlot
    {
        internal int Slot, GridRevision;
        internal ContainerSnapshot Snapshot;
        internal IReadOnlyList<ItemDisplayData> Items;
        internal bool BodyChanged, GridChanged;
    }

    /// <summary>角色状态协议和物品重建；只接受已采集状态及明确配置，不访问网络或角色生命周期。</summary>
    internal sealed class LoadoutStateCodec
    {
        internal const int Schema = 5, LegacyStateSchema = 4, MaxWireBytes = 49152, MaxItems = 256, MaxCells = 1024;
        private const int MaxIdLength = 160;
        private readonly LoadoutCodecConfiguration configuration;
        internal LoadoutStateCodec(LoadoutCodecConfiguration configuration) { this.configuration = configuration; }

        internal bool TryCapture(IReadOnlyList<LoadoutCaptureSlot> inputs,
            IReadOnlyDictionary<BackpackContainerData, IReadOnlyList<ItemDisplayData>> storageCandidates,
            string epoch, long revision, bool accepting, LoadoutSlotId selectedWeapon, out string json, out string error)
        {
            json = null; error = null;
            var slots = new SlotWire[10]; var storages = new Dictionary<string, StorageWire>(StringComparer.Ordinal);
            foreach (LoadoutCaptureSlot input in inputs)
            {
                int i = input.Slot;
                ContainerSnapshot snapshot = input.Snapshot;
                IReadOnlyList<ItemDisplayData> slotItems = input.Items;
                if (i < 0 || i >= slots.Length || slots[i] != null || slotItems == null || slotItems.Count > 1)
                    return Fail("槽位候选无效或重复。", out error);
                ItemDisplayData item = slotItems.Count == 0 ? null : slotItems[0];
                if (!EncodeItem(item, out ItemWire encoded, out error)) return false;
                int gridVersion = input.GridRevision;
                if (input.BodyChanged && snapshot.Revision == int.MaxValue)
                    return Fail("槽位版本已达到上限。", out error);
                if (i < 4 && (input.GridChanged || input.BodyChanged))
                { if (gridVersion == int.MaxValue) return Fail("容器版本已达到上限。", out error); gridVersion++; }
                // JsonUtility 的内联对象不能保留 null；占用状态由 hasItem 明确表示。
                slots[i] = new SlotWire { slot = i, id = snapshot.Id, revision = snapshot.Revision + (input.BodyChanged ? 1 : 0),
                    hasItem = item != null, item = encoded ?? new ItemWire(), storageRevision = gridVersion };
                if (item?.Storage != null)
                {
                    var storage = item.Storage; var items = new List<ItemWire>();
                    IReadOnlyList<ItemDisplayData> contents = storage.Items;
                    if (storageCandidates != null && storageCandidates.TryGetValue(storage, out IReadOnlyList<ItemDisplayData> replacement)) contents = replacement;
                    foreach (ItemDisplayData stored in contents)
                    { if (!EncodeItem(stored, out ItemWire value, out error)) return false; items.Add(value); }
                    if (storages.ContainsKey(storage.Id)) return Fail("重复储物实例。", out error);
                    storages.Add(storage.Id, new StorageWire { id = storage.Id, kind = (int)storage.Kind,
                        cells = new List<Vector2Int>(storage.ValidCells).ToArray(), items = items.ToArray() });
                }
            }
            foreach (SlotWire slot in slots) if (slot == null) return Fail("角色槽位候选不完整。", out error);
            var state = new StateWire { configuration = configuration.Signature, epoch = epoch, revision = revision,
                accepting = accepting, selectedWeapon = (int)selectedWeapon, slots = slots, storages = new List<StorageWire>(storages.Values).ToArray() };
            json = JsonUtility.ToJson(state);
            // 预留版本增长和 true -> false 的字节，确保暂停时仍能发送完整快照。
            int maximumBytes = Encoding.UTF8.GetByteCount(json) + 19 - state.revision.ToString().Length + (state.accepting ? 1 : 0);
            foreach (SlotWire slot in slots)
                maximumBytes += 10 - slot.revision.ToString().Length + 10 - slot.storageRevision.ToString().Length;
            if (maximumBytes > MaxWireBytes) return Fail("库存快照超过协议容量。", out error);
            return TryReadState(json, out _, out _, out error);
        }

        internal bool EncodeItem(ItemDisplayData item, out ItemWire wire, out string error)
        {
            wire = null; error = null; if (item == null) return true;
            string definition = item.DefinitionId;
            if (string.IsNullOrEmpty(definition) && item.IsContainer) definition = "$container/" + (int)item.ContainerType;
            if (string.IsNullOrEmpty(definition)) return Fail("物品缺少已登记的稳定定义编号。", out error);
            wire = new ItemWire { id = item.Id, definition = definition, storage = item.Storage?.Id, origin = item.Origin, count = item.Count, rotated = item.Rotated };
            if (item.RuntimeItem != null)
            {
                if (item.RuntimeItem.StackCount != item.Count || !item.RuntimeItem.ItemId.IsValid
                    || !RuntimeItemSnapshotCodec.TryCapture(item.RuntimeItem, out RuntimeItemSnapshot snapshot, out error)
                    || !RuntimeItemSnapshotCodec.TryEncodeSnapshot(snapshot, out byte[] bytes, out error)) return false;
                wire.runtimeItemId = item.RuntimeItem.ItemId.Value; wire.runtimeState = Convert.ToBase64String(bytes);
                wire.runtimeRevision = item.RuntimeItem.Revision; wire.structureRevision = item.RuntimeItem.StructureRevision;
            }
            return true;
        }

        internal bool TryReadState(string json, out StateWire state, out Candidate candidate, out string error)
        {
            state = null; candidate = null; error = null;
            if (!Bounded(json, MaxWireBytes)) return Fail("库存快照长度无效。", out error);
            try { state = JsonUtility.FromJson<StateWire>(json); }
            catch (Exception) { return Fail("库存快照无法解析。", out error); }
            if (state == null || state.schema != Schema && state.schema != LegacyStateSchema || state.configuration != configuration.Signature || !ValidToken(state.epoch)
                || !IsWeaponSlot((LoadoutSlotId)state.selectedWeapon)
                || state.revision < 0 || state.slots == null || state.slots.Length != 10 || state.storages == null || state.storages.Length > 4)
                return Fail("库存协议、配置或角色版本不匹配。", out error);
            var built = new Candidate { selectedWeapon = (LoadoutSlotId)state.selectedWeapon }; var ids = new HashSet<string>(StringComparer.Ordinal); int itemCount = 0;
            foreach (StorageWire storage in state.storages)
            {
                if (storage == null || !ValidId(storage.id) || !ids.Add(storage.id) || storage.kind < 1 || storage.kind > 4
                    || storage.cells == null || storage.cells.Length > MaxCells || storage.items == null
                    || (itemCount += storage.items.Length) > MaxItems) return Fail("储物实例无效或超过协议上限。", out error);
                LoadoutCodecContainer config = ContainerConfiguration((ContainerKind)storage.kind);
                if (config == null || storage.cells.Length == 0 || !SameCells(config.Cells, storage.cells)) return Fail("储物形状与本地配置不匹配。", out error);
                var contents = new List<ItemDisplayData>();
                foreach (ItemWire wire in storage.items)
                {
                    if (wire == null || !string.IsNullOrEmpty(wire.storage) || !DecodeItem(wire, built.storages, ids, out ItemDisplayData item, out error)
                        || item.IsContainer) return Fail(error ?? "储物内容不允许嵌套容器。", out error);
                    contents.Add(item);
                }
                if (!BackpackContainerData.TryCreate(storage.id, (ContainerKind)storage.kind,
                    config.Icon,
                    storage.cells, contents, out BackpackContainerData data, out error)) return false;
                built.storages.Add(storage.id, data);
            }
            var usedStorage = new HashSet<string>(StringComparer.Ordinal);
            foreach (SlotWire slot in state.slots)
            {
                if (slot == null || slot.slot < 0 || slot.slot > 9 || built.slots[slot.slot] != null
                    || !ValidId(slot.id) || !ids.Add(slot.id) || slot.revision < 0 || slot.storageRevision < 0)
                    return Fail("槽位身份或版本无效。", out error);
                ItemDisplayData item = null;
                if (slot.hasItem)
                {
                    if (slot.item == null) return Fail("槽位物品数据缺失。", out error);
                    if (++itemCount > MaxItems || slot.item.origin != Vector2Int.zero)
                        return Fail("槽位物品数量或位置无效。", out error);
                    if (!DecodeItem(slot.item, built.storages, ids, out item, out error)) return false;
                    if (item.Storage != null && !usedStorage.Add(item.Storage.Id)) return Fail("容器物品共享了储物实例。", out error);
                }
                LoadoutSlotRule rule = configuration.Rules[slot.slot];
                var snapshot = new ContainerSnapshot(slot.id, item?.ContainerType ?? ContainerKind.None, item?.Icon,
                    eventSource: new object(), isSlot: true, rule: rule, rejectContainers: false);
                if (!LoadoutOperations.Validate(snapshot, item == null ? Array.Empty<ItemDisplayData>() : new[] { item }, out error)) return false;
                built.slots[slot.slot] = slot; built.items[slot.slot] = item;
            }
            if (usedStorage.Count != built.storages.Count) return Fail("存在未被容器物品引用的储物实例。", out error);
            candidate = built; return true;
        }

        internal bool DecodeItem(ItemWire wire, Dictionary<string, BackpackContainerData> storages, HashSet<string> ids,
            out ItemDisplayData item, out string error)
        {
            item = null; error = null;
            if (!ValidId(wire.id) || !ids.Add(wire.id) || !ValidId(wire.definition) || wire.count < 1
                || !BackpackContainerData.IsSupportedCoordinate(wire.origin))
                return Fail("物品实例数据或位置无效；列坐标必须在 0 到 4 之间。", out error);
            BackpackContainerData storage = null;
            if (!string.IsNullOrEmpty(wire.storage) && !storages.TryGetValue(wire.storage, out storage)) return Fail("物品储物关联无效。", out error);
            if (configuration.Catalog.TryGetValue(wire.definition, out LoadoutItemDefinition definition))
            {
                if (!TryRestoreRuntime(wire, definition, ids, out RuntimeItem runtime, out error)) return false;
                item = definition.Create(wire.id, wire.origin, wire.count, storage, runtime, createRuntime: false);
            }
            else
            {
                LoadoutCodecContainer fallback = null;
                for (int kind = 1; kind <= 4; kind++)
                    if (wire.definition == "$container/" + kind) fallback = ContainerConfiguration((ContainerKind)kind);
                if (fallback == null || fallback.Definition != null || wire.count != 1 || wire.runtimeItemId != 0 || !string.IsNullOrEmpty(wire.runtimeState)) return Fail("未知物品定义编号。", out error);
                item = fallback.CreateBody(wire.id, storage).At(wire.origin);
            }
            item = item.WithRotation(wire.rotated);
            if (item.IsContainer != (storage != null) || (storage != null && storage.Kind != item.ContainerType))
                return Fail("容器物品与储物实例类别不一致。", out error);
            return true;
        }

        private bool TryRestoreRuntime(ItemWire wire, LoadoutItemDefinition definition, HashSet<string> identities,
            out RuntimeItem runtime, out string error)
        {
            runtime = null; error = null;
            if (definition.Manifest == null)
                return wire.runtimeItemId == 0 && string.IsNullOrEmpty(wire.runtimeState)
                    || Fail("纯显示定义不接受运行物品载荷。", out error);
            if (wire.runtimeItemId == 0 || string.IsNullOrEmpty(wire.runtimeState) || wire.runtimeRevision < 0 || wire.structureRevision < 0
                || configuration.ManifestCatalog == null || !configuration.ManifestCatalog.TryGet(definition.Manifest.ItemType, out ItemManifest registered)
                || registered != definition.Manifest) return Fail("运行物品缺少授权清单、身份或完整状态。", out error);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(wire.runtimeState); }
            catch (FormatException) { return Fail("运行物品状态编码无效。", out error); }
            if (!RuntimeItemSnapshotCodec.TryDecodeSnapshot(new ItemId(wire.runtimeItemId), definition.Manifest.ItemType, wire.count,
                    bytes, out RuntimeItemSnapshot snapshot, out error)
                || !registered.TryRestoreRuntimeItem(snapshot, configuration.ManifestCatalog, out runtime, out error)) return false;
            foreach (RuntimeItem node in ItemAssemblyTraversal.Enumerate(runtime))
                if (!node.ItemId.IsValid || !identities.Add("\0runtime/" + node.ItemId.Value))
                    return Fail("完整库存含重复或无效运行物品身份。", out error);
            runtime.RestoreRevisions(wire.runtimeRevision, wire.structureRevision);
            if (wire.id != "runtime/" + wire.runtimeItemId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return Fail("运行物品字符串身份不匹配。", out error);
            return true;
        }
        private LoadoutCodecContainer ContainerConfiguration(ContainerKind kind) => configuration.Containers[(int)kind - 1];
        private static bool SameCells(IReadOnlyList<Vector2Int> first, Vector2Int[] second)
        { return first.Count == second.Length && new HashSet<Vector2Int>(first).SetEquals(second) && new HashSet<Vector2Int>(second).Count == second.Length; }
        private static bool IsWeaponSlot(LoadoutSlotId slot) => slot == LoadoutSlotId.PrimaryWeapon1
            || slot == LoadoutSlotId.PrimaryWeapon2 || slot == LoadoutSlotId.SecondaryWeapon || slot == LoadoutSlotId.MeleeWeapon;
        internal static bool ValidId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength) return false;
            foreach (char character in id) if (char.IsControl(character)) return false;
            return true;
        }
        internal static bool ValidToken(string token) => token != null && token.Length == 32 && Guid.TryParseExact(token, "N", out _);
        internal static bool Bounded(string value, int max) => !string.IsNullOrEmpty(value) && value.Length <= max && Encoding.UTF8.GetByteCount(value) <= max;
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
