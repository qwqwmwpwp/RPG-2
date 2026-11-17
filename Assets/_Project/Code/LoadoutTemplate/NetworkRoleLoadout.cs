using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Mirror;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>角色库存的网络边界。服务器持有 Role，拥有者 UI 只持有独立只读投影，主机也走相同请求管线。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity))]
    public sealed partial class NetworkRoleLoadout : NetworkBehaviour, ILoadoutCandidateCodec
    {
        // 状态读取保留上一版持久数据，内容配置签名不随请求/结果线格式升级。
        private const int ConfigurationSchema = 4;
        private const int Schema = LoadoutStateCodec.Schema, MaxWireBytes = LoadoutStateCodec.MaxWireBytes, MaxCells = LoadoutStateCodec.MaxCells;
        private const int MaxCatalog = 256;
        [SerializeField] private RoleLoadoutController role;
        [SerializeField] private MonoBehaviour presentation;
        private IRoleLoadoutPresentation Presentation => presentation != null ? presentation as IRoleLoadoutPresentation : null;
        [SerializeField] private LoadoutItemDefinition[] itemCatalog = Array.Empty<LoadoutItemDefinition>();
        [SerializeField, Min(1f)] private float requestTimeout = 6f;
        private readonly Dictionary<string, LoadoutItemDefinition> catalog = new Dictionary<string, LoadoutItemDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, ILoadoutEndpoint> serverEndpoints = new Dictionary<string, ILoadoutEndpoint>(StringComparer.Ordinal);
        private readonly ProjectionEndpoint[] projection = new ProjectionEndpoint[14];
        private readonly List<EventSubscription> clientSubscriptions = new List<EventSubscription>();
        private readonly object providerSource = new object();
        private ServerLoadoutOperations serverOperations;
        private LoadoutStateCodec stateCodec;
        private LootStateCodec lootCodec;
        private readonly HashSet<string> retiredClientSessions = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, Receipt> receipts = new Dictionary<int, Receipt>();
        private string configuration, clientEpoch, clientSession, serverClientSession, ownerKey;
        private string serverEpoch => role != null ? role.CommitCoordinator.Epoch : null;
        private long serverRevision => role != null ? role.CommitCoordinator.Revision : 0;
        private long clientRevision = -1;
        private bool serverInitialized, operationsAllowed, clientOperationsAllowed, projectionReady, localInputAllowed, localStarted;
        private int nextRequest, pendingRequest, highestRequest, syncSequence, pendingSync;
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
        private sealed class Receipt { internal string request; internal LoadoutOperationOutcome outcome; }
        private sealed class ProjectionEndpoint
        {
            internal readonly object source = new object();
            internal ContainerSnapshot snapshot;
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
                    CreateClientSubscriptions();
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
        public override void OnStopServer()
        {
            CloseServerLoot();
            role?.ClearRuntimeReservations();
            serverInitialized = false; operationsAllowed = false;
            serverClientSession = null; retiredClientSessions.Clear(); receipts.Clear(); serverEndpoints.Clear();
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

        public void SuspendServerOperations()
        {
            if (!NetworkServer.active || !serverInitialized) return;
            if (!operationsAllowed) return;
            role.CommitCoordinator.CommitSystemState(CreateOperationKey(LoadoutOperationChannel.System),
                () => { operationsAllowed = false; receipts.Clear(); },
                () => SendSnapshot(connectionToClient, 0, false, "角色库存操作已暂停。"));
        }

        public string CaptureServerState()
        {
            if (!NetworkServer.active || !serverInitialized || !TryCapture(out string state, out _)) return null;
            return state;
        }

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
                if (!TryReadState(state, out StateWire wire, out Candidate candidate, out error)) return false;
                ulong highWater = 0;
                foreach (ItemDisplayData item in candidate.items)
                {
                    if (item == null) continue;
                    foreach (RuntimeItem node in RuntimeNodes(item)) highWater = Math.Max(highWater, node.ItemId.Value);
                }
                try { LoadoutRuntimeItems.SharedItemIds.Observe(new ItemId(highWater)); }
                catch (ArgumentOutOfRangeException exception) { return Fail(exception.Message, out error); }
                LoadoutStateVersion before = role.CaptureStateVersion();
                var dependencies = new Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot>();
                foreach (RoleSlotConfiguration config in role.Configuration)
                {
                    ILoadoutEndpoint slot = role.GetSlot(config.slotId);
                    dependencies.Add(slot, LoadoutRuleSnapshot.CaptureEndpoint(slot, slot.Snapshot));
                    if ((int)config.slotId < 4)
                    {
                        ILoadoutEndpoint grid = config.containerController;
                        dependencies.Add(grid, LoadoutRuleSnapshot.CaptureEndpoint(grid, grid.Snapshot));
                    }
                }
                var prepared = new PreparedLoadoutOperation(new Dictionary<ILoadoutEndpoint, List<ItemDisplayData>>(),
                    new Dictionary<ILoadoutEndpoint, object>(), dependencies,
                    selectedWeaponChanged: role.SelectedWeaponSlot != candidate.selectedWeapon, applyState: () =>
                    {
                        role.ClearRuntimeReservations();
                        role.ApplySelectedWeapon(candidate.selectedWeapon);
                        // 已完成完整解码与规则检查，按持久状态恢复槽位和网格版本。
                        foreach (RoleSlotConfiguration config in role.Configuration)
                        {
                            int index = (int)config.slotId;
                            var endpoint = (LoadoutSlotEndpoint)role.GetSlot(config.slotId);
                            endpoint.ApplyNetworkState(candidate.slots[index].id, candidate.items[index], candidate.slots[index].revision);
                        }
                        foreach (RoleSlotConfiguration config in role.Configuration)
                            if ((int)config.slotId < 4)
                                config.containerController.ApplyNetworkStorage(candidate.items[(int)config.slotId]?.Storage,
                                    candidate.slots[(int)config.slotId].storageRevision);
                        CloseServerLoot();
                        highestRequest = 0;
                        receipts.Clear();
                        ClearRuntimeServerOperations();
                        operationsAllowed = true;
                    });
                if (!role.CommitCoordinator.TryCommit(prepared, CreateOperationKey(LoadoutOperationChannel.System), before,
                    null, out LoadoutCommitRecord commit, out failure, restoredEpoch: Guid.NewGuid().ToString("N")))
                    return Fail(failure.Reason, out error);
                role.QueueCommitted(commit, () => SendSnapshot(connectionToClient, 0, true, null));
                ReindexServer();
            }
            role.FlushCommitted();
            return true;
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

        private bool BuildCatalog(out string error)
        {
            try { return BuildCatalogCore(out error); }
            catch (Exception exception)
            {
                return Fail("物品目录构建失败，" + exception.Message, out error);
            }
        }

        private bool BuildCatalogCore(out string error)
        {
            error = null; catalog.Clear();
            if (role == null || role.GetComponentInParent<NetworkRoleLoadout>(true) != this
                || role.Configuration == null || role.Configuration.Count != 10)
                return Fail("需要四容器及六装备槽配置。", out error);
            var text = new StringBuilder("loadout-schema-").Append(ConfigurationSchema);
            var sorted = new List<RoleSlotConfiguration>(role.Configuration);
            sorted.Sort((a, b) => (a == null ? -1 : (int)a.slotId).CompareTo(b == null ? -1 : (int)b.slotId));
            int mask = 0;
            foreach (RoleSlotConfiguration config in sorted)
            {
                if (config == null || (int)config.slotId < 0 || (int)config.slotId > 9 || config.rule == null
                    || (mask & (1 << (int)config.slotId)) != 0) return Fail("槽位配置无效或重复。", out error);
                mask |= 1 << (int)config.slotId;
                text.Append('|').Append((int)config.slotId).Append(':').Append(config.rule.ConfigurationSignature);
                if ((int)config.slotId < 4)
                {
                    var container = config.containerController;
                    if (container == null || container.GetComponentInParent<NetworkRoleLoadout>(true) != this
                        || !container.isActiveAndEnabled || (int)container.ConfiguredKind != (int)config.slotId + 1)
                        return Fail("四个容器的类别与槽位不匹配。", out error);
                    if (container.ConfiguredCellCount > MaxCells
                        || !container.TryGetConfiguredShape(out List<Vector2Int> cells, out error) || cells.Count > MaxCells)
                        return Fail(error ?? "容器有效格超过协议上限。", out error);
                    text.Append(':').Append(container.ConfigurationSignature).Append(':').Append(container.Definition != null ? container.Definition.DefinitionId : "$container/" + (int)container.ConfiguredKind);
                    if (container.Definition != null && !AddDefinition(container.Definition, out error)) return false;
                    foreach (ContainerInitialContent content in container.InitialContents ?? Array.Empty<ContainerInitialContent>())
                        if (content == null || content.definition == null || content.count < 1 || !AddDefinition(content.definition, out error))
                            return Fail(error ?? "容器初始内容缺少有效物品定义或数量。", out error);
                }
            }
            foreach (RoleSlotConfiguration config in sorted)
                if (config.initialItem != null && !AddDefinition(config.initialItem, out error)) return false;
            foreach (LoadoutItemDefinition definition in role.RuntimeDefinitions ?? Array.Empty<LoadoutItemDefinition>())
                if (definition == null || !AddDefinition(definition, out error)) return Fail(error ?? "运行物品显示定义含空项。", out error);
            if (role.ManifestCatalog != null)
            {
                var manifests = new List<ItemManifest>(role.ManifestCatalog.Manifests);
                manifests.Sort((a, b) => string.CompareOrdinal(a != null ? a.ItemType.Name : null, b != null ? b.ItemType.Name : null));
                var types = new HashSet<string>(StringComparer.Ordinal);
                foreach (ItemManifest manifest in manifests)
                {
                    if (manifest == null || !types.Add(manifest.ItemType.Name)) return Fail("运行物品目录存在空项或重复类型。", out error);
                    try { manifest.ValidateConfiguration(); } catch (Exception exception) { return Fail(exception.Message, out error); }
                    text.Append('|').Append(LoadoutRuntimeItems.ManifestSignature(manifest));
                }
            }
            foreach (LoadoutItemDefinition definition in itemCatalog ?? Array.Empty<LoadoutItemDefinition>())
                if (definition == null || !AddDefinition(definition, out error)) return Fail(error ?? "物品目录含空定义。", out error);
            var ids = new List<string>(catalog.Keys); ids.Sort(StringComparer.Ordinal);
            foreach (string id in ids) text.Append('|').Append(id).Append(':').Append(catalog[id].ConfigurationSignature);
            using (SHA256 hash = SHA256.Create()) configuration = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
            var rules = new LoadoutSlotRule[10];
            var containers = new LoadoutCodecContainer[4];
            foreach (RoleSlotConfiguration config in sorted)
            {
                int i = (int)config.slotId; rules[i] = config.rule;
                if (i >= 4) continue;
                var container = config.containerController;
                if (!container.TryGetConfiguredShape(out List<Vector2Int> shape, out error)) return false;
                containers[i] = new LoadoutCodecContainer(container.Definition, shape,
                    container.Definition == null ? container.CreateConfiguredBody("preview", null) : null);
            }
            var codecConfiguration = new LoadoutCodecConfiguration(configuration, catalog, role.ManifestCatalog, rules, containers);
            stateCodec = new LoadoutStateCodec(codecConfiguration);
            lootCodec = new LootStateCodec(codecConfiguration, stateCodec);
            return true;
        }

        private bool AddDefinition(LoadoutItemDefinition definition, out string error)
        {
            error = null;
            string id = definition.DefinitionId;
            if (!ValidId(id) || id.StartsWith("$", StringComparison.Ordinal)) return Fail("物品定义编号无效。", out error);
            if (catalog.TryGetValue(id, out LoadoutItemDefinition existing))
                return existing == definition || Fail("物品目录含重复定义编号。", out error);
            if (catalog.Count >= MaxCatalog) return Fail("物品目录超过协议上限。", out error);
            if (definition.Manifest != null)
            {
                if (role.ManifestCatalog == null || !role.ManifestCatalog.TryGet(definition.Manifest.ItemType, out ItemManifest manifest)
                    || manifest != definition.Manifest) return Fail("运行定义的清单未登记在角色授权目录。", out error);
                try { manifest.ValidateConfiguration(); } catch (Exception exception) { return Fail(exception.Message, out error); }
                foreach (LoadoutItemDefinition existingDefinition in catalog.Values)
                    if (existingDefinition.Manifest != null && existingDefinition.Manifest.ItemType.MatchesTagExact(manifest.ItemType))
                        return Fail("一个运行物品类型只能登记一个显示定义。", out error);
            }
            catalog.Add(id, definition); return true;
        }

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
                highestRequest = 0;
                receipts.Clear();
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
            projectionReady = true;
            ApplyProjection(candidate);
            RefreshInput(); NotifyState();
            EventBus.Global.Publish(LoadoutChannels.RoleAvailable, providerSource, ownerKey);
            if (outcome != null && NeedsResync(outcome.Code)) RequestResync();
        }

        private void CreateClientSubscriptions()
        {
            for (int i = 0; i < projection.Length; i++)
            {
                var endpoint = new ProjectionEndpoint(); projection[i] = endpoint;
                clientSubscriptions.Add(EventBus.Global.Subscribe<LoadoutSnapshotRequest>(LoadoutChannels.SnapshotRequested,
                    endpoint.source, this, request => EventBus.Global.Publish(LoadoutChannels.SnapshotReceived, request.ReplySource,
                        new LoadoutSnapshotReply(request.RequestId, endpoint.snapshot, endpoint.snapshot == null ? "库存尚未同步。" : null))));
                clientSubscriptions.Add(EventBus.Global.Subscribe<LoadoutOperationRequest>(LoadoutChannels.OperationRequested,
                    endpoint.source, this, OnLocalOperation));
            }
            clientSubscriptions.Add(EventBus.Global.Subscribe<LoadoutRoleConnectRequest>(LoadoutChannels.RoleConnectRequested,
                EventBus.AnySource, this, OnRoleConnect));
        }

        private void OnRoleConnect(LoadoutRoleConnectRequest request)
        {
            if (request.Session == null || !request.Session.IsActive || request.ReplySource == null
                || !string.Equals(request.Session.OwnerKey, ownerKey, StringComparison.Ordinal)
                || Presentation == null || !Presentation.OwnsSession(request.Session)) return;
            var result = new List<LoadoutRoleBinding>();
            if (projectionReady)
                for (int i = 0; i < 10; i++) result.Add(new LoadoutRoleBinding((LoadoutSlotId)i,
                    projection[i].source, i < 4 ? projection[10 + i].source : null,
                    projection[i].snapshot, i < 4 ? projection[10 + i].snapshot : null));
            EventBus.Global.Publish(LoadoutChannels.RoleConnected, request.ReplySource,
                new LoadoutRoleConnectReply(request, providerSource, result, projectionReady ? null : "库存尚未同步。"));
        }

        private void OnLocalOperation(LoadoutOperationRequest request)
        {
            if (TryRouteLootOperation(request)) return;
            var session = request.Session as LoadoutWindowSession;
            ProjectionEndpoint source = FindProjection(request.Source), target = FindProjection(request.Target);
            if (!localStarted || !isLocalPlayer || !projectionReady || !clientOperationsAllowed || !localInputAllowed
                || session == null || session.OwnerKey != ownerKey || Presentation == null || !Presentation.OwnsSession(session) || !Presentation.IsWindowOpen
                || source?.snapshot == null || target?.snapshot == null || pendingRequest != 0 || pendingRuntimeRequest != 0 || nextRequest == int.MaxValue || awaitingSync)
            {
                EventBus.Global.Publish(LoadoutChannels.OperationCompleted, request.Session,
                    new LoadoutOperationResult(request, LocalOutcome(LoadoutResultCode.NotReady, "当前会话或库存端点不可用。"))); return;
            }
            pendingOperation = request; pendingRequest = ++nextRequest;
            pendingOperationKey = ClientOperationKey(LoadoutOperationChannel.Inventory, pendingRequest);
            LoadoutDiagnostics.BeginRequest(pendingOperationKey);
            requestDeadline = Time.unscaledTime + requestTimeout;
            var intent = request.Intent;
            var wire = new OperationWire { requestId = pendingRequest, configuration = configuration,
                epoch = clientEpoch, session = clientSession, roleRevision = clientRevision, source = intent.SourceContainerId, target = intent.TargetContainerId,
                item = intent.ItemId, targetItem = intent.TargetItemId, sourceOrigin = intent.SourceOrigin, origin = intent.Origin,
                targetOrigin = intent.TargetItemOrigin, sourceRevision = intent.SourceVersion, targetRevision = intent.TargetVersion,
                kind = (int)intent.Kind, rotated = intent.Rotated ?? false };
            CmdOperate(JsonUtility.ToJson(wire));
        }

        private ProjectionEndpoint FindProjection(object source)
        {
            foreach (ProjectionEndpoint endpoint in projection) if (endpoint != null && ReferenceEquals(endpoint.source, source)) return endpoint;
            return null;
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
            foreach (EventSubscription subscription in clientSubscriptions) subscription.Dispose();
            clientSubscriptions.Clear();
            Array.Clear(projection, 0, projection.Length);
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

        private void ApplyProjection(Candidate candidate)
        {
            projectedWeaponSlot = candidate.selectedWeapon;
            foreach (RoleSlotConfiguration config in role.Configuration)
            {
                int i = (int)config.slotId; ItemDisplayData item = candidate.items[i]; SlotWire slot = candidate.slots[i];
                projection[i].snapshot = new ContainerSnapshot(slot.id, item?.ContainerType ?? ContainerKind.None, item?.Icon,
                    item == null ? Array.Empty<ItemDisplayData>() : new[] { item }, eventSource: projection[i].source,
                    revision: slot.revision, isSlot: true, rule: config.rule, rejectContainers: false);
                if (i < 4)
                {
                    BackpackContainerData storage = item?.Storage;
                    projection[10 + i].snapshot = new ContainerSnapshot(storage?.Id ?? slot.id + "/empty",
                        storage?.Kind ?? config.containerController.ConfiguredKind, storage?.Icon, storage?.Items, storage?.ValidCells,
                        projection[10 + i].source, slot.storageRevision, isReady: storage != null);
                }
            }
            // 完整批次写入后才通知视图，监听者不会观察到两个端点各一半的交换。
            foreach (ProjectionEndpoint endpoint in projection) EventBus.Global.Publish(LoadoutChannels.Changed, endpoint.source);
            role.PublishRuntimeChanged();
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
        private static IEnumerable<RuntimeItem> RuntimeNodes(ItemDisplayData item)
        {
            if (item.RuntimeItem != null) foreach (RuntimeItem node in ItemAssemblyTraversal.Enumerate(item.RuntimeItem)) yield return node;
            if (item.Storage != null) foreach (ItemDisplayData child in item.Storage.Items)
                foreach (RuntimeItem node in RuntimeNodes(child)) yield return node;
        }
        private static bool ValidToken(string token) => LoadoutStateCodec.ValidToken(token);
        private static bool Bounded(string value, int max) => LoadoutStateCodec.Bounded(value, max);
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
