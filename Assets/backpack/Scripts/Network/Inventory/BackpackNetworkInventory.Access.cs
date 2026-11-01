using System;
using System.Collections.Generic;
using System.IO;
using Mirror;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    public sealed partial class BackpackNetworkInventory
    {
        [Header("共享容器（内容仅发给获授权会话）")]
        [SerializeField] private bool sharedContainer;
        [SerializeField, Min(0.1f)] private float interactionRange = 3f;
        private Func<NetworkConnectionToClient, bool> sharedPermission;

        /// <summary>角色固定 tick 调度入口。返回 true 后必须且只能调用一次 execute 或 reject；false 表示未接收。</summary>
        public Func<Action, Action<string>, bool> ServerOperationScheduler { get; set; }
        /// <summary>只绑定玩家 ingress；由权威角色检查存活与交互状态。未绑定时拒绝客户端操作。</summary>
        public Func<NetworkConnectionToClient, bool> ServerAccessValidator { get; set; }
        public bool IsSharedContainer => sharedContainer;
        public bool HasSharedSubscription => sharedClientSubscribed;
        public event Action<bool, string> SharedAccessChanged;

        // 包在普通对象中，避免 Weaver 把定向会话流注册为此 Behaviour 的普通 SyncObject。
        private sealed class SessionStream { internal readonly InventorySyncList Entries = new InventorySyncList(); }
        private readonly SessionStream sharedServerStream = new SessionStream();
        private readonly SessionStream sharedClientStream = new SessionStream();
        private sealed class Subscription
        {
            internal BackpackNetworkInventory Ingress;
            internal uint Session;
            internal ulong SentVersion;
            internal uint TransferSequence;
            internal SharedBatch Pending;
            internal int Offset;
            internal bool Queued, HasBaseline;
            internal string Error;
        }
        private readonly Dictionary<NetworkConnectionToClient, Subscription> subscribers = new Dictionary<NetworkConnectionToClient, Subscription>();
        private readonly List<NetworkConnectionToClient> revokedSubscribers = new List<NetworkConnectionToClient>();
        private readonly HashSet<BackpackNetworkInventory> openedShared = new HashSet<BackpackNetworkInventory>();
        private readonly Dictionary<uint, uint> clientSessions = new Dictionary<uint, uint>();
        private bool sharedClientSubscribed;
        private uint clientSessionCounter, sharedClientSession;
        private ulong clientTicket, serverTicketHighWater;
        private const int MaxPendingOperations = 64, ResultHistoryLimit = 256, MaxOpenContainers = 8;
        private readonly HashSet<ulong> pendingOperations = new HashSet<ulong>();
        private readonly Dictionary<ulong, Action> completedOperations = new Dictionary<ulong, Action>();
        private readonly Queue<ulong> completedOrder = new Queue<ulong>();
        private readonly Dictionary<(bool weapon, int id), ulong> requestTickets = new Dictionary<(bool, int), ulong>();
        private readonly Queue<(bool weapon, int id)> requestOrder = new Queue<(bool, int)>();
        private uint inventoryRequestHighWater, weaponRequestHighWater;
        private NetworkConnectionToClient operationOwner;

        // Host 上的 UI 使用获授权镜像；服务器事务始终使用独立的权威视图，不能受本地主机 UI 会话影响。
        private sealed class AuthorityView : IBackpackView
        {
            private readonly BackpackNetworkInventory owner;
            internal AuthorityView(BackpackNetworkInventory owner) { this.owner = owner; }
            public bool IsReady => owner.IsServerInventory;
            public int Rows => owner.Container?.Rows ?? 0;
            public int Columns => owner.Container?.Columns ?? 0;
            public int ConfiguredRows => owner.rows;
            public int ConfiguredColumns => owner.columns;
            public object EventSource => owner.Container;
            public GridCoord ResolveItemDimensions(RuntimeItem item) => InventoryContainer.GetItemDimensions(item);
            public bool CanAcceptItem(RuntimeItem item, out string error) => owner.CanAcceptItem(item, out error);
            public void CopyItems(List<RuntimeItem> destination) { if (owner.Container == null) destination.Clear(); else owner.Container.FillItemsByItemId(destination); }
            public bool TryGetPlacement(ItemId id, out InventoryPlacement placement)
            {
                placement = InventoryPlacement.Invalid;
                return owner.Container != null && owner.Container.TryGetItemById(id, out RuntimeItem item)
                    && owner.Container.TryGetPlacement(item, out placement);
            }
        }
        private AuthorityView authorityView;
        private sealed class CoreBindings
        {
            internal Func<bool> Authority;
            internal Func<ItemIdGenerator> Generator;
            internal Action<InventoryOperation> Forward, Dispatch;
            internal Action<WeaponOperation> ForwardWeapon, DispatchWeapon;
            internal Func<IBackpackView, bool> CanForward;
            internal CommitConfirmHandler Confirm;
            internal bool SingleCell;
        }
        private CoreBindings previousBindings;

        private void CaptureCoreBindings(InventoryHostCore authority)
        {
            previousBindings = new CoreBindings
            {
                Authority = authority.IsAuthority, Generator = authority.IdGeneratorProvider,
                Forward = authority.ForwardOperation, Dispatch = authority.AuthorityOperationDispatcher,
                ForwardWeapon = authority.ForwardWeaponOperation, DispatchWeapon = authority.AuthorityWeaponOperationDispatcher,
                CanForward = authority.ForwardCanSubmit, Confirm = authority.CommitConfirm,
                SingleCell = authority.SupportsSingleCellTransfers
            };
        }

        private void RestoreCoreBindings()
        {
            if (core == null || previousBindings == null) return;
            core.IsAuthority = previousBindings.Authority; core.IdGeneratorProvider = previousBindings.Generator;
            core.ForwardOperation = previousBindings.Forward; core.AuthorityOperationDispatcher = previousBindings.Dispatch;
            core.ForwardWeaponOperation = previousBindings.ForwardWeapon; core.AuthorityWeaponOperationDispatcher = previousBindings.DispatchWeapon;
            core.ForwardCanSubmit = previousBindings.CanForward; core.CommitConfirm = previousBindings.Confirm;
            core.SupportsSingleCellTransfers = previousBindings.SingleCell;
            previousBindings = null;
        }

        public void ConfigureSharedAccess(float range, Func<NetworkConnectionToClient, bool> permission = null)
        {
            if (netId != 0) throw new InvalidOperationException("共享容器模式必须在网络 Spawn 前配置。");
            sharedContainer = true;
            interactionRange = Mathf.Max(0.1f, range);
            sharedPermission = permission;
            syncMode = SyncMode.Owner;
        }

        private Action pendingOpenGranted;
        public bool IsNetworkSessionActive => NetworkClient.active || NetworkServer.active;
        public bool IsServerSession => NetworkServer.active;
        public IBackpackView InventoryView => this;
        public bool HasLocalAccess => sharedClientSubscribed && IsReady;
        public bool RequestOpenForLocalPlayer(Action granted)
        {
            BackpackNetworkInventory ingress = LocalIngress;
            if (ingress == null) return false;
            pendingOpenGranted = granted;
            SharedAccessChanged -= OnRequestedSharedAccessChanged;
            SharedAccessChanged += OnRequestedSharedAccessChanged;
            if (ingress.RequestOpenShared(this)) return true;
            SharedAccessChanged -= OnRequestedSharedAccessChanged;
            pendingOpenGranted = null;
            return false;
        }
        private void OnRequestedSharedAccessChanged(bool granted, string reason)
        {
            SharedAccessChanged -= OnRequestedSharedAccessChanged;
            Action callback = pendingOpenGranted;
            pendingOpenGranted = null;
            if (granted) callback?.Invoke();
        }
        public void CloseForLocalPlayer()
        {
            SharedAccessChanged -= OnRequestedSharedAccessChanged;
            pendingOpenGranted = null;
            LocalIngress?.RequestCloseShared(this);
        }

        private static BackpackNetworkInventory LocalIngress => NetworkClient.localPlayer != null
            ? NetworkClient.localPlayer.GetComponent<BackpackNetworkInventory>() : null;

        private ulong NextClientTicket() => ++clientTicket;

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();
            // 初次 Spawn 已有 Mirror 基线；已有外壳转归属时主动索取完整私有流。
            if (!isServer && core != null) CmdRequestOwnerBaseline();
        }

        public override void OnStopAuthority()
        {
            if (!isServer)
            {
                ClearClientAccess();
                entries.Reset(); mirror.Clear(); mirrorPlacements.Clear(); mirrorProjection.Clear();
                mirrorReady = false; mirrorDirty = false;
                pendingViewChanges.Clear(); fullViewRefreshPending = true; RaiseChanged();
            }
            base.OnStopAuthority();
        }

        [Command(requiresAuthority = true)]
        private void CmdRequestOwnerBaseline()
        {
            if (!IsServerInventory || sharedContainer || !SyncNow(out _)) return;
            using (NetworkWriterPooled writer = NetworkWriterPool.Get())
            {
                entries.OnSerializeAll(writer);
                TargetPrivateBaseline(connectionToClient, Container.Rows, Container.Columns, writer.ToArray());
            }
        }

        [TargetRpc]
        private void TargetPrivateBaseline(NetworkConnectionToClient target, int gridRows, int gridColumns, byte[] payload)
        {
            if (isServer || !isOwned) return;
            authoritativeRows = gridRows; authoritativeColumns = gridColumns;
            using (NetworkReaderPooled reader = NetworkReaderPool.Get(payload)) entries.OnDeserializeAll(reader);
            mirrorProjection.RequestFullRefresh(true);
            mirrorDirty = true;
        }

        public Action ServerBeforeEquipmentOrWeaponOperation { get; set; }
        public Action<WeaponOperation> ServerBeforeWeaponOperation { get; set; }

        private bool CanForwardTo(IBackpackView destination)
        {
            BackpackNetworkInventory ingress = LocalIngress;
            IBackpackView target = destination ?? this;
            return ingress != null && ingress.isOwned && IsReady && target.IsReady
                && (ReferenceEquals(this, ingress) || sharedClientSubscribed)
                && ingress.ClientOwnsEndpoint(target);
        }

        internal bool CanSubmitEquipmentTo(IBackpackView destination) => isOwned && (destination == null || ClientOwnsEndpoint(destination));

        private bool ClientOwnsEndpoint(IBackpackView view)
        {
            if (view is BackpackNetworkInventory inventory) return inventory == this || inventory.sharedClientSubscribed;
            if (view is BackpackNetworkEquipment equipment) return equipment.netIdentity == netIdentity && equipment.isOwned;
            if (isServer && view is Component component) return component.gameObject == gameObject;
            return false;
        }

        private static bool ResolveClientEndpoint(IBackpackView view, out uint identityId, out bool equipment)
        {
            equipment = view is BackpackNetworkEquipment || view is RPG4.Backpack.Runtime.EquipmentInventory;
            identityId = 0;
            if (!(view is Component component) || component == null) return false;
            NetworkIdentity identity = component.GetComponent<NetworkIdentity>();
            if (identity == null) return false;
            identityId = identity.netId;
            return identityId != 0;
        }

        private void SubmitClientOperation(IBackpackView source, InventoryOperation operation)
        {
            BackpackNetworkInventory owner = LocalIngress;
            IBackpackView destination = operation.Destination ?? source;
            if (owner == null || !owner.isOwned || !source.IsReady || !destination.IsReady
                || !owner.ClientOwnsEndpoint(source) || !owner.ClientOwnsEndpoint(destination)
                || !ResolveClientEndpoint(source, out uint sourceId, out bool sourceEquipment)
                || !ResolveClientEndpoint(destination, out uint destinationId, out bool destinationEquipment))
            {
                var failure = new InventoryOperationResult(operation.RequestId, false, ItemId.None, 0, "当前客户端不能提交此库存操作。");
                if (source is BackpackNetworkEquipment equipment) equipment.PublishResult(failure);
                else core?.PublishOperationResult(failure);
                return;
            }
            owner.CmdExecuteInventoryOperation(owner.NextClientTicket(), operation.RequestId, sourceId, sourceEquipment,
                source is BackpackNetworkInventory sourceInventory ? sourceInventory.sharedClientSession : 0,
                destinationId, destinationEquipment, destination is BackpackNetworkInventory destinationInventory ? destinationInventory.sharedClientSession : 0, (int)operation.Kind, operation.SourceItemId.Value,
                operation.TargetItemId.Value, operation.Count, operation.UpperLeftIndex);
        }

        internal void SubmitEquipmentOperation(BackpackNetworkEquipment source, InventoryOperation operation)
        {
            if (isClient) { SubmitClientOperation(source, operation); return; }
            ScheduleOperation(() =>
            {
                ServerBeforeEquipmentOrWeaponOperation?.Invoke();
                source.PublishResult(source.AuthorityCore.ExecuteOperation(operation));
            }, reason => source.PublishResult(new InventoryOperationResult(operation.RequestId, false, ItemId.None, 0, reason)));
        }

        /// <summary>仅发送容器身份；服务器从此拥有的玩家身份验证，不接收客户端位置或物品快照。</summary>
        public bool RequestOpenShared(BackpackNetworkInventory target)
        {
            if (!isClient || !isOwned || sharedContainer || target == null || target.netId == 0) return false;
            uint session = ++clientSessionCounter;
            if (session == 0) session = ++clientSessionCounter;
            clientSessions[target.netId] = session;
            target.sharedReceive = null;
            CmdSetSharedSubscription(target.netId, session, true);
            return true;
        }

        public void RequestCloseShared(BackpackNetworkInventory target)
        {
            if (!isClient || !isOwned || target == null || !clientSessions.TryGetValue(target.netId, out uint session)) return;
            clientSessions.Remove(target.netId);
            target.ClearSharedMirror("共享容器已关闭。");
            CmdSetSharedSubscription(target.netId, session, false);
        }

        [Command(requiresAuthority = true)]
        private void CmdSetSharedSubscription(uint targetNetId, uint session, bool open)
        {
            if (!TryFindInventory(targetNetId, out BackpackNetworkInventory target) || !target.sharedContainer)
            {
                if (open) TargetSharedRevoked(connectionToClient, targetNetId, session, "共享容器不存在或未开放。");
                return;
            }
            NetworkConnectionToClient conn = connectionToClient;
            if (!open)
            {
                if (target.subscribers.TryGetValue(conn, out Subscription existing) && existing.Ingress == this && existing.Session == session)
                    target.RevokeSubscription(conn, "共享容器已关闭。");
                return;
            }
            if (session == 0 || openedShared.Count >= MaxOpenContainers && !openedShared.Contains(target)
                || !target.CanAccessShared(this, conn))
            {
                TargetSharedRevoked(conn, targetNetId, session, "共享容器访问未获授权。");
                return;
            }
            if (target.subscribers.TryGetValue(conn, out Subscription previous))
            {
                NetworkSendScheduler.Cancel(conn, target.netId, SharedStreamId);
                if (previous.Ingress != null) previous.Ingress.openedShared.Remove(target);
            }
            target.subscribers[conn] = new Subscription { Ingress = this, Session = session };
            openedShared.Add(target);
            if (!target.SyncNow(out string syncError))
            {
                target.RevokeSubscription(conn, syncError ?? "共享库存投影未就绪。");
                return;
            }
            target.FlushSharedChanges();
        }

        private bool IsAuthenticatedIngress(NetworkConnectionToClient conn) => IsServerInventory && isActiveAndEnabled && !sharedContainer
            && conn != null && conn.isAuthenticated && conn.isReady && conn.identity == netIdentity
            && connectionToClient == conn && ServerAccessValidator != null && ServerAccessValidator(conn);

        private bool CanAccessShared(BackpackNetworkInventory ingress, NetworkConnectionToClient conn)
        {
            if (!IsServerInventory || !sharedContainer || !isActiveAndEnabled || ingress == null || !ingress.IsAuthenticatedIngress(conn)
                || conn.identity.gameObject.scene != gameObject.scene || !netIdentity.observers.ContainsKey(conn.connectionId)) return false;
            Vector3 delta = conn.identity.transform.position - transform.position;
            return !float.IsNaN(delta.sqrMagnitude) && delta.sqrMagnitude <= interactionRange * interactionRange
                && (sharedPermission == null || sharedPermission(conn));
        }

        private bool CanAccessEndpoint(BackpackNetworkInventory target, NetworkConnectionToClient conn)
            => target == this || target != null && target.sharedContainer
                && target.subscribers.TryGetValue(conn, out Subscription subscription)
                && subscription.Ingress == this && target.CanAccessShared(this, conn);

        private static bool TryFindInventory(uint id, out BackpackNetworkInventory inventory)
        {
            inventory = null;
            return NetworkServer.spawned.TryGetValue(id, out NetworkIdentity identity)
                && (inventory = identity.GetComponent<BackpackNetworkInventory>()) != null;
        }

        private void ReceiveInventoryOperation(ulong ticket, int requestId, uint sourceNetId, bool sourceEquipment, uint sourceSession,
            uint destinationNetId, bool destinationEquipment, uint destinationSession, int kind, ulong sourceId, ulong targetId, int count, int upperLeftIndex)
        {
            NetworkConnectionToClient conn = connectionToClient;
            Action<InventoryOperationResult> complete = result =>
            {
                if (connectionToClient != conn || operationOwner != conn) return;
                CompleteTicket(ticket, () =>
                TargetRoutedInventoryCompleted(conn, sourceNetId, sourceEquipment, result.RequestId, result.Success, result.ResultItemId.Value,
                    result.MovedCount, result.Error, result.SourceStackCount, result.ResultStackCount));
            };
            InventoryOperationResult Failure(string reason) => new InventoryOperationResult(requestId, false, ItemId.None, 0, reason);
            if (!BeginTicket(ticket, requestId, false, () => TargetRoutedInventoryCompleted(conn, sourceNetId, sourceEquipment, requestId, false, 0, 0, "请求已过期或队列已满。", -1, -1))) return;
            ScheduleOperation(() =>
            {
                if (!IsAuthenticatedIngress(conn)
                    || !TryResolveServerEndpoint(sourceNetId, sourceEquipment, sourceSession, conn, out IBackpackView source, out InventoryHostCore sourceCore)
                    || !TryResolveServerEndpoint(destinationNetId, destinationEquipment, destinationSession, conn, out IBackpackView destination, out _))
                { complete(Failure("库存访问授权已失效。")); return; }
                if (sourceEquipment || destinationEquipment) ServerBeforeEquipmentOrWeaponOperation?.Invoke();
                complete(sourceCore.ExecuteOperation(new InventoryOperation(requestId, (InventoryOperationKind)kind,
                    new ItemId(sourceId), new ItemId(targetId), count, destination, upperLeftIndex)));
            }, reason => complete(Failure(reason)));
        }

        private bool TryResolveServerEndpoint(uint id, bool equipment, uint session, NetworkConnectionToClient conn,
            out IBackpackView view, out InventoryHostCore authority)
        {
            view = null; authority = null;
            if (equipment)
            {
                if (id != netId) return false;
                BackpackNetworkEquipment slot = GetComponent<BackpackNetworkEquipment>();
                if (slot == null || slot.AuthorityCore == null) return false;
                view = slot; authority = slot.AuthorityCore; return true;
            }
            if (!TryFindInventory(id, out BackpackNetworkInventory inventory) || !CanAccessEndpoint(inventory, conn)) return false;
            if (inventory != this && (!inventory.subscribers.TryGetValue(conn, out Subscription subscription) || subscription.Session != session)) return false;
            if (inventory.authorityView == null) inventory.authorityView = new AuthorityView(inventory);
            view = inventory.authorityView; authority = inventory.core; return true;
        }

        private bool BeginTicket(ulong ticket, int requestId, bool weapon, Action reject)
        {
            if (operationOwner != connectionToClient)
            {
                pendingOperations.Clear(); completedOperations.Clear(); completedOrder.Clear();
                requestTickets.Clear(); requestOrder.Clear(); serverTicketHighWater = 0;
                inventoryRequestHighWater = weaponRequestHighWater = 0;
                operationOwner = connectionToClient;
            }
            if (completedOperations.TryGetValue(ticket, out Action resend)) { resend(); return false; }
            if (pendingOperations.Contains(ticket)) return false;
            // 非零业务 requestId 在当前玩家会话内幂等；内部 requestId=0 的独立调用仍由网络 ticket 区分。
            var key = (weapon, requestId);
            if (requestId != 0 && requestTickets.TryGetValue(key, out ulong original))
            {
                if (completedOperations.TryGetValue(original, out Action previous)) previous();
                else if (!pendingOperations.Contains(original)) reject();
                return false;
            }
            uint highWater = weapon ? weaponRequestHighWater : inventoryRequestHighWater;
            if (ticket == 0 || ticket <= serverTicketHighWater || requestId != 0 && (uint)requestId <= highWater)
            { reject(); return false; }
            serverTicketHighWater = ticket;
            if (requestId != 0)
            {
                if (weapon) weaponRequestHighWater = (uint)requestId; else inventoryRequestHighWater = (uint)requestId;
                requestTickets[key] = ticket; requestOrder.Enqueue(key);
                while (requestOrder.Count > ResultHistoryLimit + MaxPendingOperations) requestTickets.Remove(requestOrder.Dequeue());
            }
            bool full = pendingOperations.Count >= MaxPendingOperations;
            pendingOperations.Add(ticket);
            if (full) { CompleteTicket(ticket, reject); return false; }
            return true;
        }

        private void CompleteTicket(ulong ticket, Action send)
        {
            if (!pendingOperations.Remove(ticket)) return;
            completedOperations[ticket] = send;
            completedOrder.Enqueue(ticket);
            while (completedOrder.Count > ResultHistoryLimit) completedOperations.Remove(completedOrder.Dequeue());
            if (connectionToClient != null && connectionToClient.isReady) send();
        }

        /// <summary>所有涉及角色物品/负重/弹匣的网络意图经过同一 tick 边界；没有调度器时拒绝。</summary>
        public void ScheduleOperation(Action execute, Action<string> reject)
        {
            bool settled = false;
            Action run = () =>
            {
                if (settled) return;
                settled = true;
                try { execute(); }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                    reject("库存事务执行失败。");
                }
            };
            Action<string> fail = reason => { if (settled) return; settled = true; reject(reason); };
            try
            {
                if (ServerOperationScheduler == null || !ServerOperationScheduler(run, fail)) fail("角色事务调度器未就绪或队列已满。");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                fail("库存事务调度失败。");
            }
        }

        private void DispatchAuthorityOperation(InventoryOperation operation)
        {
            if (isClient) { RequestInventoryOperation(operation); return; }
            ScheduleOperation(() => core?.PublishOperationResult(core.ExecuteOperation(operation)),
                reason => core?.PublishOperationResult(new InventoryOperationResult(operation.RequestId, false, ItemId.None, 0, reason)));
        }

        private void DispatchAuthorityWeaponOperation(WeaponOperation operation)
        {
            if (isClient) { RequestWeaponOperation(operation); return; }
            ScheduleOperation(() => core?.PublishWeaponOperationResult(core.ExecuteWeaponOperation(operation)),
                reason => core?.PublishWeaponOperationResult(new WeaponOperationResult(operation.RequestId, operation.TargetItemId, false, reason)));
        }

        [TargetRpc]
        private void TargetRoutedInventoryCompleted(NetworkConnectionToClient target, uint sourceNetId, bool sourceEquipment, int requestId,
            bool success, ulong resultItemId, int movedCount, string error, int sourceStackCount, int resultStackCount)
        {
            if (!NetworkClient.spawned.TryGetValue(sourceNetId, out NetworkIdentity source)) return;
            var result = new InventoryOperationResult(requestId, success, new ItemId(resultItemId), movedCount, error, sourceStackCount, resultStackCount);
            if (sourceEquipment) source.GetComponent<BackpackNetworkEquipment>()?.PublishResult(result);
            else source.GetComponent<BackpackNetworkInventory>()?.core?.PublishOperationResult(result);
        }

        private void RecordSharedChange(SyncList<NetworkInventoryEntry>.Operation op, int index, NetworkInventoryEntry item)
        {
            if (!sharedContainer) return;
            InventorySyncList stream = sharedServerStream.Entries;
            switch (op)
            {
                case SyncList<NetworkInventoryEntry>.Operation.OP_ADD: stream.Add(item); break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_INSERT: stream.Insert(index, item); break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_SET: stream[index] = item; break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_REMOVEAT: stream.RemoveAt(index); break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_CLEAR: stream.Clear(); break;
            }
        }

        private void MaintainServerAccess()
        {
            if (!sharedContainer) return;
            revokedSubscribers.Clear();
            foreach (KeyValuePair<NetworkConnectionToClient, Subscription> pair in subscribers)
                if (pair.Value.Error != null || !CanAccessShared(pair.Value.Ingress, pair.Key)) revokedSubscribers.Add(pair.Key);
            foreach (NetworkConnectionToClient conn in revokedSubscribers)
                RevokeSubscription(conn, subscribers[conn].Error ?? "共享容器访问已失效。");
        }

        private void RevokeSubscription(NetworkConnectionToClient conn, string reason)
        {
            if (!subscribers.TryGetValue(conn, out Subscription subscription)) return;
            NetworkSendScheduler.Cancel(conn, netId, SharedStreamId);
            subscribers.Remove(conn);
            if (subscribers.Count == 0) ClearSharedJournal();
            if (subscription.Ingress == null) return;
            subscription.Ingress.openedShared.Remove(this);
            if (conn != null && conn.isReady)
                subscription.Ingress.TargetSharedRevoked(conn, netId, subscription.Session, reason);
        }

        private void FlushSharedChanges()
        {
            if (!sharedContainer) return;
            FlushSharedJournal();
            foreach (KeyValuePair<NetworkConnectionToClient, Subscription> pair in subscribers)
                PumpSharedSubscription(pair.Key, pair.Value);
        }

        private void OnSharedClientEntryChanged(SyncList<NetworkInventoryEntry>.Operation op, int index,
            NetworkInventoryEntry previous, NetworkInventoryEntry current)
        {
            // Host 的权威 entries 与定向镜像分开，撤销不会清空服务器容器。
            switch (op)
            {
                case SyncList<NetworkInventoryEntry>.Operation.OP_ADD:
                case SyncList<NetworkInventoryEntry>.Operation.OP_INSERT: mirrorProjection.Added(current); break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_SET: mirrorProjection.Replaced(previous, current); break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_REMOVEAT: mirrorProjection.Removed(previous); break;
                case SyncList<NetworkInventoryEntry>.Operation.OP_CLEAR: mirrorProjection.RequestFullRefresh(); break;
            }
            mirrorDirty = true;
        }

        [TargetRpc]
        private void TargetSharedRevoked(NetworkConnectionToClient target, uint containerId, uint session, string reason)
        {
            if (!clientSessions.TryGetValue(containerId, out uint expected) || expected != session) return;
            clientSessions.Remove(containerId);
            if (NetworkClient.spawned.TryGetValue(containerId, out NetworkIdentity identity))
                identity.GetComponent<BackpackNetworkInventory>()?.ClearSharedMirror(reason);
        }

        private void ClearSharedMirror(string reason)
        {
            sharedReceive = null;
            sharedClientVersion = 0;
            sharedClientSubscribed = false;
            sharedClientSession = 0;
            sharedClientStream.Entries.Reset();
            mirror.Clear(); mirrorPlacements.Clear(); mirrorProjection.Clear();
            mirrorReady = false; mirrorDirty = false;
            pendingViewChanges.Clear(); fullViewRefreshPending = true;
            RaiseChanged();
            SharedAccessChanged?.Invoke(false, reason);
        }

        private void ClearClientAccess()
        {
            foreach (uint id in clientSessions.Keys)
                if (NetworkClient.spawned.TryGetValue(id, out NetworkIdentity identity))
                    identity.GetComponent<BackpackNetworkInventory>()?.ClearSharedMirror("连接已结束。");
            clientSessions.Clear();
            if (sharedClientSubscribed) ClearSharedMirror("容器已离开可见范围。");
        }

        private void StopServerAccess()
        {
            RevokeAccessSubscriptions("库存连接已结束。");
            openedShared.Clear(); pendingOperations.Clear(); completedOperations.Clear(); completedOrder.Clear();
            serverTicketHighWater = 0; operationOwner = null;
            requestTickets.Clear(); requestOrder.Clear(); inventoryRequestHighWater = weaponRequestHighWater = 0;
            ClearSharedJournal();
            sharedVersion = 0;
            sharedServerStream.Entries.Reset();
        }
    }
}
