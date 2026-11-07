using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mirror;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Runtime;

namespace RPG4.Backpack.Network
{
    /// <summary>复用角色已有武器槽；完整物品树仅定向给 owner，公共同步只有模板与父槽关系。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BackpackNetworkInventory), typeof(EquipmentInventory))]
    public sealed class BackpackNetworkEquipment : NetworkBehaviour, IBackpackView, IInventoryOperations, IInventoryCommitParticipant
    {
        [SyncVar(hook = nameof(OnAppearanceIdChanged))] private ulong publicItemId;
        [SyncVar(hook = nameof(OnAppearanceChanged))] private string definitionId;
        [SyncVar(hook = nameof(OnAppearanceChanged))] private string appearanceTree;
        private sealed class Stream { internal readonly InventorySyncList Entries = new InventorySyncList(); }
        private readonly Stream stream = new Stream();
        private readonly InventoryNetworkProjection projection = new InventoryNetworkProjection();
        private readonly InventoryNetworkMirror mirrorProjection = new InventoryNetworkMirror();
        private readonly Dictionary<ulong, RuntimeItem> mirror = new Dictionary<ulong, RuntimeItem>();
        private readonly Dictionary<ulong, InventoryPlacement> placements = new Dictionary<ulong, InventoryPlacement>();
        private EquipmentInventory local;
        private BackpackNetworkInventory ingress;
        private IInventoryCommitParticipant previousParticipant;
        private Action<InventoryOperation> previousDispatcher;
        private Action<AssemblyQuery> previousAssemblyQuery;
        private Action<AssemblyOperation> previousAssemblyOperation;
        private bool baselineSent, mirrorReady, mirrorDirty, appearanceDirty;
        private sealed class AppearanceValues : IItemRandomSource
        {
            internal static readonly AppearanceValues Instance = new AppearanceValues();
            public float Range(float minInclusive, float maxInclusive) => minInclusive;
        }
        private RuntimeItem appearanceItem;
        private NetworkConnectionToClient baselineOwner;
        public event Action Changed;
        public event Action<RuntimeItem> AppearanceChanged;
        public RuntimeItem AppearanceItem => appearanceItem;
        public ItemId PublicItemId => new ItemId(publicItemId);
        public string DefinitionId => definitionId;
        public string MuzzleDefinitionId => (isServer ? local?.EquippedItem : appearanceItem)?.Assembly.GetChild(ItemAssemblySlots.Muzzle)?.ItemType.Name;
        public InventoryHostCore AuthorityCore => isServer && local != null ? local.AuthorityCore : null;
        public bool IsReady => isServer || isOwned && mirrorReady;
        public int Rows => 1;
        public int Columns => 1;
        public int ConfiguredRows => 1;
        public int ConfiguredColumns => 1;
        public object EventSource => isServer ? (object)local.Container : this;
        public RuntimeItem EquippedItem
        {
            get
            {
                if (isServer) return local.EquippedItem;
                foreach (RuntimeItem item in mirror.Values) return item;
                return null;
            }
        }

        private void Awake()
        {
            ingress = GetComponent<BackpackNetworkInventory>();
            local = GetComponent<EquipmentInventory>();
            syncDirection = SyncDirection.ServerToClient;
            stream.Entries.Callback += OnEntryChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            local.SetNetworkAuthority(true);
            previousParticipant = local.CommitParticipant;
            previousDispatcher = local.AuthorityCore.AuthorityOperationDispatcher;
            previousAssemblyQuery = local.AuthorityCore.AuthorityAssemblyQueryDispatcher;
            previousAssemblyOperation = local.AuthorityCore.AuthorityAssemblyOperationDispatcher;
            local.CommitParticipant = InventoryCommitParticipants.Combine(previousParticipant, this);
            projection.Bind(local.Container, stream.Entries);
            local.AuthorityCore.AuthorityOperationDispatcher = operation => ingress.SubmitEquipmentOperation(this, operation);
            local.AuthorityCore.AuthorityAssemblyQueryDispatcher = ingress.SubmitEquipmentAssemblyQuery;
            local.AuthorityCore.AuthorityAssemblyOperationDispatcher = ingress.SubmitEquipmentAssemblyOperation;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (!isServer) local.SetNetworkAuthority(false);
            if (!isServer && isOwned) BindOwnerCore();
            ingress.CatalogChanged += OnCatalogChanged;
            appearanceDirty = true;
        }

