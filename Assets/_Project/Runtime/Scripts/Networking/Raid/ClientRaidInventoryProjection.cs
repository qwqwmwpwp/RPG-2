using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
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
        internal event Action<byte, int> AssemblyRequestChanged;
        private ClientRaidFlow flow;
        private readonly Dictionary<(byte source, int session), AssemblyQuery> assemblyQueries = new Dictionary<(byte, int), AssemblyQuery>();
        private readonly Dictionary<(byte source, int session), AssemblyOperation> assemblyOperations = new Dictionary<(byte, int), AssemblyOperation>();
        public bool IsReady => views[0].IsReady;
        public IBackpackView Stash => views[0];
        public IBackpackView Backpack => views[1];
        public IBackpackView Equipment => views[2];

        internal ClientRaidInventoryProjection(Func<ItemManifestCatalog> resolveCatalog)
        { this.resolveCatalog = resolveCatalog; }

        internal void Bind(ClientRaidFlow owner)
        {
            flow = owner;
            foreach (RaidInventoryView view in views) { view.Submit = Submit; view.SubmitAssemblyQuery = SubmitAssemblyQuery; view.SubmitAssemblyOperation = SubmitAssemblyOperation; }
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
            if (result.AssemblyKind != 0) {
                if (result.AssemblyPayload != null) return; // The bounded response follows the inventory snapshot.
                foreach (var pair in new List<KeyValuePair<(byte source, int session), AssemblyQuery>>(assemblyQueries))
                    if (result.AssemblyKind == 1 && pair.Key.source == (byte)source && pair.Value.RequestId == result.RequestId) {
                        AssemblyQuery query = pair.Value;
                        views[(byte)source].Core?.PublishAssemblyInspection(new AssemblyInspectionSnapshot(null, null, null, null, null,
                            result.Error, -1, query.SessionId, query.RequestId, query.RootItemId, query.ParentItemId, query.SlotId, query.CandidateOffset));
                    }
                foreach (var pair in new List<KeyValuePair<(byte source, int session), AssemblyOperation>>(assemblyOperations))
                    if (result.AssemblyKind == 2 && pair.Key.source == (byte)source && pair.Value.RequestId == result.RequestId) {
                        AssemblyOperation operation = pair.Value; assemblyOperations.Remove(pair.Key);
                        views[(byte)source].Core?.PublishAssemblyOperationResult(new AssemblyOperationResult(operation.RequestId, operation.SessionId,
                            operation.RootItemId, operation.ParentItemId, operation.SlotId, false, result.Error));
                    }
                return;
            }
            views[(byte)source].Core?.PublishOperationResult(new InventoryOperationResult(result.RequestId,
                result.Success, new ItemId(result.ResultItemId), result.MovedCount, result.Error));
        }

        public void Clear()
        { foreach (RaidInventoryView view in views) { view.Dispose(); view.Writable = false; } assemblyQueries.Clear(); assemblyOperations.Clear(); }

        private void SubmitAssemblyQuery(RaidInventoryView source, AssemblyQuery query)
        {
            if (query.ParentItemId.IsNone && query.RootItemId.IsValid)
                query = new AssemblyQuery(query.RootItemId, query.RootItemId, query.SlotId, query.SessionId,
                    query.RequestId, query.CandidateOffset, query.CandidateLimit);
            var key = ((byte)source.Kind, query.SessionId);
            AssemblyRequestChanged?.Invoke((byte)source.Kind, query.SessionId);
            if (query.RootItemId.IsNone) { assemblyQueries.Remove(key); assemblyOperations.Remove(key); return; }
            if (!assemblyQueries.ContainsKey(key) && assemblyQueries.Count >= 32) return;
            if (assemblyQueries.TryGetValue(key, out AssemblyQuery previous) && (previous.RootItemId != query.RootItemId
                || previous.ParentItemId != query.ParentItemId || previous.SlotId != query.SlotId)) assemblyOperations.Remove(key);
            assemblyQueries[key] = query;
            flow.SubmitAssembly(new LobbyCommand { Kind = LobbyCommandKind.AssemblyQuery, Source = (byte)source.Kind,
                Destination = (byte)PersonalContainerKind.Backpack, RequestId = query.RequestId, AssemblySessionId = query.SessionId,
                RootItemId = query.RootItemId.Value, ParentItemId = query.ParentItemId.Value, SlotId = query.SlotId,
                CandidateOffset = query.CandidateOffset, CandidateLimit = query.CandidateLimit });
        }
        private void SubmitAssemblyOperation(RaidInventoryView source, AssemblyOperation operation)
        {
            var key = ((byte)source.Kind, operation.SessionId);
            assemblyOperations[key] = operation;
            flow.SubmitAssembly(new LobbyCommand { Kind = LobbyCommandKind.AssemblyOperation, Source = (byte)source.Kind,
                Destination = (byte)PersonalContainerKind.Backpack, RequestId = operation.RequestId, AssemblySessionId = operation.SessionId,
                RootItemId = operation.RootItemId.Value, ParentItemId = operation.ParentItemId.Value, SlotId = operation.SlotId,
                CandidateItemId = operation.CandidateItemId.Value, StructureRevision = operation.ExpectedStructureRevision });
        }
        internal bool AcceptsAssembly(byte source, byte kind, int session, int request, ulong root, ulong parent, string slot, int offset)
        {
            if (source >= views.Length || !views[source].IsReady) return false;
            var key = (source, session);
            if (kind == 1) return assemblyQueries.TryGetValue(key, out AssemblyQuery query) && query.RequestId == request
                && query.RootItemId.Value == root && query.ParentItemId.Value == parent && query.SlotId == slot && query.CandidateOffset == offset;
            return kind == 2 && assemblyOperations.TryGetValue(key, out AssemblyOperation operation) && operation.RequestId == request
                && operation.RootItemId.Value == root && operation.ParentItemId.Value == parent && operation.SlotId == slot;
        }
        internal void PublishAssembly(byte source, byte kind, byte[] payload)
        {
            if (source >= views.Length) return;
            if (kind == 1) {
                AssemblyInspectionSnapshot value = AssemblyWireCodec.DecodeInspection(payload, ResolveAssemblyItem);
                if (AcceptsAssembly(source, kind, value.SessionId, value.RequestId, value.RootItemId.Value, value.ParentItemId.Value, value.SlotId, value.CandidateOffset))
                    views[source].Core?.PublishAssemblyInspection(value);
            }
            else if (kind == 2) {
                AssemblyOperationResult value = AssemblyWireCodec.DecodeResult(payload);
                if (!AcceptsAssembly(source, kind, value.SessionId, value.RequestId, value.RootItemId.Value, value.ParentItemId.Value, value.SlotId, 0)) return;
                assemblyOperations.Remove((source, value.SessionId)); views[source].Core?.PublishAssemblyOperationResult(value);
            }
        }
        private WeaponUiItem ResolveAssemblyItem(ItemId id)
        {
            var items = new List<RuntimeItem>();
            foreach (RaidInventoryView view in views) {
                view.CopyItems(items);
                foreach (RuntimeItem root in items)
                    if (ItemAssemblyTraversal.TryFind(root, id, out RuntimeItem item)) return WeaponUiSnapshots.Describe(item, resolveCatalog());
            }
            return null;
        }

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
