using System;
using System.Collections.Generic;
using System.Text;
using Mirror;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    public sealed partial class NetworkRoleLoadout
    {
        private const int OutcomeSchema = 2;
        // 两个 JSON 字符串各不超过 48 KiB，均低于 Mirror 的 65534 字节字符串上限。
        private const int MaxOutcomeWireBytes = MaxWireBytes, MaxResultMessageCharacters = 1024;
        private LoadoutOperationKey pendingOperationKey;
        private LoadoutOperationKey activeServerOperationKey;
        private bool hasActiveServerOperation;

        [Serializable] private sealed class VersionWire
        {
            public string epoch, sourceId;
            public long roleRevision;
            public int sourceRevision;
        }
        [Serializable] private sealed class ChangeWire
        {
            public string endpointId;
            public int before, after;
            public int[] items;
        }
        [Serializable] private sealed class OutcomeWire
        {
            public int schema = OutcomeSchema, channel, requestId, code;
            public uint actor;
            public string epoch, session, message;
            public bool committed, runtimeChanged, selectedChanged, detailsComplete;
            public string[] itemIds;
            public VersionWire before, after, current;
            public ChangeWire[] changes;
        }
        internal LoadoutOperationKey CreateOperationKey(LoadoutOperationChannel channel, int requestId = 0)
        {
            if (channel == LoadoutOperationChannel.Runtime && runtimeApplyingOperation)
                return activeServerOperationKey;
            return new LoadoutOperationKey(netId, serverEpoch, serverClientSession,
                channel == LoadoutOperationChannel.Runtime ? LoadoutOperationChannel.System : channel, requestId);
        }
        internal LoadoutStateVersion CurrentStateVersion(LootSource source = null) =>
            new LoadoutStateVersion(serverEpoch, serverRevision, source?.Id, source?.Revision ?? -1);
        private LoadoutStateVersion ClientStateVersion() =>
            new LoadoutStateVersion(clientEpoch, clientRevision, clientLootSourceId, clientLootRevision);
        private LoadoutOperationKey ClientOperationKey(LoadoutOperationChannel channel, int requestId) =>
            new LoadoutOperationKey(netId, clientEpoch, clientSession, channel, requestId);
        private LoadoutOperationKey CurrentDiagnosticKey() => hasActiveServerOperation
            ? activeServerOperationKey : CreateOperationKey(LoadoutOperationChannel.System);
        private LoadoutOperationOutcome LocalOutcome(LoadoutResultCode code, string message,
            LoadoutOperationChannel channel = LoadoutOperationChannel.Inventory) =>
            new LoadoutOperationOutcome(ClientOperationKey(channel, 0), code, message, null, ClientStateVersion());
        private static bool NeedsResync(LoadoutResultCode code) => code == LoadoutResultCode.RequestExpired
            || code == LoadoutResultCode.RevisionConflict || code == LoadoutResultCode.SessionExpired
            || code == LoadoutResultCode.SnapshotInvalid || code == LoadoutResultCode.ConfigurationMismatch;

        private void CompletePending(LoadoutOperationOutcome outcome)
        {
            if (pendingRequest == 0) return;
            LoadoutOperationRequest completed = pendingOperation;
            pendingRequest = 0; pendingOperation = default;
            LoadoutDiagnostics.CompleteRequest(pendingOperationKey, outcome.Code);
            pendingOperationKey = default;
            try { EventBus.Global.Publish(LoadoutChannels.OperationCompleted, completed.Session,
                new LoadoutOperationResult(completed, outcome)); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
        private static VersionWire EncodeVersion(LoadoutStateVersion version) => new VersionWire
        { epoch = version.Epoch, roleRevision = version.RoleRevision, sourceId = version.SourceId, sourceRevision = version.SourceRevision };
        private static LoadoutStateVersion DecodeVersion(VersionWire version) =>
            new LoadoutStateVersion(version.epoch, version.roleRevision, version.sourceId, version.sourceRevision);
        private static string BoundResultMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            if (message.Length <= MaxResultMessageCharacters) return message;
            int length = MaxResultMessageCharacters - 1;
            if (char.IsHighSurrogate(message[length - 1])) length--;
            return message.Substring(0, length) + "…";
        }
        private static string EncodeOutcome(LoadoutOperationOutcome outcome)
        {
            using var encodingScope = LoadoutDiagnostics.Begin(LoadoutDiagnosticStage.Encode);
            var wire = new OutcomeWire { actor = outcome.Key.ActorNetId, epoch = outcome.Key.Epoch,
                session = outcome.Key.Session, channel = (int)outcome.Key.Channel, requestId = outcome.Key.RequestId,
                code = (int)outcome.Code, message = BoundResultMessage(outcome.Message), current = EncodeVersion(outcome.Current),
                committed = outcome.Commit != null, itemIds = Array.Empty<string>(), changes = Array.Empty<ChangeWire>() };
            if (outcome.Commit != null)
            {
                wire.before = EncodeVersion(outcome.Commit.Before); wire.after = EncodeVersion(outcome.Commit.After);
                wire.runtimeChanged = outcome.Commit.RuntimeItemsChanged; wire.selectedChanged = outcome.Commit.SelectedWeaponChanged;
                wire.detailsComplete = outcome.Commit.DetailsComplete;
                if (wire.detailsComplete)
                {
                    var identities = new List<string>();
                    var indices = new Dictionary<string, int>(StringComparer.Ordinal);
                    var changes = new List<ChangeWire>();
                    foreach (LoadoutEndpointChange change in outcome.Commit.Changes)
                    {
                        var items = new List<int>(change.ItemIds.Count);
                        foreach (string id in change.ItemIds)
                        {
                            if (!indices.TryGetValue(id, out int index))
                            { index = identities.Count; identities.Add(id); indices.Add(id, index); }
                            items.Add(index);
                        }
                        changes.Add(new ChangeWire { endpointId = change.EndpointId, before = change.BeforeRevision,
                            after = change.AfterRevision, items = items.ToArray() });
                    }
                    wire.itemIds = identities.ToArray(); wire.changes = changes.ToArray();
                }
            }
            string json = JsonUtility.ToJson(wire);
            if (!Bounded(json, MaxOutcomeWireBytes))
            {
                // 回执与本地事件保留完整事实。仅网络明细降为显式摘要，成功码及原提交版本保持完整。
                // 请求 JSON 限 4096 字节、版本源 ID 限 160 字符、消息限 1024 字符，摘要必低于 48 KiB。
                wire.detailsComplete = false;
                wire.itemIds = Array.Empty<string>(); wire.changes = Array.Empty<ChangeWire>();
                json = JsonUtility.ToJson(wire);
            }
            if (LoadoutDiagnostics.Enabled)
                LoadoutDiagnostics.RecordSnapshot(outcome.Key, "outcome", Encoding.UTF8.GetByteCount(json));
            return json;
        }
        private static LoadoutOperationOutcome DecodeOutcome(string json)
        {
            if (!Bounded(json, MaxOutcomeWireBytes)) return null;
            try
            {
                OutcomeWire wire = JsonUtility.FromJson<OutcomeWire>(json);
                if (wire == null || wire.schema != OutcomeSchema || wire.current == null
                    || wire.message == null || wire.message.Length > MaxResultMessageCharacters
                    || !Enum.IsDefined(typeof(LoadoutResultCode), wire.code)
                    || !Enum.IsDefined(typeof(LoadoutOperationChannel), wire.channel)) return null;
                var key = new LoadoutOperationKey(wire.actor, wire.epoch, wire.session,
                    (LoadoutOperationChannel)wire.channel, wire.requestId);
                LoadoutCommitRecord commit = null;
                if (wire.committed)
                {
                    if (wire.before == null || wire.after == null || wire.changes == null || wire.itemIds == null
                        || !wire.detailsComplete && (wire.changes.Length != 0 || wire.itemIds.Length != 0)) return null;
                    var identities = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string id in wire.itemIds)
                        if (!ValidId(id) || !identities.Add(id)) return null;
                    var changes = new List<LoadoutEndpointChange>();
                    foreach (ChangeWire change in wire.changes)
                    {
                        if (change == null || change.items == null || !ValidId(change.endpointId)) return null;
                        var items = new List<string>(change.items.Length);
                        foreach (int index in change.items)
                        {
                            if (index < 0 || index >= wire.itemIds.Length) return null;
                            items.Add(wire.itemIds[index]);
                        }
                        changes.Add(new LoadoutEndpointChange(change.endpointId, change.before, change.after, items));
                    }
                    commit = new LoadoutCommitRecord(key, DecodeVersion(wire.before), DecodeVersion(wire.after),
                        changes, wire.runtimeChanged, wire.selectedChanged, wire.detailsComplete);
                }
                return new LoadoutOperationOutcome(key, (LoadoutResultCode)wire.code, wire.message, commit, DecodeVersion(wire.current));
            }
            catch (Exception) { return null; }
        }

        /// <summary>在共享编号域内受理库存或搜刮请求，先记录权威结果再发布变更。</summary>
        private void ExecuteInventoryRequest(string json, string token, bool loot, NetworkConnectionToClient sender)
        {
            if (!Owns(sender)) return;
            OperationWire request = null;
            if (Bounded(json, 4096))
                try { request = JsonUtility.FromJson<OperationWire>(json); } catch (Exception) { }
            var channel = loot ? LoadoutOperationChannel.Loot : LoadoutOperationChannel.Inventory;
            var key = new LoadoutOperationKey(netId, request?.epoch, request?.session, channel, request?.requestId ?? 0);
            LootSource changedSource = loot ? serverLootData : null;
            LoadoutOperationOutcome outcome;
            if (request == null || request.schema != Schema || request.requestId <= 0)
            { SendOperationOutcome(sender, new LoadoutOperationOutcome(key, LoadoutResultCode.InvalidRequest,
                "操作协议无效。", null, CurrentStateVersion(changedSource))); return; }
            if (!serverInitialized || request.epoch != serverEpoch || request.session != serverClientSession
                || loot && (changedSource == null || token != serverLootToken || request.openId != serverLootOpenId))
            { SendOperationOutcome(sender, new LoadoutOperationOutcome(key, LoadoutResultCode.SessionExpired,
                "角色或战利品会话已失效。", null, CurrentStateVersion(changedSource))); return; }
            if (request.configuration != configuration)
            { SendOperationOutcome(sender, new LoadoutOperationOutcome(key, LoadoutResultCode.ConfigurationMismatch,
                "角色配置已改变。", null, CurrentStateVersion(changedSource))); return; }
            if (!receipts.TryBegin(key, token, json, CurrentStateVersion(changedSource), out outcome, out bool replayed))
            {
                SendOperationOutcome(sender, outcome, replayed);
                if (loot && (replayed || outcome.Code == LoadoutResultCode.DuplicateRequestConflict) && serverLootData != null)
                    OnServerLootChanged();
                return;
            }
            PreparedLoadoutOperation prepared = null;
            LoadoutCommitRecord commit = null;
            LoadoutFailure failure = new LoadoutFailure(LoadoutResultCode.Success, string.Empty);
            activeServerOperationKey = key; hasActiveServerOperation = true;
            try
            {
                ILoadoutEndpoint source = null, target = null;
                if (!operationsAllowed || loot && !CanAccessServerLoot())
                    failure = new LoadoutFailure(LoadoutResultCode.AccessDenied, "角色当前不能访问库存或战利品。");
                else if (request.roleRevision != serverRevision)
                    failure = new LoadoutFailure(LoadoutResultCode.RevisionConflict, "角色库存版本已改变。");
                else if (!ValidId(request.source) || !ValidId(request.target) || !ValidId(request.item)
                    || !string.IsNullOrEmpty(request.targetItem) && !ValidId(request.targetItem))
                    failure = new LoadoutFailure(LoadoutResultCode.InvalidRequest, "物品或端点身份无效。");
                else if (!(loot ? TryResolveLootEndpoint(request.source, out source) : serverEndpoints.TryGetValue(request.source, out source))
                    || !(loot ? TryResolveLootEndpoint(request.target, out target) : serverEndpoints.TryGetValue(request.target, out target))
                    || loot && !changedSource.Owns(source) && !changedSource.Owns(target))
                    failure = new LoadoutFailure(LoadoutResultCode.AccessDenied, "来源或目标不属于当前会话。");
                else
                {
                    var intent = new LoadoutDropIntent(request.requestId, (LoadoutDragKind)request.kind, request.source,
                        request.target, request.item, request.targetItem, request.sourceOrigin, request.origin,
                        request.targetOrigin, request.sourceRevision, request.targetRevision, request.rotated);
                    if (serverOperations.TryExecuteDetailed(source, target, intent, key, serverRevision, changedSource, out prepared, out failure))
                    {
                        commit = prepared.CommittedRecord;
                        prepared.StageChanges(changedSource, role, commit);
                        ReindexServer();
                    }
                }
            }
            catch (Exception exception)
            {
                failure = new LoadoutFailure(LoadoutResultCode.InternalError, "库存操作处理失败。");
                Debug.LogException(exception, this);
            }
            finally
            {
                outcome = new LoadoutOperationOutcome(key, commit != null ? LoadoutResultCode.Success : failure.Code,
                    commit != null ? string.Empty : failure.Reason, commit, CurrentStateVersion(changedSource));
                try
                {
                    receipts.Record(token, json, outcome);
                    SendOperationOutcome(sender, outcome);
                }
                finally
                {
                    hasActiveServerOperation = false;
                    using (LoadoutDiagnostics.Begin(LoadoutDiagnosticStage.Notify))
                    {
                        if (prepared != null) prepared.PublishChanges(changedSource, role, commit);
                        else if (loot && serverLootData != null) OnServerLootChanged();
                    }
                }
            }
        }
        private void SendOperationOutcome(NetworkConnectionToClient owner, LoadoutOperationOutcome outcome, bool replayed = false)
        {
            LoadoutDiagnostics.RecordOutcome(outcome, replayed);
            if (owner == null || !isServer) return;
            if (!TryCapture(out string state, out string error))
            { state = string.Empty; Debug.LogError(error, this); }
            TargetReceive(owner, outcome.Key.Session, outcome.Key.RequestId, 0, outcome.Success,
                BoundResultMessage(outcome.Message), state, EncodeOutcome(outcome));
        }
    }
}
