using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>规则候选与模型回写分离，提交前重新核对全部参与者及规则依赖。</summary>
    internal sealed class PreparedLoadoutOperation
    {
        private readonly Dictionary<ILoadoutEndpoint, object> states;
        private readonly bool selectedWeaponChanged;
        private readonly bool runtimeItemsChanged = true;
        private readonly Action applyState;
        private readonly Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot> dependencies;
        private readonly List<LoadoutEndpointChange> changes = new List<LoadoutEndpointChange>();
        private bool committed;
        private bool staged;
        private bool published;
        private RoleLoadoutController publicationOwner;
        private Action publication;

        private PreparedLoadoutOperation(ILoadoutEndpoint source, ILoadoutEndpoint target,
            object sourceState, object targetState, Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates,
            Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot> dependencies)
        {
            states = new Dictionary<ILoadoutEndpoint, object> { [source] = sourceState };
            if (!ReferenceEquals(source, target)) states.Add(target, targetState);
            Candidates = candidates; this.dependencies = dependencies;
        }

        internal Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> Candidates { get; }
        internal LoadoutCommitRecord CommittedRecord { get; set; }
        internal LoadoutCommitRecord OtherCommittedRecord { get; set; }

        internal PreparedLoadoutOperation(Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates,
            Dictionary<ILoadoutEndpoint, object> states, Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot> dependencies,
            bool selectedWeaponChanged = false, bool runtimeItemsChanged = true, Action applyState = null)
        {
            Candidates = candidates;
            this.states = states;
            this.dependencies = dependencies;
            this.selectedWeaponChanged = selectedWeaponChanged;
            this.runtimeItemsChanged = runtimeItemsChanged;
            this.applyState = applyState;
        }

        internal static bool TryPrepare(ILoadoutEndpoint source, ILoadoutEndpoint target,
            LoadoutDropIntent intent, out PreparedLoadoutOperation operation, out string error)
        {
            bool success = TryPrepareDetailed(source, target, intent, out operation, out LoadoutFailure failure);
            error = success ? null : failure.Reason;
            return success;
        }

        internal static bool TryPrepareDetailed(ILoadoutEndpoint source, ILoadoutEndpoint target,
            LoadoutDropIntent intent, out PreparedLoadoutOperation operation, out LoadoutFailure failure)
        {
            using var preparationScope = LoadoutDiagnostics.Begin(LoadoutDiagnosticStage.Prepare);
            operation = null;
            failure = default;
            if (!Exists(source) || !Exists(target))
                return Fail(LoadoutResultCode.NotReady, "来源或目标端点不存在。", out failure);
            ContainerSnapshot sourceDisplay = source.Snapshot;
            ContainerSnapshot targetDisplay = ReferenceEquals(source, target) ? sourceDisplay : target.Snapshot;
            LoadoutRuleSnapshot sourceRules = LoadoutRuleSnapshot.CaptureEndpoint(source, sourceDisplay);
            LoadoutRuleSnapshot targetRules = ReferenceEquals(source, target) ? sourceRules : LoadoutRuleSnapshot.CaptureEndpoint(target, targetDisplay);
            if (sourceRules == null || targetRules == null)
                return Fail(LoadoutResultCode.NotReady, "来源或目标快照当前不可用。", out failure);
            if (sourceRules.Revision == int.MaxValue || targetRules.Revision == int.MaxValue)
                return Fail(LoadoutResultCode.RevisionConflict, "库存版本已达到上限。", out failure);
            if (!ReferenceEquals(source, target) && sourceRules.EndpointToken == targetRules.EndpointToken)
                return Fail(LoadoutResultCode.SnapshotInvalid, "不同端点不能共用事件身份。", out failure);
            if (!LoadoutOperations.TryBuildDetailed(sourceRules, targetRules, intent,
                out List<LoadoutRuleItem> sourceItems, out List<LoadoutRuleItem> targetItems, out failure)) return false;

            // 只在回写边界取回模型关联，规则计算已经只使用冻结值。
            var originals = new Dictionary<string, ItemDisplayData>(StringComparer.Ordinal);
            foreach (ItemDisplayData item in sourceDisplay.Items) originals.Add(item.Id, item);
            if (!ReferenceEquals(source, target))
                foreach (ItemDisplayData item in targetDisplay.Items) originals.Add(item.Id, item);
            List<ItemDisplayData> sourceCandidate = Project(sourceItems, originals);
            List<ItemDisplayData> targetCandidate = ReferenceEquals(source, target) ? sourceCandidate : Project(targetItems, originals);
            if (!source.TryPrepare(sourceCandidate, out object sourceState, out string error))
                return Fail(LoadoutResultCode.RuleRejected, error, out failure);
            object targetState = null;
            bool same = ReferenceEquals(source, target);
            if (!same && !target.TryPrepare(targetCandidate, out targetState, out error))
                return Fail(LoadoutResultCode.RuleRejected, error, out failure);
            var candidates = new Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> { [source] = sourceCandidate };
            var dependencies = new Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot> { [source] = sourceRules };
            if (!same) { candidates.Add(target, targetCandidate); dependencies.Add(target, targetRules); }
            var prepared = new PreparedLoadoutOperation(source, target, sourceState, targetState, candidates, dependencies);
            if (!prepared.ValidateDependencies(out failure)) return false;
            operation = prepared;
            return true;
        }

        internal void TrackDependencies(IEnumerable<ILoadoutEndpoint> endpoints)
        {
            foreach (ILoadoutEndpoint endpoint in endpoints)
                if (Exists(endpoint) && !dependencies.ContainsKey(endpoint))
                    dependencies.Add(endpoint, LoadoutRuleSnapshot.CaptureEndpoint(endpoint, endpoint.Snapshot));
        }

        internal bool ValidateDependencies(out LoadoutFailure failure)
        {
            foreach (var dependency in dependencies)
                if (!Exists(dependency.Key) || dependency.Value == null || !dependency.Value.Matches(dependency.Key, dependency.Key.Snapshot))
                    return Fail(LoadoutResultCode.RevisionConflict, "库存参与者、预占或规则依赖已改变。", out failure);
            failure = default;
            return true;
        }

        internal bool TryCommit(out LoadoutFailure failure)
        {
            using var commitScope = LoadoutDiagnostics.Begin(LoadoutDiagnosticStage.Commit);
            if (committed) return Fail(LoadoutResultCode.InvalidRequest, "库存操作已提交。", out failure);
            if (!ValidateDependencies(out failure)) return false;
            // 所有端点在这里完成内部状态。展示与外部订阅者在 PublishChanges 中发布。
            foreach (var state in states) state.Key.Commit(state.Value);
            applyState?.Invoke();
            committed = true;
            foreach (var dependency in dependencies)
            {
                ContainerSnapshot after = dependency.Key.Snapshot;
                if (!Candidates.ContainsKey(dependency.Key) && dependency.Value.Matches(dependency.Key, after)) continue;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var ordered = new List<string>();
                AddItemIds(dependency.Value.Items, ids, ordered);
                AddItemIds(LoadoutRuleSnapshot.Capture(after).Items, ids, ordered);
                changes.Add(new LoadoutEndpointChange(dependency.Value.Id, dependency.Value.Revision, after.Revision, ordered));
                if (!string.Equals(dependency.Value.Id, after.Id, StringComparison.Ordinal))
                    changes.Add(new LoadoutEndpointChange(after.Id, dependency.Value.Revision, after.Revision, ordered));
            }
            return true;
        }

        internal LoadoutCommitRecord CreateCommitRecord(LoadoutOperationKey key,
            LoadoutStateVersion before, LoadoutStateVersion after)
        {
            if (!committed) throw new InvalidOperationException("库存操作尚未提交。");
            return new LoadoutCommitRecord(key, before, after, changes,
                runtimeItemsChanged: runtimeItemsChanged, selectedWeaponChanged: selectedWeaponChanged);
        }

        internal void StageChanges(LootSource external = null, RoleLoadoutController role = null,
            LoadoutCommitRecord commit = null)
        {
            if (!committed || staged) return;
            staged = true;
            publication = () =>
            {
                if (published) return;
                published = true;
                try
                {
                    foreach (ILoadoutEndpoint endpoint in states.Keys)
                        if (external == null || !external.Owns(endpoint)) PublishEndpoint(endpoint);
                    if (role != null && commit == null)
                        try { role.PublishRuntimeChanged(); }
                        catch (Exception exception) { Debug.LogException(exception, role); }
                }
                finally
                {
                    // 捕获的外部源独立发布，主机回执重入关窗或角色通知异常不会吞掉其他查看者通知。
                    if (external != null) external.Publish();
                }
            };
            if (role != null && commit != null)
            {
                publicationOwner = role;
                role.QueueCommitted(commit, publication);
            }
        }

        internal void PublishChanges(LootSource external = null, RoleLoadoutController role = null,
            LoadoutCommitRecord commit = null)
        {
            StageChanges(external, role, commit);
            if (!committed || published) return;
            if (!ReferenceEquals(publicationOwner, null)) publicationOwner.FlushCommitted();
            else publication();
        }

        private static List<ItemDisplayData> Project(IEnumerable<LoadoutRuleItem> items,
            Dictionary<string, ItemDisplayData> originals)
        {
            var result = new List<ItemDisplayData>();
            foreach (LoadoutRuleItem item in items)
                result.Add(originals[item.Id].WithRotation(item.Rotated).At(item.Origin));
            return result;
        }

        private static void AddItemIds(IEnumerable<LoadoutRuleItem> items, HashSet<string> ids, List<string> result)
        {
            foreach (LoadoutRuleItem item in items)
            {
                if (item == null) continue;
                if (ids.Add(item.Id)) result.Add(item.Id);
                foreach (ulong runtimeId in item.RuntimeItemIds)
                {
                    string id = "runtime/" + runtimeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (ids.Add(id)) result.Add(id);
                }
                AddItemIds(item.StorageItems, ids, result);
            }
        }

        private static bool Exists(ILoadoutEndpoint endpoint) => endpoint != null
            && (!(endpoint is UnityEngine.Object unityObject) || unityObject != null);
        private static bool Fail(LoadoutResultCode code, string reason, out LoadoutFailure failure)
        { failure = new LoadoutFailure(code, reason); return false; }
        private static void PublishEndpoint(ILoadoutEndpoint endpoint)
        {
            try { endpoint.PublishChanged(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
}
