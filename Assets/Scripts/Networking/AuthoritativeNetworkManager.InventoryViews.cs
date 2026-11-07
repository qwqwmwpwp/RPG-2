using RPG4.Raid.Domain;
using System;
using System.Collections.Generic;
using System.IO;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Networking.Contracts;

namespace RPG4.Networking
{
    internal sealed class RaidInventoryView : IBackpackView, IInventoryOperations, IDisposable
    {
        internal InventoryHostCore Core { get; private set; }
        internal PersonalContainerKind Kind;
        internal bool Writable;
        internal Action<RaidInventoryView, InventoryOperation> Submit;
        internal Action<RaidInventoryView, AssemblyQuery> SubmitAssemblyQuery;
        internal Action<RaidInventoryView, AssemblyOperation> SubmitAssemblyOperation;
        public bool IsReady => Core != null;
        public int Rows => Core?.Container.Rows ?? 0;
        public int Columns => Core?.Container.Columns ?? 0;
        public int ConfiguredRows => Rows;
        public int ConfiguredColumns => Columns;
        public object EventSource => this;
        public GridCoord ResolveItemDimensions(RuntimeItem item) => Kind == PersonalContainerKind.Equipment ? GridCoord.One : InventoryContainer.GetItemDimensions(item);
        public bool CanAcceptItem(RuntimeItem item, out string error)
        {
            error = null;
            if (item == null) { error = "物品为空。"; return false; }
            if (Kind != PersonalContainerKind.Equipment) return true;
            if (item.StackCount != 1 || item.GetFragment<EquipmentFragment>() == null || item.GetFragment<WeaponDefinitionFragment>() == null)
            { error = "武器槽只能放入一件有效武器。"; return false; }
            try { item.GetFragment<WeaponDefinitionFragment>().Validate(); return true; }
            catch (Exception) { error = "武器定义无效。"; return false; }
        }
        internal void Apply(PersistentContainerData data, ItemManifestCatalog catalog, ItemIdGenerator generator, bool authority)
        {
            if (data == null || data.Kind != Kind) throw new InvalidDataException("Inventory kind mismatch.");
            Core?.UnsubscribeCommands();
            Core = new InventoryHostCore(data.Rows, data.Columns, LogModule.Network, "Persistent inventory", Kind == PersonalContainerKind.Equipment, CanAcceptItem)
            { Catalog = catalog, IdGeneratorProvider = () => generator, IsAuthority = () => authority,
                ForwardCanSubmit = destination => CanSubmitTo(destination), ForwardOperation = operation => Submit?.Invoke(this, operation) };
            Core.ForwardAssemblyQuery = query => SubmitAssemblyQuery?.Invoke(this, query);
            Core.ForwardAssemblyOperation = operation => SubmitAssemblyOperation?.Invoke(this, operation);
            RaidInventorySnapshots.Restore(data, Core.Container, catalog);
            Core.SubscribeCommands(this);
        }
        public bool CanSubmitTo(IBackpackView destination) => Writable && IsReady
            && (destination == null || destination is RaidInventoryView view && view.Writable && view.IsReady);
        public bool HasEmptySpace(GridCoord dimensions) => Core != null && Core.HasEmptySpace(dimensions);
        public bool TryGetPlacement(ItemId id, out InventoryPlacement placement)
        {
            placement = InventoryPlacement.Invalid;
            return Core != null && Core.Container.TryGetItemById(id, out RuntimeItem item) && Core.Container.TryGetPlacement(item, out placement);
        }
        public void CopyItems(List<RuntimeItem> destination)
        { if (Core == null) destination.Clear(); else Core.Container.FillItemsByItemId(destination); }
        internal void NotifyChanged()
        { EventBus.Global.Publish<ItemId[]>(InvChannels.ItemsChanged, this, null); EventBus.Global.Publish(InvChannels.Changed, this); }
        public void Dispose() { Core?.UnsubscribeCommands(); Core = null; }
    }

    internal static class RaidInventorySnapshots
    {
        internal static PersistentContainerData Capture(InventoryContainer container, PersonalContainerKind kind)
        {
            if (container == null) throw new InvalidOperationException("Inventory container is absent.");
            var result = new PersistentContainerData { Kind = kind, Rows = container.Rows, Columns = container.Columns };
            var items = new List<RuntimeItem>(); container.FillItemsByItemId(items);
            result.Items = new PersistentItemData[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                RuntimeItem item = items[i];
                if (!container.TryGetPlacement(item, out InventoryPlacement placement)
                    || !RuntimeItemSnapshotCodec.TryCapture(item, out RuntimeItemSnapshot snapshot, out string error)
                    || !RuntimeItemSnapshotCodec.TryEncodeSnapshot(snapshot, out byte[] payload, out error))
                    throw new InvalidOperationException("Unable to capture authoritative item.");
                result.Items[i] = new PersistentItemData { ItemId = item.ItemId.Value, ItemType = snapshot.ItemType.Name,
                    StackCount = item.StackCount, UpperLeftIndex = placement.UpperLeftIndex, Payload = payload };
            }
            return result;
        }
        internal static RuntimeItem RestoreItem(PersistentItemData data, ItemManifestCatalog catalog)
        {
            var tag = new BackpackTag(data.ItemType);
            if (catalog == null || !catalog.TryGet(tag, out ItemManifest manifest)
                || !RuntimeItemSnapshotCodec.TryDecodeSnapshot(new ItemId(data.ItemId), tag, data.StackCount, data.Payload, out RuntimeItemSnapshot snapshot, out string error)
                || !manifest.TryRestoreRuntimeItem(snapshot, catalog, out RuntimeItem item, out error))
                throw new InvalidDataException("Unable to restore persistent item.");
            return item;
        }
        internal static void Restore(PersistentContainerData data, InventoryContainer destination, ItemManifestCatalog catalog)
        {
            if (data == null || destination == null || destination.ItemCount != 0
                || destination.Rows != data.Rows || destination.Columns != data.Columns || data.Items == null)
                throw new InvalidDataException("Persistent container does not match destination.");
            foreach (PersistentItemData entry in data.Items)
                if (!destination.TryPlaceItem(RestoreItem(entry, catalog), entry.UpperLeftIndex, out string error))
                    throw new InvalidDataException(error);
        }
        internal static void Clear(InventoryContainer container)
        {
            var items = new List<RuntimeItem>(); container.FillItemsByItemId(items);
            foreach (RuntimeItem item in items)
                if (!container.TryRemoveItem(item, out string error)) throw new InvalidOperationException(error);
        }
    }
}
