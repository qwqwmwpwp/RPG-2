using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Runtime;

namespace RPG4.Backpack.Network
{
    /// <summary>复用角色已有武器槽；完整物品树仅定向给 owner，公共同步只有武器/枪口模板身份。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BackpackNetworkInventory), typeof(EquipmentInventory))]
    public sealed class BackpackNetworkEquipment : NetworkBehaviour, IBackpackView, IInventoryOperations
    {
        [SyncVar(hook = nameof(OnAppearanceIdChanged))] private ulong publicItemId;
        [SyncVar(hook = nameof(OnAppearanceChanged))] private string definitionId;
        [SyncVar(hook = nameof(OnAppearanceChanged))] private string muzzleDefinitionId;
        private sealed class Stream { internal readonly InventorySyncList Entries = new InventorySyncList(); }
        private readonly Stream stream = new Stream();
        private readonly InventoryNetworkProjection projection = new InventoryNetworkProjection();
        private readonly InventoryNetworkMirror mirrorProjection = new InventoryNetworkMirror();
        private readonly Dictionary<ulong, RuntimeItem> mirror = new Dictionary<ulong, RuntimeItem>();
        private readonly Dictionary<ulong, InventoryPlacement> placements = new Dictionary<ulong, InventoryPlacement>();
        private EquipmentInventory local;
        private BackpackNetworkInventory ingress;
        private CommitConfirmHandler previousConfirm;
        private Action<InventoryOperation> previousDispatcher;
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
        public string MuzzleDefinitionId => muzzleDefinitionId;
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
            previousConfirm = local.CommitConfirm;
            previousDispatcher = local.AuthorityCore.AuthorityOperationDispatcher;
            local.CommitConfirm = Confirm;
            projection.Bind(local.Container, stream.Entries);
            local.AuthorityCore.AuthorityOperationDispatcher = operation => ingress.SubmitEquipmentOperation(this, operation);
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
                local.CommitConfirm = previousConfirm;
                if (local.AuthorityCore != null) local.AuthorityCore.AuthorityOperationDispatcher = previousDispatcher;
                local.SetNetworkAuthority(true);
            }
            projection.Clear(); stream.Entries.Reset(); baselineSent = false; baselineOwner = null;
            base.OnStopServer();
        }

        private bool Confirm(ItemId itemId, out string error)
        {
            if (previousConfirm != null && !previousConfirm(itemId, out error)) return false;
            return SyncProjection(out error);
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
                muzzleDefinitionId = item?.WeaponState != null && item.WeaponState.Attachments.TryGetValue(WeaponAttachmentSlot.Muzzle, out RuntimeItem muzzle)
                    ? muzzle.ItemType.Name : string.Empty;
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

        private void RebuildAppearance()
        {
            appearanceDirty = false;
            appearanceItem = null;
            ItemManifestCatalog catalog = ingress.Catalog;
            if (publicItemId != 0 && !string.IsNullOrEmpty(definitionId) && catalog != null && catalog.TryGet(new BackpackTag(definitionId), out ItemManifest manifest)
                && manifest.TryCreateRuntimeItem(AppearanceValues.Instance, new ItemId(publicItemId), out RuntimeItem templateItem, out _))
            {
                // 本地默认模板数值仅用于模型；不复制服务器弹药、耐久或物品树。
                if (!RuntimeItemSnapshotCodec.TryCapture(templateItem, out RuntimeItemSnapshot basic, out _))
                { AppearanceChanged?.Invoke(null); return; }
                var attachments = new List<AttachmentStateSnapshot>();
                if (!string.IsNullOrEmpty(muzzleDefinitionId) && catalog.TryGet(new BackpackTag(muzzleDefinitionId), out ItemManifest muzzleManifest)
                    && muzzleManifest.TryCreateRuntimeItem(AppearanceValues.Instance, new ItemId(publicItemId == 2 ? 3UL : 2UL), out RuntimeItem muzzle, out _)
                    && RuntimeItemSnapshotCodec.TryCapture(muzzle, out RuntimeItemSnapshot muzzleSnapshot, out _))
                    attachments.Add(new AttachmentStateSnapshot(WeaponAttachmentSlot.Muzzle, muzzleSnapshot));
                var numbers = new FragmentNumberState[basic.Numbers.Count];
                for (int i = 0; i < numbers.Length; i++) numbers[i] = basic.Numbers[i];
                WeaponDefinitionFragment definition = templateItem.GetFragment<WeaponDefinitionFragment>();
                var snapshot = new RuntimeItemSnapshot(new ItemId(publicItemId), basic.ItemType, 1, numbers,
                    new WeaponStateSnapshot(definition != null ? definition.MaxDurability : 0f, default, 0, null, attachments), null);
                manifest.TryRestoreRuntimeItem(snapshot, catalog, out appearanceItem, out _);
            }
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