        private void BindOwnerCore()
        {
            if (clientCore != null) return;
            clientCore = new InventoryHostCore(LogModule.Network, name);
            clientCore.IsAuthority = () => false;
            clientCore.ForwardCanSubmit = CanSubmitTo;
            clientCore.ForwardOperation = operation => ingress.SubmitEquipmentOperation(this, operation);
            clientCore.ForwardAssemblyQuery = ingress.SubmitEquipmentAssemblyQuery;
            clientCore.ForwardAssemblyOperation = ingress.SubmitEquipmentAssemblyOperation;
            clientCore.SubscribeCommands(this);
        }

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();
            if (!isServer) BindOwnerCore();
        }

        public override void OnStopAuthority()
        {
            if (!isServer)
            {
                clientCore?.UnsubscribeCommands(); clientCore = null;
                stream.Entries.Reset(); mirror.Clear(); placements.Clear(); mirrorProjection.Clear();
                mirrorReady = false; mirrorDirty = false;
                Changed?.Invoke(); EventBus.Global.Publish(InvChannels.Changed, this);
            }
            base.OnStopAuthority();
        }

        private void OnCatalogChanged(ItemManifestCatalog catalog)
        {
            mirrorProjection.RequestFullRefresh(true);
            mirrorDirty = isOwned;
            appearanceDirty = true;
        }

        private InventoryHostCore clientCore;
        internal void PublishResult(InventoryOperationResult result) => (isServer ? local.AuthorityCore : clientCore)?.PublishOperationResult(result);
        internal void PublishAssemblyInspection(AssemblyInspectionSnapshot value) => (isServer ? local.AuthorityCore : clientCore)?.PublishAssemblyInspection(value);
        internal void PublishAssemblyResult(AssemblyOperationResult value) => (isServer ? local.AuthorityCore : clientCore)?.PublishAssemblyOperationResult(value);

        public override void OnStopClient()
        {
            ingress.CatalogChanged -= OnCatalogChanged;
            local.SetNetworkAuthority(true);
            clientCore?.UnsubscribeCommands(); clientCore = null;
            mirror.Clear(); placements.Clear(); mirrorProjection.Clear();
            mirrorReady = false; baselineSent = false;
            appearanceItem = null;
            AppearanceChanged?.Invoke(null); Changed?.Invoke();
            base.OnStopClient();
        }

        public override void OnStopServer()
        {
            if (local != null)
            {
                local.CommitParticipant = previousParticipant;
                if (local.AuthorityCore != null) {
                    local.AuthorityCore.AuthorityOperationDispatcher = previousDispatcher;
                    local.AuthorityCore.AuthorityAssemblyQueryDispatcher = previousAssemblyQuery;
                    local.AuthorityCore.AuthorityAssemblyOperationDispatcher = previousAssemblyOperation;
                }
                local.SetNetworkAuthority(true);
            }
            projection.Clear(); stream.Entries.Reset(); baselineSent = false; baselineOwner = null;
            publicItemId = 0; definitionId = appearanceTree = string.Empty; appearanceItem = null; appearanceDirty = false;
            AppearanceChanged?.Invoke(null);
            base.OnStopServer();
        }

        public bool TryPrepare(ItemId itemId, out IInventoryPreparedCommit prepared, out string error)
        {
            prepared = null;
            try
            {
                RuntimeItem equipped = local.EquippedItem;
                if (equipped != null && !projection.TryValidateRestoration(equipped, ingress.Catalog, out error)) return false;
                return projection.TryPrepareContainer(local.Container, stream.Entries, itemId, out prepared, out _, out error);
            }
            catch { projection.Invalidate(); throw; }
        }

        private bool SyncProjection(out string error)
        {
            RuntimeItem equipped = local.EquippedItem;
            if (equipped != null && !projection.TryValidateRestoration(equipped, ingress.Catalog, out error)) return false;
            projection.SyncContainer(local.Container, stream.Entries, out ProjectionSyncReport report);
            error = report.FirstError;
            return report.Rejected == 0;
        }

