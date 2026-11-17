using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.LoadoutTemplate
{
    internal interface ILoadoutCandidateCodec
    {
        bool ValidateCandidate(Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates,
            long revision, LootSource external, out string error);
    }

    /// <summary>服务器操作边界。准备、全库存归属和完整编码全部通过后才提交。</summary>
    internal sealed class ServerLoadoutOperations
    {
        private readonly RoleLoadoutController role;
        private readonly ILoadoutCandidateCodec codec;

        internal ServerLoadoutOperations(RoleLoadoutController role, ILoadoutCandidateCodec codec)
        {
            this.role = role;
            this.codec = codec;
        }

        internal bool TryExecuteDetailed(ILoadoutEndpoint source, ILoadoutEndpoint target, LoadoutDropIntent intent,
            LoadoutOperationKey key, long revision, LootSource external, out PreparedLoadoutOperation committed, out LoadoutFailure failure)
        {
            committed = null;
            failure = default;
            if (role == null || codec == null)
                return Fail(LoadoutResultCode.NotReady, "角色库存或编码器尚未就绪。", out failure);
            if (!role.CommitCoordinator.TryEnter(out IDisposable transaction, out failure)) return false;
            using var transactionScope = transaction;
            if (revision != role.CommitCoordinator.Revision)
                return Fail(LoadoutResultCode.RevisionConflict, "角色库存版本已改变。", out failure);
            if (revision == long.MaxValue)
                return Fail(LoadoutResultCode.RevisionConflict, "角色库存版本已达到上限。", out failure);
            LoadoutStateVersion roleVersion = role.CaptureStateVersion(external);
            int externalRevision = external?.Revision ?? 0;
            if (external != null && (externalRevision == int.MaxValue
                || !ReferenceEquals(source, target) && external.Owns(source) && external.Owns(target)
                && externalRevision == int.MaxValue - 1))
                return Fail(LoadoutResultCode.RevisionConflict, "战利品库存版本已达到上限。", out failure);
            if (!PreparedLoadoutOperation.TryPrepareDetailed(source, target, intent,
                out PreparedLoadoutOperation operation, out failure)) return false;
            List<ILoadoutEndpoint> participants = CollectParticipants(external);
            operation.TrackDependencies(participants);
            if (!ValidateOwnership(operation.Candidates, external, out string error))
                return Fail(LoadoutResultCode.SnapshotInvalid, error, out failure);
            if (!codec.ValidateCandidate(operation.Candidates, revision + 1, external, out error))
                return Fail(LoadoutResultCode.SnapshotInvalid, error, out failure);
            List<ILoadoutEndpoint> currentParticipants = CollectParticipants(external);
            LoadoutStateVersion currentRoleVersion = role.CaptureStateVersion();
            if (currentRoleVersion.RoleRevision != roleVersion.RoleRevision
                || !string.Equals(currentRoleVersion.Epoch, roleVersion.Epoch, StringComparison.Ordinal))
                return Fail(LoadoutResultCode.RevisionConflict, "角色库存版本已改变。", out failure);
            if (participants.Count != currentParticipants.Count || external != null && external.Revision != externalRevision)
                return Fail(LoadoutResultCode.RevisionConflict, "库存参与者或战利品版本已改变。", out failure);
            for (int index = 0; index < participants.Count; index++)
                if (!ReferenceEquals(participants[index], currentParticipants[index]))
                    return Fail(LoadoutResultCode.RevisionConflict, "库存参与者已改变。", out failure);
            if (!role.CommitCoordinator.TryCommit(operation, key, roleVersion, external,
                out _, out failure)) return false;
            committed = operation;
            return true;
        }

        private List<ILoadoutEndpoint> CollectParticipants(LootSource external)
        {
            var result = new List<ILoadoutEndpoint>();
            foreach (RoleSlotConfiguration configuration in role.Configuration)
            {
                result.Add(role.GetSlot(configuration.slotId));
                if (configuration.containerController != null) result.Add(configuration.containerController);
            }
            if (external != null)
                foreach (LootSection section in external.Sections)
                {
                    if (section.Body != null) result.Add(section.Body);
                    result.Add(section.Grid);
                }
            return result;
        }

        private static bool Fail(LoadoutResultCode code, string reason, out LoadoutFailure failure)
        { failure = new LoadoutFailure(code, reason); return false; }

        private bool ValidateOwnership(Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates,
            LootSource external, out string error)
        {
            error = null;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var storageCandidates = new Dictionary<BackpackContainerData, IReadOnlyList<ItemDisplayData>>();
            foreach (var entry in candidates)
            {
                if (entry.Key is BackpackContainerController grid && grid.Container != null)
                    storageCandidates[grid.Container] = entry.Value;
            }
            if (external != null)
            {
                foreach (LootSection section in external.Sections)
                {
                    if (section.Body == null || section.Body.Snapshot.Items.Count == 0) continue;
                    BackpackContainerData storage = section.Body.Snapshot.Items[0].Storage;
                    if (storage != null && candidates.TryGetValue(section.Grid, out List<ItemDisplayData> contents))
                        storageCandidates[storage] = contents;
                }
            }
            foreach (RoleSlotConfiguration configuration in role.Configuration)
            {
                if (!AddItems(Items(role.GetSlot(configuration.slotId), candidates), identities, storageCandidates, out error))
                    return false;
            }
            if (external == null) return true;
            foreach (LootSection section in external.Sections)
            {
                // 容器本体与网格描述同一批物品，只从本体遍历储物所有权。
                // 本体已被取走时，旧源的网格不再持有那些内容。
                ILoadoutEndpoint endpoint = section.Body ?? section.Grid;
                if (!AddItems(Items(endpoint, candidates), identities, storageCandidates, out error)) return false;
            }
            return true;
        }

        private static IReadOnlyList<ItemDisplayData> Items(ILoadoutEndpoint endpoint,
            Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates) =>
            candidates.TryGetValue(endpoint, out List<ItemDisplayData> items) ? items : endpoint.Snapshot.Items;

        private static bool AddItems(IReadOnlyList<ItemDisplayData> items, HashSet<string> identities,
            Dictionary<BackpackContainerData, IReadOnlyList<ItemDisplayData>> storageCandidates, out string error)
        {
            error = null;
            foreach (ItemDisplayData item in items)
            {
                if (item == null || !identities.Add(item.Id))
                {
                    error = "角色与战利品中存在重复物品归属。";
                    return false;
                }
                if (item.RuntimeItem != null)
                {
                    foreach (RuntimeItem node in ItemAssemblyTraversal.Enumerate(item.RuntimeItem))
                    {
                        if (!node.ItemId.IsValid || !identities.Add("\0runtime/" + node.ItemId.Value))
                        {
                            error = "角色与战利品中存在重复运行物品归属。";
                            return false;
                        }
                    }
                }
                if (item.Storage == null) continue;
                if (!identities.Add(item.Storage.Id))
                {
                    error = "多个容器引用了相同储物实例。";
                    return false;
                }
                IReadOnlyList<ItemDisplayData> contents = storageCandidates.TryGetValue(item.Storage, out var candidate)
                    ? candidate : item.Storage.Items;
                if (!AddItems(contents, identities, storageCandidates, out error)) return false;
            }
            return true;
        }
    }
}
