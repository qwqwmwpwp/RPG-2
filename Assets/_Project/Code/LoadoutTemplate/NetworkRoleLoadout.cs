using System;
using System.Collections.Generic;
using System.Text;
using Mirror;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>角色库存的网络边界。服务器持有 Role，拥有者 UI 只持有独立只读投影，主机也走相同请求管线。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity))]
    public sealed partial class NetworkRoleLoadout : NetworkBehaviour, ILoadoutCandidateCodec
    {
        private const int Schema = LoadoutStateCodec.Schema, MaxWireBytes = LoadoutStateCodec.MaxWireBytes;
        [SerializeField] private RoleLoadoutController role;
        [SerializeField] private MonoBehaviour presentation;
        private IRoleLoadoutPresentation Presentation => presentation != null ? presentation as IRoleLoadoutPresentation : null;
        [SerializeField] private LoadoutItemDefinition[] itemCatalog = Array.Empty<LoadoutItemDefinition>();
        [SerializeField, Min(1f)] private float requestTimeout = 6f;
        private readonly NetworkLoadoutCatalog catalog = new NetworkLoadoutCatalog();
        private readonly Dictionary<string, ILoadoutEndpoint> serverEndpoints = new Dictionary<string, ILoadoutEndpoint>(StringComparer.Ordinal);
        private readonly ClientLoadoutProjection projection = new ClientLoadoutProjection();
        private readonly object providerSource = new object();
        private ServerLoadoutOperations serverOperations;
        private LoadoutStateCodec stateCodec;
        private LootStateCodec lootCodec;
        private readonly HashSet<string> retiredClientSessions = new HashSet<string>(StringComparer.Ordinal);
        private readonly NetworkLoadoutReceipts receipts = new NetworkLoadoutReceipts();
        private string configuration => catalog.Signature;
        private string clientEpoch, clientSession, serverClientSession, ownerKey;
        private string serverEpoch => role != null ? role.CommitCoordinator.Epoch : null;
        private long serverRevision => role != null ? role.CommitCoordinator.Revision : 0;
        private long clientRevision = -1;
        private bool serverInitialized, operationsAllowed, clientOperationsAllowed, projectionReady, localInputAllowed, localStarted;
        private int nextRequest, pendingRequest, syncSequence, pendingSync;
        private bool lastReady, lastWindow;
        public event Action Changed;
        private LoadoutOperationRequest pendingOperation;
        private float requestDeadline, syncDeadline;
        private bool awaitingSync;
        private float initializationRetryAt;
        private string initializationError;

        public bool IsReady => isLocalPlayer ? projectionReady && clientOperationsAllowed : isServer && serverInitialized && operationsAllowed;
        public RoleLoadoutController Role => role;
        public bool IsWindowOpen => isLocalPlayer && Presentation != null && Presentation.IsWindowOpen;

        [Serializable] private sealed class OperationWire
        {
            public int schema = Schema, requestId, sourceRevision, targetRevision, kind, openId;
            public long roleRevision;
            public string configuration, epoch, session, source, target, item, targetItem;
            public Vector2Int sourceOrigin, origin, targetOrigin;
            public bool rotated;
        }

        private void Awake()
        {
            if (role == null) role = GetComponentInChildren<RoleLoadoutController>(true);
            Presentation?.Initialize(transform, role != null ? role.transform : null);
        }

        public override void OnStartServer()
        {
            if (!InitializeServer(out string error)) Debug.LogError("角色网络库存初始化失败：" + error, this);
        }

        public override void OnStartClient() { if (!isLocalPlayer) Presentation?.EndSession(); }

        public override void OnStartLocalPlayer()
        {
            BeginLocalSession();
        }

        /// <summary>建立本地拥有者会话，注册投影并请求首次全量同步。</summary>
        private void BeginLocalSession()
        {
            if (localStarted || Time.unscaledTime < initializationRetryAt) return;
            localStarted = true;
            clientSession = Guid.NewGuid().ToString("N");
            ownerKey = "network-loadout/" + netId + "/" + clientSession;
            string error = null;
            try
            {
                if (role == null || Presentation == null)
                    error = "角色网络背包缺少权威控制器或展示组件。";
                else if (BuildCatalog(out error) && Presentation.BeginSession(ownerKey, out error))
                {
                    Presentation.WindowChanged += NotifyState;
                    Presentation.LootClosed += CloseLoot;
                    projection.BeginRoleSession(this, OnLocalOperation, OnRoleConnect);
                    RefreshInput();
                    RequestResync();
                    initializationRetryAt = 0f;
                    initializationError = null;
                    return;
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
            FailLocalInitialization(error ?? "角色网络背包初始化未完成。");
        }

        private void FailLocalInitialization(string error)
        {
            bool report = !string.Equals(initializationError, error, StringComparison.Ordinal);
            initializationError = error;
            // 复用请求超时作为重试间隔，同一原因只报告一次。
            initializationRetryAt = Time.unscaledTime + Mathf.Max(1f, requestTimeout);
            ClearClient(resetInitializationFailure: false);
            if (report) Debug.LogError("角色网络背包初始化失败，" + error, this);
        }

        public override void OnStopLocalPlayer() => ClearClient();
        public override void OnStopAuthority() => ClearClient();
        public override void OnStopClient() => ClearClient();
        /// <summary>关闭服务器搜刮、清除角色预留和请求处理状态。</summary>
        public override void OnStopServer()
        {
            CloseServerLoot();
            role?.ClearRuntimeReservations();
            serverInitialized = false; operationsAllowed = false;
            serverClientSession = null; retiredClientSessions.Clear(); receipts.DiscardReceipts(); serverEndpoints.Clear();
            serverOperations = null;
            ClearRuntimeServerOperations();
        }
        private void OnDestroy() { CloseServerLoot(); ClearClient(); }

        public void SetLocalInputAllowed(bool allowed)
        {
            if (localInputAllowed == allowed) return;
            localInputAllowed = allowed;
            RefreshInput();
        }

        private void RefreshInput()
        {
            bool allowed = localStarted && isLocalPlayer && projectionReady && clientOperationsAllowed && localInputAllowed;
            if (!allowed) CloseLoot();
            Presentation?.SetInputAllowed(allowed);
        }

        public bool InitializeServer(out string error)
        {
            error = null;
            // PlayerInitializer 在 Spawn 前调用；此时 NetworkServer.active 为真，isServer 尚未设置。
            if (!NetworkServer.active) return Fail("服务器尚未启动。", out error);
            if (serverInitialized) return true;
            if (role == null) role = GetComponentInChildren<RoleLoadoutController>(true);
            if (role == null || !role.isActiveAndEnabled || !BuildCatalog(out error) || !role.EnsureInitialized(out error))
                return Fail(error ?? "未配置角色装备控制器。", out error);
            role.CommitCoordinator.ResetVersion(Guid.NewGuid().ToString("N"));
            serverInitialized = true; operationsAllowed = true;
            serverOperations = new ServerLoadoutOperations(role, this);
            ReindexServer();
            if (!TryCapture(out _, out error)) { serverInitialized = false; operationsAllowed = false; return false; }
            return true;
        }

        /// <summary>在权威提交中暂停库存操作并向拥有者发布状态。</summary>
        public void SuspendServerOperations()
        {
            if (!NetworkServer.active || !serverInitialized) return;
            if (!operationsAllowed) return;
            role.CommitCoordinator.CommitSystemState(CreateOperationKey(LoadoutOperationChannel.System),
                () => { operationsAllowed = false; receipts.DiscardReceipts(); },
                () => SendSnapshot(connectionToClient, 0, false, "角色库存操作已暂停。"));
        }

        public string CaptureServerState()
        {
            if (!NetworkServer.active || !serverInitialized || !TryCapture(out string state, out _)) return null;
            return state;
        }

        /// <summary>在独占恢复事务中解码存档、提交角色状态，并在事务结束后发布变更。</summary>
        public bool RestoreServerState(string state, out string error)
        {
            error = null;
            if (!NetworkServer.active) return Fail("服务器未就绪。", out error);
            if (role != null && (role.CommitCoordinator.IsExecuting || role.CommitCoordinator.IsPublishing))
                return Fail("角色旧世代库存事务尚未结束。", out error);
            if (!InitializeServer(out error)) return false;
            if (!role.CommitCoordinator.TryEnterRestore(out IDisposable transaction, out LoadoutFailure failure))
                return Fail(failure.Reason, out error);
            using (transaction)
            {
                if (!TryReadState(state, out _, out Candidate candidate, out error)) return false;
                if (!NetworkLoadoutRestore.TryPrepare(role, candidate, ResetRestoredSession, out LoadoutStateVersion before,
                    out PreparedLoadoutOperation prepared, out error)) return false;
                if (!role.CommitCoordinator.TryCommit(prepared, CreateOperationKey(LoadoutOperationChannel.System), before,
                    null, out LoadoutCommitRecord commit, out failure, restoredEpoch: Guid.NewGuid().ToString("N")))
                    return Fail(failure.Reason, out error);
                role.QueueCommitted(commit, () => SendSnapshot(connectionToClient, 0, true, null));
                ReindexServer();
            }
            role.FlushCommitted();
            return true;
        }

        /// <summary>恢复权威角色状态后关闭旧搜刮并重置本世代的请求编号域。</summary>
        private void ResetRestoredSession()
        {
            CloseServerLoot();
            receipts.Reset();
            ClearRuntimeServerOperations();
            operationsAllowed = true;
        }

        private void NotifyState()
        {
            bool ready = IsReady, open = IsWindowOpen;
            if (ready == lastReady && open == lastWindow) return;
            lastReady = ready; lastWindow = open; Changed?.Invoke();
        }

        private void ReindexServer()
        {
            serverEndpoints.Clear();
            foreach (RoleSlotConfiguration config in role.Configuration)
            {
                ILoadoutEndpoint endpoint = role.GetSlot(config.slotId);
                serverEndpoints.Add(endpoint.Snapshot.Id, endpoint);
                if ((int)config.slotId < 4 && config.containerController.Container != null)
                    serverEndpoints.Add(config.containerController.Snapshot.Id, config.containerController);
            }
        }

        /// <summary>从当前组件配置建立服务端与客户端共用的角色及搜刮编解码器。</summary>
        private bool BuildCatalog(out string error)
        {
            try
            {
                if (!catalog.TryBuild(this, role, itemCatalog, out LoadoutCodecConfiguration settings, out error)) return false;
                stateCodec = new LoadoutStateCodec(settings);
                lootCodec = new LootStateCodec(settings, stateCodec);
                return true;
            }
            catch (Exception exception)
            {
                return Fail("物品目录构建失败，" + exception.Message, out error);
            }
        }

        /// <summary>校验拥有者与同步编号，按客户端会话切换回执表并返回完整快照。</summary>
        [Command]
        private void CmdRequestSnapshot(string session, int syncId, NetworkConnectionToClient sender = null)
        {
            if (!Owns(sender) || !ValidToken(session) || syncId <= 0 || !serverInitialized) return;
            if (!string.Equals(serverClientSession, session, StringComparison.Ordinal))
            {
                if (retiredClientSessions.Contains(session)) return;
                if (!string.IsNullOrEmpty(serverClientSession)) retiredClientSessions.Add(serverClientSession);
                CloseServerLoot();
                serverLootOpenId = 0;
                serverClientSession = session;
                receipts.Reset();
                ClearRuntimeServerOperations();
            }
            SendSnapshot(sender, 0, true, null, syncId);
        }

        [Command]
        private void CmdOperate(string json, NetworkConnectionToClient sender = null)
        { ExecuteInventoryRequest(json, null, false, sender); }

        private bool Owns(NetworkConnectionToClient sender) => NetworkServer.active && sender != null
            && sender == connectionToClient && sender.identity == netIdentity && sender.isAuthenticated && sender.isReady;

        bool ILoadoutCandidateCodec.ValidateCandidate(Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates,
            long revision, LootSource external, out string error)
        {
            if (!TryCapture(out _, out error, candidates, revision)) return false;
            return external == null || TryEncodeLoot(external, out _, out error, candidates);
        }

        private void SendSnapshot(NetworkConnectionToClient owner, int requestId, bool success, string error, int syncId = 0)
        {
            if (owner == null || !isServer || string.IsNullOrEmpty(serverClientSession)) return;
            if (!TryCapture(out string json, out string captureError))
            { Debug.LogError("角色库存快照失败：" + captureError, this); return; }
            TargetReceive(owner, serverClientSession, requestId, syncId, success, BoundResultMessage(error), json, string.Empty);
        }

        /// <summary>核对操作结果和完整快照，仅以当前显式同步响应切换客户端世代。</summary>
        [TargetRpc]
        private void TargetReceive(NetworkConnectionToClient target, string session, int requestId, int syncId, bool success, string error, string json, string outcomeJson)
        {
            if (!localStarted || !isLocalPlayer || !string.Equals(session, clientSession, StringComparison.Ordinal)) return;
            LoadoutOperationOutcome outcome = DecodeOutcome(outcomeJson);
            if (requestId != 0 && outcome == null)
            {
                if (requestId == pendingRequest) FailPending("库存结果载荷无效。", LoadoutResultCode.SnapshotInvalid);
                RequestResync(); return;
            }
            if (outcome != null && (outcome.Key.ActorNetId != netId || outcome.Key.Session != clientSession
                || outcome.Key.Epoch != clientEpoch || outcome.Key.RequestId != requestId
                || requestId == pendingRequest && outcome.Key.Channel != pendingOperationKey.Channel)) return;
            if (!TryReadState(json, out StateWire wire, out Candidate candidate, out string decodeError))
            {
                projectionReady = false; awaitingSync = false; RefreshInput();
                if (outcome != null && requestId == pendingRequest) CompletePending(outcome);
                else FailPending(decodeError, LoadoutResultCode.SnapshotInvalid);
                Debug.LogWarning("角色库存同步被拒绝：" + decodeError, this);
                // 同步响应本身无效时延后重试，主机同步 RPC 不在同一调用栈递归请求。
                awaitingSync = true; syncDeadline = Time.unscaledTime + requestTimeout;
                return;
            }
            bool newEpoch = !string.Equals(clientEpoch, wire.epoch, StringComparison.Ordinal);
            if (!newEpoch && wire.revision < clientRevision)
            {
                if (outcome != null && requestId == pendingRequest) CompletePending(outcome);
                if (outcome != null && NeedsResync(outcome.Code)) RequestResync();
                return;
            }
            if (newEpoch && (!awaitingSync || syncId != pendingSync))
            {
                if (outcome != null && requestId == pendingRequest) CompletePending(outcome);
                RequestResync(); return;
            }
            if (newEpoch) CloseLoot();
            clientEpoch = wire.epoch; clientRevision = wire.revision; clientOperationsAllowed = wire.accepting;
            if (syncId == pendingSync) awaitingSync = false;
            if (pendingRequest != 0 && requestId == pendingRequest && outcome != null)
                CompletePending(outcome);
            else if (newEpoch) FailPending("角色会话已改变。");
            if (!wire.accepting) FailPending(error);
            PublishRoleProjection(candidate);
            if (outcome != null && NeedsResync(outcome.Code)) RequestResync();
        }

        /// <summary>仅向当前拥有者的展示会话返回角色槽位与容器绑定。</summary>
        private void OnRoleConnect(LoadoutRoleConnectRequest request)
        {
            if (request.Session == null || !request.Session.IsActive || request.ReplySource == null
                || !string.Equals(request.Session.OwnerKey, ownerKey, StringComparison.Ordinal)
                || Presentation == null || !Presentation.OwnsSession(request.Session)) return;
            var result = projectionReady ? projection.GetRoleBindings() : null;
            EventBus.Global.Publish(LoadoutChannels.RoleConnected, request.ReplySource,
                new LoadoutRoleConnectReply(request, providerSource, result, projectionReady ? null : "库存尚未同步。"));
        }

        /// <summary>根据投影地址分流搜刮请求，并校验角色窗口的库存操作准入。</summary>
        private void OnLocalOperation(LoadoutOperationRequest request)
        {
            ContainerSnapshot source = projection.FindSnapshot(request.Source, out bool fromLoot);
            ContainerSnapshot target = projection.FindSnapshot(request.Target, out bool toLoot);
            if (fromLoot || toLoot)
            {
                SubmitLootOperation(request, source != null && target != null);
                return;
            }
            var session = request.Session as LoadoutWindowSession;
            if (!localStarted || !isLocalPlayer || !projectionReady || !clientOperationsAllowed || !localInputAllowed
                || session == null || session.OwnerKey != ownerKey || Presentation == null || !Presentation.OwnsSession(session) || !Presentation.IsWindowOpen
                || source == null || target == null || pendingRequest != 0 || pendingRuntimeRequest != 0 || nextRequest == int.MaxValue || awaitingSync)
            {
                EventBus.Global.Publish(LoadoutChannels.OperationCompleted, request.Session,
                    new LoadoutOperationResult(request, LocalOutcome(LoadoutResultCode.NotReady, "当前会话或库存端点不可用。"))); return;
            }
            SendClientOperation(request, LoadoutOperationChannel.Inventory);
        }

        /// <summary>为库存和搜刮共用的请求序列登记待办，按通道发送线协议。</summary>
        private void SendClientOperation(LoadoutOperationRequest request, LoadoutOperationChannel channel)
        {
            pendingOperation = request; pendingRequest = ++nextRequest;
            pendingOperationKey = ClientOperationKey(channel, pendingRequest);
            LoadoutDiagnostics.BeginRequest(pendingOperationKey);
            requestDeadline = Time.unscaledTime + requestTimeout;
            LoadoutDropIntent intent = request.Intent;
            bool loot = channel == LoadoutOperationChannel.Loot;
            var wire = new OperationWire { requestId = pendingRequest, configuration = configuration,
                epoch = clientEpoch, session = clientSession, roleRevision = clientRevision, openId = loot ? lootOpenSequence : 0,
                source = intent.SourceContainerId, target = intent.TargetContainerId, item = intent.ItemId, targetItem = intent.TargetItemId,
                sourceOrigin = intent.SourceOrigin, origin = intent.Origin, targetOrigin = intent.TargetItemOrigin,
                sourceRevision = intent.SourceVersion, targetRevision = intent.TargetVersion, kind = (int)intent.Kind, rotated = intent.Rotated ?? false };
            string json = JsonUtility.ToJson(wire);
            if (loot) CmdLootOperate(clientLootToken, json);
            else CmdOperate(json);
        }

        private void Update()
        {
            // Mirror 的 previousLocalPlayer 可能抑制同一活动对象重获所有权时的第二次回调。
            // 不依赖回调次数，最终拥有权与 localPlayer 标记建立后恢复一个全新的会话。
            UpdateLoot();
            UpdateRuntimeOperations();
            if (isClient && isLocalPlayer && isOwned && !localStarted) BeginLocalSession();
            if (!localStarted || !isLocalPlayer) return;
            if (pendingRequest != 0 && Time.unscaledTime >= requestDeadline)
            { FailPending("服务器请求超时，正在重新同步。", LoadoutResultCode.Timeout); RequestResync(); }
            if (awaitingSync && Time.unscaledTime >= syncDeadline)
            { awaitingSync = false; RequestResync(); }
        }

        private void RequestResync()
        {
            if (!localStarted || !isLocalPlayer || string.IsNullOrEmpty(clientSession)) return;
            LoadoutDiagnostics.RecordResync(ClientOperationKey(LoadoutOperationChannel.System, syncSequence + 1));
            awaitingSync = true; syncDeadline = Time.unscaledTime + requestTimeout;
            pendingSync = ++syncSequence;
            CmdRequestSnapshot(clientSession, pendingSync);
        }

        private void FailPending(string error, LoadoutResultCode code = LoadoutResultCode.SessionExpired)
        {
            if (pendingRequest == 0) return;
            LoadoutDiagnostics.CancelRequest(pendingOperationKey);
            CompletePending(new LoadoutOperationOutcome(pendingOperationKey, code, error, null, ClientStateVersion()));
        }

        /// <summary>结束本地拥有者会话并释放投影、待办和展示绑定。</summary>
        private void ClearClient(bool resetInitializationFailure = true)
        {
            if (resetInitializationFailure)
            {
                initializationRetryAt = 0f;
                initializationError = null;
            }
            if (!localStarted) return;
            CloseLoot();
            localStarted = false; projectionReady = false; awaitingSync = false; clientOperationsAllowed = false;
            ClearRuntimeClientOperations("角色已断开连接。");
            FailPending("角色已断开连接。");
            projection.EndRoleSession();
            EventBus.Global.Publish(LoadoutChannels.RoleDisconnected, providerSource);
            if (Presentation != null)
            {
                Presentation.WindowChanged -= NotifyState;
                Presentation.LootClosed -= CloseLoot;
                Presentation.EndSession();
            }
            clientSession = null; clientEpoch = null; ownerKey = null; clientRevision = -1; NotifyState();
            role?.PublishRuntimeChanged();
        }

        /// <summary>发布已通过各自版本检查的角色快照，在写完所有端点后刷新武器、输入和窗口。</summary>
        private void PublishRoleProjection(Candidate candidate)
        {
            projectionReady = true;
            projection.ApplyRole(candidate, role.Configuration);
            role.PublishRuntimeChanged();
            RefreshInput(); NotifyState();
            EventBus.Global.Publish(LoadoutChannels.RoleAvailable, providerSource, ownerKey);
        }

        private bool TryCapture(out string json, out string error,
            Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates = null, long? candidateRevision = null, LoadoutSlotId? candidateSelected = null)
        {
            using var encodingScope = LoadoutDiagnostics.Begin(LoadoutDiagnosticStage.Encode);
            var inputs = new List<LoadoutCaptureSlot>();
            var storageCandidates = new Dictionary<BackpackContainerData, IReadOnlyList<ItemDisplayData>>();
            foreach (RoleSlotConfiguration config in role.Configuration)
            {
                int i = (int)config.slotId;
                ILoadoutEndpoint endpoint = role.GetSlot(config.slotId);
                ContainerSnapshot snapshot = endpoint.Snapshot;
                bool bodyChanged = candidates != null && candidates.ContainsKey(endpoint);
                inputs.Add(new LoadoutCaptureSlot { Slot = i, Snapshot = snapshot,
                    Items = bodyChanged ? candidates[endpoint] : snapshot.Items,
                    BodyChanged = bodyChanged, GridRevision = i < 4 ? config.containerController.Snapshot.Revision : 0,
                    GridChanged = i < 4 && candidates != null && candidates.ContainsKey(config.containerController) });
            }
            if (candidates != null)
                foreach (var entry in candidates)
                    if (entry.Key is BackpackContainerController grid && grid.Container != null)
                        storageCandidates[grid.Container] = entry.Value;
            if (!stateCodec.TryCapture(inputs, storageCandidates, serverEpoch, candidateRevision ?? serverRevision,
                    operationsAllowed, candidateSelected ?? role.SelectedWeaponSlot, out json, out error)) return false;
            if (LoadoutDiagnostics.Enabled)
                LoadoutDiagnostics.RecordSnapshot(CurrentDiagnosticKey(), "role", Encoding.UTF8.GetByteCount(json));
            return true;
        }

        private bool TryReadState(string json, out StateWire state, out Candidate candidate, out string error) =>
            stateCodec.TryReadState(json, out state, out candidate, out error);

        private static bool ValidId(string id) => LoadoutStateCodec.ValidId(id);
        private static bool ValidToken(string token) => LoadoutStateCodec.ValidToken(token);
        private static bool Bounded(string value, int max) => LoadoutStateCodec.Bounded(value, max);
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
