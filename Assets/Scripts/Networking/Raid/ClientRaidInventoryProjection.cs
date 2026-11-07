using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Raid.Application.Client;
using RPG4.Raid.Domain;

namespace RPG4.Networking
{
    public interface IClientLobbyInventoryViews
    {
        bool IsReady { get; }
        IBackpackView Stash { get; }
        IBackpackView Backpack { get; }
        IBackpackView Equipment { get; }
    }

    /// <summary>Unity item restoration and UI event projection; owns no raid/request state.</summary>
    internal sealed class ClientRaidInventoryProjection : IClientRaidInventoryProjection, IClientLobbyInventoryViews
    {
        private readonly Func<ItemManifestCatalog> resolveCatalog;
        private readonly RaidInventoryView[] views = {
            new RaidInventoryView { Kind = PersonalContainerKind.Stash },
            new RaidInventoryView { Kind = PersonalContainerKind.Backpack },
            new RaidInventoryView { Kind = PersonalContainerKind.Equipment } };
        private ClientRaidFlow flow;
        public bool IsReady => views[0].IsReady;
        public IBackpackView Stash => views[0];
        public IBackpackView Backpack => views[1];
        public IBackpackView Equipment => views[2];

        internal ClientRaidInventoryProjection(Func<ItemManifestCatalog> resolveCatalog)
        { this.resolveCatalog = resolveCatalog; }

        internal void Bind(ClientRaidFlow owner)
        {
            flow = owner;
            foreach (RaidInventoryView view in views) view.Submit = Submit;
        }

        public void Apply(PersistentInventoryState inventory)
        {
            PersistentContainerData[] containers = { inventory.Stash, inventory.Backpack, inventory.Equipment };
            ItemManifestCatalog catalog = resolveCatalog();
            for (int i = 0; i < views.Length; i++)
            { views[i].Apply(containers[i], catalog, null, false); views[i].Writable = !inventory.ReadOnly; }
        }

        public void NotifyChanged()
        { foreach (RaidInventoryView view in views) view.NotifyChanged(); }

        public void PublishResult(PersonalContainerKind source, LobbyInventoryResult result)
        {
            if ((byte)source >= views.Length) return;
            views[(byte)source].Core?.PublishOperationResult(new InventoryOperationResult(result.RequestId,
                result.Success, new ItemId(result.ResultItemId), result.MovedCount, result.Error));
        }

        public void Clear()
        { foreach (RaidInventoryView view in views) { view.Dispose(); view.Writable = false; } }

        private void Submit(RaidInventoryView source, InventoryOperation operation)
        {
            RaidInventoryView destination = operation.Destination as RaidInventoryView ?? source;
            if (!source.CanSubmitTo(destination))
            {
                PublishResult(source.Kind, LobbyInventoryResult.Rejected(operation.RequestId, "库存当前只读或正在处理中。"));
                return;
            }
            flow.SubmitInventoryOperation(new LobbyCommand { RequestId = operation.RequestId,
                Source = (byte)source.Kind, Destination = (byte)destination.Kind, OperationKind = (byte)operation.Kind,
                SourceItemId = operation.SourceItemId.Value, TargetItemId = operation.TargetItemId.Value,
                Count = operation.Count, UpperLeftIndex = operation.UpperLeftIndex });
        }
    }
}