        private void LateUpdate()
        {
            if (isServer)
            {
                if (!SyncProjection(out _)) return;
                RuntimeItem item = local.EquippedItem;
                publicItemId = item?.ItemId.Value ?? 0;
                definitionId = item?.ItemType.Name ?? string.Empty;
                appearanceTree = CaptureAppearance(item);
                NetworkConnectionToClient conn = connectionToClient;
                bool ownerVisible = conn != null && conn.isReady && netIdentity.observers.ContainsKey(conn.connectionId);
                if (conn != baselineOwner || !ownerVisible)
                {
                    baselineSent = false;
                    baselineOwner = conn;
                }
                if (ownerVisible)
                {
                    using (NetworkWriterPooled writer = NetworkWriterPool.Get())
                    {
                        if (!baselineSent)
                        {
                            stream.Entries.ClearChanges();
                            stream.Entries.OnSerializeAll(writer);
                        }
                        else stream.Entries.OnSerializeDelta(writer);
                        if (!baselineSent || writer.Position > sizeof(uint))
                            TargetEquipmentContents(conn, !baselineSent, writer.ToArray());
                    }
                    baselineSent = true;
                }
                stream.Entries.ClearChanges();
            }
            if (!isServer && mirrorDirty)
            {
                mirrorDirty = false;
                mirrorReady = mirrorProjection.TryApply(stream.Entries, ingress.Catalog, 1, 1, projection, mirror, placements, out _);
                Changed?.Invoke();
                EventBus.Global.Publish(InvChannels.Changed, this);
            }
            if (appearanceDirty) RebuildAppearance();
        }

        [TargetRpc]
        private void TargetEquipmentContents(NetworkConnectionToClient target, bool baseline, byte[] payload)
        {
            if (isServer || !isOwned) return;
            using (NetworkReaderPooled reader = NetworkReaderPool.Get(payload))
            {
                if (baseline) { stream.Entries.OnDeserializeAll(reader); mirrorProjection.RequestFullRefresh(true); }
                else stream.Entries.OnDeserializeDelta(reader);
            }
            mirrorDirty = true;
        }

        private void OnEntryChanged(SyncList<NetworkInventoryEntry>.Operation op, int index, NetworkInventoryEntry oldItem, NetworkInventoryEntry newItem)
        {
            if (isServer) { Changed?.Invoke(); return; }
            switch (op)
            {
                case SyncList<NetworkInventoryEntry>.Operation.OP_ADD:
                case SyncList<NetworkInventoryEntry>.Operation.OP_INSERT: mirrorProjection.Added(newItem); break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_SET: mirrorProjection.Replaced(oldItem, newItem); break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_REMOVEAT: mirrorProjection.Removed(oldItem); break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_CLEAR: mirrorProjection.RequestFullRefresh(); break;
            }
        }

        private void OnAppearanceChanged(string oldValue, string newValue) => appearanceDirty = true;
        private void OnAppearanceIdChanged(ulong oldValue, ulong newValue) => appearanceDirty = true;

