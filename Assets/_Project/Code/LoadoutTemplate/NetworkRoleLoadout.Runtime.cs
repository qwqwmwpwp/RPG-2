using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using RPG4.LoadoutContracts;
using Mirror;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    public sealed partial class NetworkRoleLoadout
    {
        private const int RuntimeOperationSchema = 2, RuntimeReceiptLimit = 128;
        [Serializable] private sealed class RuntimeOperationWire
        {
            public int schema = RuntimeOperationSchema, requestId, callerRequestId, kind, count, slot;
            public long revision;
            public ulong target, candidate;
            public string configuration, epoch, session, assemblySlot;
        }
        private readonly Dictionary<int, Receipt> runtimeReceipts = new Dictionary<int, Receipt>();
        private string runtimeReceiptSession, runtimeReceiptEpoch;
        private int highestRuntimeRequest, nextRuntimeRequest, pendingRuntimeRequest;
        private bool runtimeOperationExecuting, runtimeApplyingOperation;
        private int deferredRuntimePublications;
        public event Action<WeaponOperation, LoadoutOperationOutcome> RuntimeOperationCompleted;
        private float runtimeRequestDeadline;
        private RuntimeOperationWire pendingRuntimeWire;
        private WeaponOperation pendingRuntimeOperation;
        private Func<bool> runtimeCanOperate;
        private Action<WeaponOperation> runtimePrepare;
        public event Action<WeaponOperationResult> WeaponOperationCompleted;

        /// <summary>角色行为桥接提供准入和动作取消；未绑定时服务器拒绝运行物品请求。</summary>
        public void BindRuntimeOperations(Func<bool> canOperate, Action<WeaponOperation> prepare)
        { runtimeCanOperate = canOperate; runtimePrepare = prepare; }
        public void UnbindRuntimeOperations(Func<bool> canOperate, Action<WeaponOperation> prepare)
        {
            if (runtimeCanOperate == canOperate && runtimePrepare == prepare)
            { runtimeCanOperate = null; runtimePrepare = null; }
        }
        public bool RequestWeaponOperation(WeaponOperation operation)
        {
            if (!localStarted || !isLocalPlayer || !isOwned || !IsReady || !localInputAllowed || awaitingSync
                || pendingRequest != 0 || pendingRuntimeRequest != 0 || nextRuntimeRequest == int.MaxValue
                || !LoadoutWeaponOperations.Validate(operation, out _))
            {
                PublishRuntimeOutcome(operation, LocalOutcome(LoadoutResultCode.NotReady,
                    "当前角色会话不可提交武器操作。", LoadoutOperationChannel.Runtime)); return false;
            }
            pendingRuntimeRequest = ++nextRuntimeRequest; pendingRuntimeOperation = operation;
            LoadoutDiagnostics.BeginRequest(ClientOperationKey(LoadoutOperationChannel.Runtime, pendingRuntimeRequest));
            pendingRuntimeWire = new RuntimeOperationWire { requestId = pendingRuntimeRequest,
                callerRequestId = operation.RequestId, configuration = configuration, epoch = clientEpoch, session = clientSession,
                revision = clientRevision, kind = (int)operation.Kind, target = operation.TargetItemId.Value,
                candidate = operation.CandidateItemId.Value, count = operation.Count, slot = (int)operation.Slot,
                assemblySlot = operation.AssemblySlotId };
            runtimeRequestDeadline = Time.unscaledTime + requestTimeout;
            CmdRuntimeOperate(JsonUtility.ToJson(pendingRuntimeWire));
            return true;
        }
        [Command] private void CmdRuntimeOperate(string json, NetworkConnectionToClient sender = null)
        {
            if (!Owns(sender)) return;
            RuntimeOperationWire request = null;
            if (Bounded(json, 4096))
                try { request = JsonUtility.FromJson<RuntimeOperationWire>(json); } catch (Exception) { }
            var key = new LoadoutOperationKey(netId, request?.epoch, request?.session,
                LoadoutOperationChannel.Runtime, request?.requestId ?? 0);
            if (request == null || request.schema != RuntimeOperationSchema || request.requestId <= 0)
            { SendRuntimeResult(sender, request ?? new RuntimeOperationWire(), new LoadoutOperationOutcome(key, LoadoutResultCode.InvalidRequest,
                "武器操作协议无效。", null, CurrentStateVersion())); return; }
            if (!serverInitialized || request.session != serverClientSession || request.epoch != serverEpoch)
            { SendRuntimeResult(sender, request, new LoadoutOperationOutcome(key, LoadoutResultCode.SessionExpired,
                "角色会话已改变。", null, CurrentStateVersion())); return; }
            if (request.configuration != configuration)
            { SendRuntimeResult(sender, request, new LoadoutOperationOutcome(key, LoadoutResultCode.ConfigurationMismatch,
                "角色配置已改变。", null, CurrentStateVersion())); return; }
            if (runtimeReceiptSession != request.session || runtimeReceiptEpoch != request.epoch)
            { ClearRuntimeServerOperations(); runtimeReceiptSession = request.session; runtimeReceiptEpoch = request.epoch; }
            if (runtimeReceipts.TryGetValue(request.requestId, out Receipt receipt))
            {
                bool identical = receipt.request == json;
                var replay = identical
                    ? new LoadoutOperationOutcome(key, receipt.outcome.Code, receipt.outcome.Message, receipt.outcome.Commit, CurrentStateVersion())
                    : new LoadoutOperationOutcome(key, LoadoutResultCode.DuplicateRequestConflict,
                        "重复请求编号对应不同操作。", null, CurrentStateVersion());
                SendRuntimeResult(sender, request, replay, identical); return;
            }
            if (request.requestId <= highestRuntimeRequest)
            { SendRuntimeResult(sender, request, new LoadoutOperationOutcome(key, LoadoutResultCode.RequestExpired,
                "旧武器请求回执已过期，请重新同步。", null, CurrentStateVersion())); return; }
            if (runtimeOperationExecuting)
            {
                highestRuntimeRequest = request.requestId;
                var busy = new LoadoutOperationOutcome(key, LoadoutResultCode.NotReady,
                    "上一笔武器操作正在完成。", null, CurrentStateVersion());
                if (runtimeReceipts.Count >= RuntimeReceiptLimit) runtimeReceipts.Clear();
                runtimeReceipts[request.requestId] = new Receipt { request = json, outcome = busy };
                SendRuntimeResult(sender, request, busy); return;
            }
            highestRuntimeRequest = request.requestId;
            var operation = new WeaponOperation(request.callerRequestId, (WeaponOperationKind)request.kind,
                new ItemId(request.target), new ItemId(request.candidate), request.count,
                (WeaponAttachmentSlot)request.slot, request.assemblySlot);
            LoadoutResultCode code = LoadoutResultCode.Success;
            string error = null;
            LoadoutCommitRecord requestedCommit = null;
            activeServerOperationKey = key; hasActiveServerOperation = true;
            runtimeOperationExecuting = true;
            try
            {
                if (!LoadoutWeaponOperations.Validate(operation, out error)) code = LoadoutResultCode.InvalidRequest;
                else if (!LoadoutWeaponOperations.ValidateOwnership(role, operation, out error)) code = LoadoutResultCode.AccessDenied;
                // 准入和版本检查必须早于动作取消，旧请求不能取消当前换弹。
                else if (request.revision != serverRevision)
                { code = LoadoutResultCode.RevisionConflict; error = "角色库存版本已改变。"; }
                else if (!operationsAllowed || !isActiveAndEnabled || runtimeCanOperate == null || runtimePrepare == null)
                { code = LoadoutResultCode.AccessDenied; error = "角色当前不能操作武器物品。"; }
                else
                {
                    Func<bool> guard = runtimeCanOperate;
                    Action<WeaponOperation> prepare = runtimePrepare;
                    if (!guard()) { code = LoadoutResultCode.AccessDenied; error = "角色当前不能操作武器物品。"; }
                    else if (request.revision != serverRevision || request.epoch != serverEpoch || request.session != serverClientSession)
                    { code = LoadoutResultCode.RevisionConflict; error = "角色库存版本或会话已改变。"; }
                    else if (role.CommitCoordinator.IsExecuting)
                    { code = LoadoutResultCode.NotReady; error = "角色库存正在完成另一笔操作。"; }
                    else
                    {
                        prepare(operation);
                        if (!operationsAllowed || !isActiveAndEnabled || runtimeCanOperate != guard || runtimePrepare != prepare || !guard())
                        { code = LoadoutResultCode.AccessDenied; error = "操作准备期间角色状态已改变。"; }
                        else if (request.revision != serverRevision || request.epoch != serverEpoch || request.session != serverClientSession)
                        { code = LoadoutResultCode.RevisionConflict; error = "操作准备期间库存版本已改变。"; }
                        else
                        {
                            runtimeApplyingOperation = true;
                            try
                            {
                                if (!LoadoutWeaponOperations.TryApply(role, operation, out requestedCommit, out error)) code = LoadoutResultCode.RuleRejected;
                            }
                            finally { runtimeApplyingOperation = false; }
                        }
                    }
                }
            }
            catch (Exception exception)
            { code = LoadoutResultCode.InternalError; error = "武器操作处理失败。"; Debug.LogException(exception, this); }
            finally
            {
                // 已提交的结果在任何主机展示回调前落回执，通知失败不改变权威成功。
                LoadoutCommitRecord commit = requestedCommit;
                if (commit != null) { code = LoadoutResultCode.Success; error = null; }
                var outcome = new LoadoutOperationOutcome(key, code, error, commit, CurrentStateVersion());
                try
                {
                    if (runtimeReceipts.Count >= RuntimeReceiptLimit) runtimeReceipts.Clear();
                    runtimeReceipts[request.requestId] = new Receipt { request = json, outcome = outcome };
                    SendRuntimeResult(sender, request, outcome);
                }
                finally
                {
                    runtimeOperationExecuting = false; runtimeApplyingOperation = false;
                    hasActiveServerOperation = false;
                    FlushRuntimePublications();
                }
            }
        }
        private void SendRuntimeResult(NetworkConnectionToClient owner, RuntimeOperationWire request,
            LoadoutOperationOutcome outcome, bool replayed = false)
        {
            LoadoutDiagnostics.RecordOutcome(outcome, replayed);
            if (owner == null || !isServer) return;
            if (!TryCapture(out string state, out string captureError))
            { state = string.Empty; Debug.LogError(captureError, this); }
            TargetRuntimeResult(owner, request.session, request.epoch, request.requestId, request.callerRequestId,
                request.target, EncodeOutcome(outcome), state);
        }
        [TargetRpc] private void TargetRuntimeResult(NetworkConnectionToClient target, string session, string epoch,
            int requestId, int callerRequestId, ulong targetItemId, string outcomeJson, string json)
        {
            if (!localStarted || !isLocalPlayer || !isOwned || session != clientSession || epoch != clientEpoch
                || requestId != pendingRuntimeRequest || pendingRuntimeWire == null
                || callerRequestId != pendingRuntimeOperation.RequestId || targetItemId != pendingRuntimeOperation.TargetItemId.Value) return;
            LoadoutOperationOutcome outcome = DecodeOutcome(outcomeJson);
            if (outcome == null || outcome.Key.ActorNetId != netId || outcome.Key.Channel != LoadoutOperationChannel.Runtime
                || outcome.Key.Epoch != epoch || outcome.Key.Session != session || outcome.Key.RequestId != requestId)
            { CompletePendingRuntime(false, "武器操作结果无效。", LoadoutResultCode.SnapshotInvalid); RequestResync(); return; }
            bool applied = false;
            try { applied = TryApplyRuntimeSnapshot(json, out _); }
            catch (Exception exception) { Debug.LogException(exception, this); }
            finally
            {
                CompletePendingRuntime(outcome);
                if (!applied || NeedsResync(outcome.Code)) RequestResync();
            }
        }
        private bool TryApplyRuntimeSnapshot(string json, out string error)
        {
            if (!TryReadState(json, out StateWire wire, out Candidate candidate, out error)) return false;
            if (wire.epoch != clientEpoch) return Fail("角色会话已改变。", out error);
            // 更晚的完整角色快照已包含这次提交，不回退到迟到的结果快照。
            if (wire.revision < clientRevision) return projectionReady;
            clientRevision = wire.revision; clientOperationsAllowed = wire.accepting;
            projectionReady = true; ApplyProjection(candidate); RefreshInput(); NotifyState();
            EventBus.Global.Publish(LoadoutChannels.RoleAvailable, providerSource, ownerKey);
            return true;
        }
        private void CompletePendingRuntime(bool success, string error, LoadoutResultCode code = LoadoutResultCode.SessionExpired)
        {
            if (pendingRuntimeRequest == 0) return;
            var key = ClientOperationKey(LoadoutOperationChannel.Runtime, pendingRuntimeRequest);
            LoadoutDiagnostics.CancelRequest(key);
            CompletePendingRuntime(new LoadoutOperationOutcome(key, success ? LoadoutResultCode.Success : code,
                error, null, ClientStateVersion()));
        }
        private void CompletePendingRuntime(LoadoutOperationOutcome outcome)
        {
            if (pendingRuntimeRequest == 0) return;
            WeaponOperation completed = pendingRuntimeOperation;
            pendingRuntimeRequest = 0; pendingRuntimeWire = null; pendingRuntimeOperation = default;
            LoadoutDiagnostics.CompleteRequest(outcome.Key, outcome.Code);
            PublishRuntimeOutcome(completed, outcome);
        }
        private void PublishRuntimeOutcome(WeaponOperation operation, LoadoutOperationOutcome outcome)
        {
            if (RuntimeOperationCompleted != null)
                foreach (Action<WeaponOperation, LoadoutOperationOutcome> callback in RuntimeOperationCompleted.GetInvocationList())
                    try { callback(operation, outcome); } catch (Exception exception) { Debug.LogException(exception, this); }
            PublishRuntimeResult(new WeaponOperationResult(operation.RequestId, operation.TargetItemId, outcome.Success, outcome.Message));
        }
        private void PublishRuntimeResult(WeaponOperationResult result)
        {
            if (WeaponOperationCompleted == null) return;
            foreach (Action<WeaponOperationResult> callback in WeaponOperationCompleted.GetInvocationList())
                try { callback(result); } catch (Exception exception) { Debug.LogException(exception, this); }
        }
        private void UpdateRuntimeOperations()
        {
            if (pendingRuntimeRequest == 0) return;
            if (!localStarted || !isLocalPlayer || !isOwned || !projectionReady || !clientOperationsAllowed
                || pendingRuntimeWire.session != clientSession || pendingRuntimeWire.epoch != clientEpoch)
            { CompletePendingRuntime(false, "角色会话已结束或库存已暂停。"); return; }
            if (Time.unscaledTime >= runtimeRequestDeadline)
            { CompletePendingRuntime(false, "武器操作确认超时，正在重新同步。", LoadoutResultCode.Timeout); RequestResync(); }
        }
        private void ClearRuntimeClientOperations(string error)
        { CompletePendingRuntime(false, error); nextRuntimeRequest = 0; }
        private void ClearRuntimeServerOperations()
        {
            runtimeReceipts.Clear(); highestRuntimeRequest = 0;
            runtimeReceiptSession = runtimeReceiptEpoch = null;
        }

        private LoadoutSlotId projectedWeaponSlot = LoadoutSlotId.PrimaryWeapon1;
        internal LoadoutSlotId ProjectedWeaponSlot => projectedWeaponSlot;
        internal ContainerSnapshot GetProjectedSlot(LoadoutSlotId slot) => projectionReady
            && (int)slot >= 0 && (int)slot < 10 ? projection[(int)slot]?.snapshot : null;
        internal bool TryGetRuntimeDefinition(BackpackTag type, out LoadoutItemDefinition definition)
        {
            foreach (LoadoutItemDefinition candidate in catalog.Values)
                if (candidate.Manifest != null && candidate.Manifest.ItemType.MatchesTagExact(type)) { definition = candidate; return true; }
            definition = null; return false;
        }
        internal bool TryPrepareRuntimeChanges(Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates,
            LoadoutSlotId selected, out string error)
        {
            if (!Mirror.NetworkServer.active || !serverInitialized || !operationsAllowed || serverRevision == long.MaxValue
                || runtimeOperationExecuting && deferredRuntimePublications >= RuntimeReceiptLimit)
                return Fail("服务器库存未就绪、暂停或版本耗尽。", out error);
            return TryCapture(out _, out error, candidates, serverRevision + 1, selected);
        }
        internal void CommitRuntimeChanges()
        { ReindexServer(); }
        internal void PublishRuntimeCommit()
        {
            if (runtimeOperationExecuting)
            {
                deferredRuntimePublications++;
                return;
            }
            try
            {
                using (LoadoutDiagnostics.Begin(LoadoutDiagnosticStage.Notify)) role.FlushCommitted();
            }
            finally
            {
                try { SendSnapshot(connectionToClient, 0, true, null); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
        }
        private void FlushRuntimePublications()
        {
            int count = deferredRuntimePublications;
            deferredRuntimePublications = 0;
            if (count == 0) return;
            try
            {
                using (LoadoutDiagnostics.Begin(LoadoutDiagnosticStage.Notify)) role.FlushCommitted();
            }
            finally
            {
                try { SendSnapshot(connectionToClient, 0, true, null); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
        }
    }
    /// <summary>只执行新角色库存事务；入口负责拥有权、会话版本及角色行为准入。</summary>
    public static class LoadoutWeaponOperations
    {
        public static bool Validate(WeaponOperation operation, out string error)
        {
            error = null;
            if (operation.RequestId <= 0 || !operation.TargetItemId.IsValid)
            { error = "武器请求编号或目标物品无效。"; return false; }
            switch (operation.Kind)
            {
                case WeaponOperationKind.ExchangeMagazine:
                    if (operation.Count == 0 && string.IsNullOrEmpty(operation.AssemblySlotId)) return true;
                    break;
                case WeaponOperationKind.ExchangeAttachment:
                    if (operation.Count == 0 && operation.Slot == WeaponAttachmentSlot.Muzzle
                        && string.IsNullOrEmpty(operation.AssemblySlotId)) return true;
                    break;
                case WeaponOperationKind.LoadMagazine:
                    if (operation.CandidateItemId.IsValid && operation.Count > 0
                        && string.IsNullOrEmpty(operation.AssemblySlotId)) return true;
                    break;
                case WeaponOperationKind.ExchangeAssembly:
                    if (operation.Count == 0 && ItemAssemblySlots.IsValid(operation.AssemblySlotId)) return true;
                    break;
            }
            error = "武器操作类型、数量或装配槽无效。"; return false;
        }
        public static bool ValidateOwnership(RoleLoadoutController role, WeaponOperation operation, out string error)
        {
            error = null;
            if (role == null || !role.TryGetOwnedRuntimeItem(operation.TargetItemId, out _)
                || operation.CandidateItemId.IsValid && !role.TryGetOwnedRuntimeItem(operation.CandidateItemId, out _))
            { error = "操作物品不属于当前角色。"; return false; }
            return true;
        }
        public static bool TryApply(RoleLoadoutController role, WeaponOperation operation, out string error) =>
            TryApply(role, operation, out _, out error);

        internal static bool TryApply(RoleLoadoutController role, WeaponOperation operation,
            out LoadoutCommitRecord commit, out string error)
        {
            commit = null;
            if (!Validate(operation, out error) || !ValidateOwnership(role, operation, out error)) return false;
            if (role == null || !role.IsAuthority || !role.isActiveAndEnabled)
            { error = "角色权威库存尚未就绪。"; return false; }
            return role.TryApplyWeaponOperation(operation, out commit, out error);
        }
    }

}
