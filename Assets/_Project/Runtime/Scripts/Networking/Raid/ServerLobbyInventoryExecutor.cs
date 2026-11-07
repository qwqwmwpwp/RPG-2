using System;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
using RPG4.Raid.Application;
using RPG4.Raid.Domain;

namespace RPG4.Networking
{
    public sealed partial class AuthoritativeNetworkManager
    {
        private sealed class ServerLobbyInventoryExecutor : ILobbyInventoryExecutor
        {
            private readonly AuthoritativeNetworkManager owner;
            internal ServerLobbyInventoryExecutor(AuthoritativeNetworkManager owner) { this.owner = owner; }
            public PersistentContainerData CreateInitialStash()
                => (owner.CurrentGameplaySceneBindings?.TrustedSpawnConfiguration as OnlineSpawnConfiguration)?.CreateInitialStash(owner.ServerItemIds);
            public LobbyInventoryCandidate Execute(PersistentInventoryState state, LobbyCommand request)
            {
                if (request.Source > 2 || request.Destination > 2 || request.Kind == LobbyCommandKind.InventoryOperation && request.OperationKind > (byte)InventoryOperationKind.SwapItems)
                    throw new ArgumentException("Invalid inventory operation.");
                var views = new[] { new RaidInventoryView { Kind = PersonalContainerKind.Stash, Writable = true },
                    new RaidInventoryView { Kind = PersonalContainerKind.Backpack, Writable = true }, new RaidInventoryView { Kind = PersonalContainerKind.Equipment, Writable = true } };
                try
                {
                    var data = new[] { state.Stash, state.Backpack, state.Equipment };
                    ItemManifestCatalog catalog = owner.ResolveLobbyCatalog();
                    for (int i = 0; i < views.Length; i++) views[i].Apply(data[i], catalog, owner.ServerItemIds, true);
                    if (request.Kind == LobbyCommandKind.AssemblyQuery || request.Kind == LobbyCommandKind.AssemblyOperation)
                    {
                        if (request.RootItemId == 0 || request.SlotId != null && request.SlotId.Length > 64
                            || request.CandidateOffset < 0 || request.CandidateLimit < 1 || request.CandidateLimit > 64)
                            throw new ArgumentException("Invalid assembly request.");
                        InventoryHostCore host = views[1].Core;
                        host.AssemblyContainerResolver = id => {
                            foreach (RaidInventoryView view in views) if (view.Core.Container.TryGetItemById(id, out _)) return view.Core.Container;
                            return null;
                        };
                        host.AssemblyCandidateContainersProvider = () => new[] { views[0].Core.Container, views[1].Core.Container };
                        var assembly = new LobbyInventoryCandidate { Result = new LobbyInventoryResult { RequestId = request.RequestId } };
                        if (request.Kind == LobbyCommandKind.AssemblyQuery) {
                            var query = new AssemblyQuery(new ItemId(request.RootItemId), new ItemId(request.ParentItemId), request.SlotId,
                                request.AssemblySessionId, request.RequestId, request.CandidateOffset, request.CandidateLimit);
                            assembly.Result.AssemblyPayload = AssemblyWireCodec.Encode(host.QueryAssembly(query));
                            assembly.Result.AssemblyKind = 1; assembly.Result.Success = true;
                            return assembly;
                        }
                        AssemblyOperationResult resultAssembly = host.ExecuteAssembly(new AssemblyOperation(request.RequestId,
                            new ItemId(request.RootItemId), new ItemId(request.ParentItemId), request.SlotId,
                            new ItemId(request.CandidateItemId), request.StructureRevision, request.AssemblySessionId));
                        assembly.Result.Success = resultAssembly.Success; assembly.Result.Error = resultAssembly.Error;
                        assembly.Result.AssemblyKind = 2; assembly.Result.AssemblyPayload = AssemblyWireCodec.Encode(resultAssembly);
                        if (resultAssembly.Success) {
                            assembly.Stash = RaidInventorySnapshots.Capture(views[0].Core.Container, PersonalContainerKind.Stash);
                            assembly.Backpack = RaidInventorySnapshots.Capture(views[1].Core.Container, PersonalContainerKind.Backpack);
                            assembly.Equipment = RaidInventorySnapshots.Capture(views[2].Core.Container, PersonalContainerKind.Equipment);
                        }
                        return assembly;
                    }
                    InventoryOperationResult result = views[request.Source].Core.ExecuteOperation(new InventoryOperation(request.RequestId,
                        (InventoryOperationKind)request.OperationKind, new ItemId(request.SourceItemId), new ItemId(request.TargetItemId), request.Count,
                        views[request.Destination], request.UpperLeftIndex));
                    var candidate = new LobbyInventoryCandidate { Result = new LobbyInventoryResult { RequestId = result.RequestId,
                        Success = result.Success, ResultItemId = result.ResultItemId.Value, MovedCount = result.MovedCount, Error = result.Error } };
                    if (result.Success)
                    {
                        candidate.Stash = RaidInventorySnapshots.Capture(views[0].Core.Container, PersonalContainerKind.Stash);
                        candidate.Backpack = RaidInventorySnapshots.Capture(views[1].Core.Container, PersonalContainerKind.Backpack);
                        candidate.Equipment = RaidInventorySnapshots.Capture(views[2].Core.Container, PersonalContainerKind.Equipment);
                    }
                    return candidate;
                }
                finally { foreach (RaidInventoryView view in views) view.Dispose(); }
            }
        }
    }
}
