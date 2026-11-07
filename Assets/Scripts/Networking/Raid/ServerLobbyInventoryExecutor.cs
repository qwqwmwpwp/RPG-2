using System;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
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
                if (request.Source > 2 || request.Destination > 2 || request.OperationKind > (byte)InventoryOperationKind.SwapItems)
                    throw new ArgumentException("Invalid inventory operation.");
                var views = new[] { new RaidInventoryView { Kind = PersonalContainerKind.Stash, Writable = true },
                    new RaidInventoryView { Kind = PersonalContainerKind.Backpack, Writable = true }, new RaidInventoryView { Kind = PersonalContainerKind.Equipment, Writable = true } };
                try
                {
                    var data = new[] { state.Stash, state.Backpack, state.Equipment };
                    ItemManifestCatalog catalog = owner.ResolveLobbyCatalog();
                    for (int i = 0; i < views.Length; i++) views[i].Apply(data[i], catalog, owner.ServerItemIds, true);
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