        private const int MaxAppearanceBytes = 32768;
        private static void WriteAppearanceText(BinaryWriter writer, string value, int limit)
        {
            if (string.IsNullOrEmpty(value) || value.Length > limit) throw new InvalidDataException();
            byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        private static string ReadAppearanceText(BinaryReader reader, int limit)
        {
            int length = reader.ReadUInt16();
            if (length < 1 || length > limit * 4 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException();
            string value = new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
            if (value.Length > limit) throw new InvalidDataException();
            return value;
        }
        private static string CaptureAppearance(RuntimeItem root)
        {
            if (root == null) return string.Empty;
            try {
                // The traversal validates cycles, depth and all 32 instance identities before encoding.
                var nodes = new List<RuntimeItem>(ItemAssemblyTraversal.Enumerate(root));
                using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
                    writer.Write((byte)1); writer.Write((byte)nodes.Count);
                    for (int i = 0; i < nodes.Count; i++) {
                        RuntimeItem node = nodes[i]; int parentIndex = -1; string slotId = null;
                        for (int j = 0; j < i && parentIndex < 0; j++)
                            foreach (var child in nodes[j].Assembly.InstalledChildren)
                                if (ReferenceEquals(child.Value, node)) { parentIndex = j; slotId = child.Key; break; }
                        writer.Write((sbyte)parentIndex);
                        if (i != 0) WriteAppearanceText(writer, slotId, ItemAssemblySlots.MaxSlotIdLength);
                        WriteAppearanceText(writer, node.ItemType.Name, RuntimeItemSnapshot.MaxItemTypeLength);
                    }
                    if (stream.Length > MaxAppearanceBytes) throw new InvalidDataException();
                    return Convert.ToBase64String(stream.ToArray());
                }
            }
            catch (Exception) { return string.Empty; }
        }
        private void RebuildAppearance()
        {
            appearanceDirty = false;
            appearanceItem = null;
            try {
                ItemManifestCatalog catalog = ingress.Catalog;
                if (publicItemId == 0 || string.IsNullOrEmpty(definitionId) || string.IsNullOrEmpty(appearanceTree) || catalog == null)
                { AppearanceChanged?.Invoke(null); return; }
                if (appearanceTree.Length > (MaxAppearanceBytes + 2) / 3 * 4) throw new InvalidDataException();
                byte[] bytes = Convert.FromBase64String(appearanceTree);
                if (bytes.Length > MaxAppearanceBytes) throw new InvalidDataException();
                using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) {
                    if (reader.ReadByte() != 1) throw new InvalidDataException();
                    int count = reader.ReadByte(); if (count < 1 || count > RuntimeItemSnapshot.MaxTreeItems) throw new InvalidDataException();
                    var parents = new int[count]; var depths = new int[count]; var slots = new string[count];
                    var manifests = new ItemManifest[count]; var snapshots = new RuntimeItemSnapshot[count];
                    var children = new List<InstalledItemSnapshot>[count]; var slotSets = new HashSet<string>[count];
                    var ids = new HashSet<ulong> { publicItemId }; ulong nextId = ulong.MaxValue;
                    for (int i = 0; i < count; i++) {
                        parents[i] = reader.ReadSByte();
                        if (i == 0 ? parents[i] != -1 : parents[i] < 0 || parents[i] >= i) throw new InvalidDataException();
                        if (i != 0) {
                            slots[i] = ReadAppearanceText(reader, ItemAssemblySlots.MaxSlotIdLength);
                            depths[i] = depths[parents[i]] + 1;
                            if (depths[i] > RuntimeItemSnapshot.MaxDepth || !slotSets[parents[i]].Add(slots[i])) throw new InvalidDataException();
                        }
                        string type = ReadAppearanceText(reader, RuntimeItemSnapshot.MaxItemTypeLength);
                        if (i == 0 && !string.Equals(type, definitionId, StringComparison.Ordinal)) throw new InvalidDataException();
                        var tag = new BackpackTag(type);
                        if (!catalog.TryGet(tag, out ItemManifest manifest)) throw new InvalidDataException();
                        ulong id = publicItemId;
                        if (i != 0) { while (nextId == 0 || ids.Contains(nextId)) nextId--; id = nextId--; ids.Add(id); }
                        if (!manifest.TryCreateRuntimeItem(AppearanceValues.Instance, new ItemId(id), out RuntimeItem template, out _)
                            || !RuntimeItemSnapshotCodec.TryCapture(template, out RuntimeItemSnapshot basic, out _)) throw new InvalidDataException();
                        snapshots[i] = basic; manifests[i] = manifest; children[i] = new List<InstalledItemSnapshot>(); slotSets[i] = new HashSet<string>(StringComparer.Ordinal);
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException();
                    for (int i = count - 1; i >= 0; i--) {
                        RuntimeItemSnapshot basic = snapshots[i];
                        var numbers = new FragmentNumberState[basic.Numbers.Count]; for (int j = 0; j < numbers.Length; j++) numbers[j] = basic.Numbers[j];
                        WeaponStateSnapshot weapon = basic.Weapon == null ? null : new WeaponStateSnapshot(basic.Weapon.Durability, default, 0);
                        MagazineStateSnapshot magazine = basic.Magazine == null ? null : new MagazineStateSnapshot(Array.Empty<BackpackTag>());
                        snapshots[i] = new RuntimeItemSnapshot(basic.ItemId, basic.ItemType, 1, numbers, weapon, magazine, children[i]);
                        if (i != 0) children[parents[i]].Add(new InstalledItemSnapshot(slots[i], snapshots[i]));
                    }
                    if (!manifests[0].TryRestoreRuntimeItem(snapshots[0], catalog, out appearanceItem, out _)) throw new InvalidDataException();
                }
            }
            catch (Exception) { appearanceItem = null; }
            AppearanceChanged?.Invoke(appearanceItem);
        }

        public bool CanSubmitTo(IBackpackView destination) => isServer ? local.CanSubmitTo(destination)
            : isOwned && IsReady && ingress.CanSubmitEquipmentTo(destination);
        public bool HasEmptySpace(GridCoord dimensions) => IsReady && EquippedItem == null;
        public GridCoord ResolveItemDimensions(RuntimeItem item) => GridCoord.One;
        public bool CanAcceptItem(RuntimeItem item, out string error) => local.CanAcceptItem(item, out error);
        public void CopyItems(List<RuntimeItem> destination)
        {
            destination.Clear();
            if (EquippedItem != null) destination.Add(EquippedItem);
        }
        public bool TryGetPlacement(ItemId itemId, out InventoryPlacement placement)
        {
            if (isServer) return local.TryGetPlacement(itemId, out placement);
            return placements.TryGetValue(itemId.Value, out placement);
        }
    }
}
