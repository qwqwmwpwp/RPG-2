using System;
using System.Collections.Generic;
using Cinemachine;
using Mirror;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using RPG4.Combat;
using RPG4.PlayerIntent;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking
{
    /// <summary>Server-owned simulation, owner-only device sampling and movement-only prediction.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-300)]
    [RequireComponent(typeof(NetworkIdentity), typeof(Player))]
    public sealed partial class NetworkPlayerController : NetworkBehaviour
    {
        [SerializeField, Range(0.1f, 0.5f)] private float inputTimeout = 0.25f;
        [SerializeField, Range(1, 4)] private int gapWaitTicks = 2;
        [SerializeField, Range(1, 4)] private int snapshotEveryTicks = 1;
        [SerializeField, Range(0.02f, 0.25f)] private float interpolationDelay = 0.1f;
        [SerializeField] private Behaviour[] additionalClientPresentationBehaviours = Array.Empty<Behaviour>();
        private const int OperationCapacity = 32;
        private const int StateStream = 1, ShotStream = 2;
        private const double OwnerStateMaxDelay = 0.1d, ShotLifetime = 0.3d;
        private readonly ServerPlayerInputBuffer serverInputs = new ServerPlayerInputBuffer();
        private readonly PlayerSnapshotTimeline snapshots = new PlayerSnapshotTimeline();
        private readonly Queue<Operation> operations = new Queue<Operation>();
        private struct Operation { internal Action Execute; internal Action<string> Reject; internal double Received; }
        private Player player;
        private readonly Dictionary<Behaviour, bool> visualBehaviours = new Dictionary<Behaviour, bool>();
        private BackpackNetworkInventory inventory;
        private CharacterBackpack characterBackpack;
        private BackpackNetworkEquipment replicatedEquipment;
        private RPG4.Input.PlayerInput localInput;
        private bool serverStarted, clientStarted, ready;
        private bool localGameplayInputAllowed = true, localGameplayInputExplicitlySet;
        private ulong nextSequence = 1;
        private readonly Dictionary<ulong, ulong> shotWatermarks = new Dictionary<ulong, ulong>();
        private readonly Queue<ulong> shotWeapons = new Queue<ulong>();
        private readonly Queue<PendingShot> pendingShots = new Queue<PendingShot>();
        private struct PendingShot { internal NetworkShotPresentation Shot; internal double Received; }
        private readonly List<OwnerPublication> ownerPublications = new List<OwnerPublication>(128);
        private struct OwnerPublication { internal long Tick; internal double SentAt; }
        private struct ActionRequest { internal ulong Id, WeaponId, InputSequence; internal byte Kind; internal double Received; }
        private readonly Queue<ActionRequest> actionRequests = new Queue<ActionRequest>(32);
        private ulong nextActionRequest = 1, lastActionRequest, ownerTriggerRequest, triggerRequest, triggerWeapon;
        private uint triggerOrdinal;
        private struct ShotCorrelation { internal ulong Request; internal uint Ordinal; }
        private readonly Dictionary<(ulong Weapon, ulong Shot), ShotCorrelation> shotCorrelations
            = new Dictionary<(ulong Weapon, ulong Shot), ShotCorrelation>();
        private long lastServerTick, lastPublishedTick;
        private bool timeoutCleared;
        private double acceptedRewindDelay;
        private PlayerInputPacket previous, older;
        public Player Player => player;
        public long LastServerTick => lastServerTick;
        public bool HasInitialAuthoritativeState => ready;
        public bool LocalGameplayInputAllowed => localGameplayInputAllowed;
        public event Action<NetworkPlayerController> InitialAuthoritativeStateReceived;
        /// <summary>Raised after both receiving and invalidating initial state. Subscribe, then read the property.</summary>
        public event Action<NetworkPlayerController> InitialAuthoritativeStateChanged;
        public event Action<NetworkShotPresentation> ShotPresented;

        private void Awake()
        {
            player = GetComponent<Player>();
            localInput = GetComponent<RPG4.Input.PlayerInput>();
            ApplyDefaultLocalInputGate();
            foreach (Behaviour behaviour in GetComponentsInChildren<Behaviour>(true))
                if (behaviour is Animator || behaviour is UnityEngine.Animations.Rigging.RigBuilder
                    || behaviour is PlayerAimPose || behaviour is PlayerWeaponRig)
                    visualBehaviours[behaviour] = behaviour.enabled;
            if (additionalClientPresentationBehaviours != null)
                foreach (Behaviour behaviour in additionalClientPresentationBehaviours)
                    if (behaviour != null && !visualBehaviours.ContainsKey(behaviour))
                        visualBehaviours.Add(behaviour, behaviour.enabled);
            SetClientPresentation(false);
#if !UNITY_SERVER
            // 在客户端动画/Rig 求值前采集原始骨骼，避免头发选区被初始姿态偏移裁成空网格。
            // 这里只初始化；构建和模拟仍由客户端表现组件启用后的 Start 驱动。
            foreach (var entry in visualBehaviours)
                if (entry.Value && entry.Key is MagicaCloth2.MagicaCloth cloth) cloth.Initialize();
#endif
            inventory = GetComponent<BackpackNetworkInventory>();
            characterBackpack = GetComponent<CharacterBackpack>();
            replicatedEquipment = GetComponent<BackpackNetworkEquipment>();
            // Runs before Player.Awake, so no server/remote ever binds a device in Player initialization.
            player.ConfigureNetworkSimulation(false, false);
            GetComponent<RPG4.CharacterAttributes.CharacterAttributes>()?.ConfigureAuthority(false);
            GetComponent<PlayerEquipment>()?.ConfigureAuthority(false);
            if (characterBackpack != null) characterBackpack.IsLocalPlayer = false;
            SetLocalCameras(false);
        }
        public override void OnStartServer()
        {
            base.OnStartServer();
            serverStarted = true;
            ResetInitialAuthoritativeState();
            ApplyDefaultLocalInputGate();
            player.ConfigureNetworkSimulation(true, isLocalPlayer);
            player.Attributes.ConfigureAuthority(true);
            player.Attributes.Died += OnServerDied;
            player.Attributes.Revived += OnServerRevived;
            player.Equipment?.ConfigureAuthority(true, true);
            if (player.Equipment != null) player.Equipment.ShotResolved += OnShotResolved;
            if (inventory != null)
            {
                inventory.ServerOperationScheduler = QueueOperation;
                inventory.ServerConsumableUse = operation => player.Consumables.TryStart(inventory.AuthorityCore, operation, NetworkPlayerSimulation.Time);
                inventory.ConsumableStateProvider = () => player.Consumables != null ? player.Consumables.GetViewState() : default;
                inventory.ServerAccessValidator = ValidateAccess;
                inventory.ServerBeforeEquipmentOrWeaponOperation = () => player.Equipment?.CancelWeaponActionForTransaction();
                inventory.ServerBeforeWeaponOperation = operation => player.Equipment?.PrepareWeaponOperation(operation);
            }
            NetworkPlayerSimulation.Register(this);
        }
        public override void OnStartClient()
        {
            base.OnStartClient();
            clientStarted = true;
            if (inventory != null) inventory.ConsumableStateProvider = () => player.Consumables != null ? player.Consumables.GetViewState() : default;
            if (replicatedEquipment != null)
            {
                replicatedEquipment.AppearanceChanged += OnAppearanceChanged;
                OnAppearanceChanged(replicatedEquipment.AppearanceItem);
            }
            RefreshRole();
            NetworkPlayerSimulation.Register(this);
        }
        public override void OnStartLocalPlayer()
        {
            base.OnStartLocalPlayer();
            RefreshRole();
            if (NetworkManager.singleton is AuthoritativeNetworkManager manager) manager.BindLocalPlayer(player);
        }
        public override void OnStartAuthority() { base.OnStartAuthority(); if (clientStarted) RefreshRole(); }
        public override void OnStopAuthority()
        {
            InvalidateMovementSession();
            ResetInitialAuthoritativeState();
            player.Input.SetNetworkLocalInput(false);
            player.ResetOwnerPresentation();
            player.Equipment?.SetLocalPresentationWeapon(null);
            ClearLocalInputEdges();
            player.ClearIntent();
            if (characterBackpack != null) characterBackpack.IsLocalPlayer = false;
            SetLocalCameras(false);
            base.OnStopAuthority();
        }
        private void OnAppearanceChanged(RPG4.Backpack.Items.RuntimeItem item)
        { if (!serverStarted) player.Equipment?.ApplyReplicatedEquipment(item); }
        private void RefreshRole()
        {
            bool local = isLocalPlayer && isOwned;
            ApplyDefaultLocalInputGate();
            player.ConfigureNetworkSimulation(serverStarted, local);
            localInput.SetLocalGameplayInputAllowed(localGameplayInputAllowed);
            if (!serverStarted)
            {
                player.Attributes.ConfigureAuthority(false);
                player.Equipment?.ConfigureAuthority(false);
            }
            SetClientPresentation(clientStarted);
            if (characterBackpack != null) characterBackpack.IsLocalPlayer = local;
            SetLocalCameras(local);
            EnableOwnerMovementHistory();
        }
        private void ApplyDefaultLocalInputGate()
        {
            if (localGameplayInputExplicitlySet) return;
            localGameplayInputAllowed = !(NetworkManager.singleton is AuthoritativeNetworkManager manager
                && manager.UsesGameplaySceneBindings);
            localInput?.SetLocalGameplayInputAllowed(localGameplayInputAllowed);
        }
        /// <summary>Gate only local device input and prediction; replication and server simulation remain active.</summary>
        public void SetLocalGameplayInputAllowed(bool allowed)
        {
            // The frontend may hold input closed after readiness, but cannot open an incomplete online player.
            if (allowed && NetworkManager.singleton is AuthoritativeNetworkManager manager
                && manager.UsesGameplaySceneBindings && !manager.IsLocalGameplayReady) allowed = false;
            localGameplayInputExplicitlySet = true;
            bool changed = localGameplayInputAllowed != allowed;
            localGameplayInputAllowed = allowed;
            localInput?.SetLocalGameplayInputAllowed(allowed);
            if (changed || !allowed) ClearLocalInputEdges();
        }
        public void ClearLocalInputEdges()
        {
            localInput?.ClearLocalInputEdges();
            ClearOwnerMovementInput();

            previous = older = default;
            ownerTriggerRequest = 0;
            player?.ResetOwnerPresentation();
            if (clientStarted && ready && isLocalPlayer && isOwned && !serverStarted
                && NetworkClient.isConnected && NetworkClient.ready)
                CmdCancelActions(nextActionRequest - 1);
            // Host's device feeds the same authoritative buffer; discard its queued samples without cancelling actions.
            if (serverStarted && isLocalPlayer && isOwned)
            {
                serverInputs.DiscardPending();
                RejectPendingActions();
                timeoutCleared = true;
            }
        }
        private void MarkInitialAuthoritativeStateReceived()
        {
            // Late RPCs and a Host publication must not ready a disabled controller/player.
            if (!isActiveAndEnabled || player == null || !player.isActiveAndEnabled) return;
            if (ready) return;
            ready = true;
            PublishInitialStateEvent(InitialAuthoritativeStateChanged);
            PublishInitialStateEvent(InitialAuthoritativeStateReceived);
        }
        private void ResetInitialAuthoritativeState()
        {
            if (!ready) return;
            ready = false;
            PublishInitialStateEvent(InitialAuthoritativeStateChanged);
        }
        private void PublishInitialStateEvent(Action<NetworkPlayerController> handlers)
        {
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try { ((Action<NetworkPlayerController>)handler)(this); }
                catch (Exception) { Debug.LogError("An initial player state subscriber failed.", this); }
            }
        }
        private void ResetLocalInputGate()
        {
            localGameplayInputExplicitlySet = false;
            ApplyDefaultLocalInputGate();
            ClearLocalInputEdges();
        }
        private void SetClientPresentation(bool enabled)
        {
            player.PresentationEnabled = enabled;
            foreach (var entry in visualBehaviours)
                if (entry.Key != null) entry.Key.enabled = enabled && entry.Value;
        }
        private void SetLocalCameras(bool local)
        {
            // Role ownership controls rendering/listeners. The gameplay gate acts on PlayerInput's look/zoom
            // sampling; disabling the cameras here until ready would break presentation binding readiness.
            foreach (Camera camera in GetComponentsInChildren<Camera>(true)) camera.enabled = local;
            foreach (AudioListener listener in GetComponentsInChildren<AudioListener>(true)) listener.enabled = local;
            foreach (CinemachineVirtualCameraBase camera in GetComponentsInChildren<CinemachineVirtualCameraBase>(true)) camera.enabled = local;
            foreach (PlayerCameraController camera in GetComponentsInChildren<PlayerCameraController>(true)) camera.enabled = local;
        }
        private bool ValidateAccess(NetworkConnectionToClient sender) => serverStarted && sender != null
            && sender == connectionToClient && sender.identity == netIdentity && player.IsAlive && player.isActiveAndEnabled;
        private bool QueueOperation(Action execute, Action<string> reject)
        {
            if (!ValidateAccess(connectionToClient) || execute == null || operations.Count >= OperationCapacity) return false;
            operations.Enqueue(new Operation { Execute = execute, Reject = reject, Received = Time.realtimeSinceStartupAsDouble });
            return true;
        }
        internal void DrainOperations()
        {
            // Freeze this tick's budget: callbacks cannot grow the amount of work drained this tick.
            int count = operations.Count;
            for (int i = 0; i < count; i++)
            {
                Operation op = operations.Dequeue();
                if (Time.realtimeSinceStartupAsDouble - op.Received > 1d) { Reject(op, "库存请求已过期。"); continue; }
                if (!ValidateAccess(connectionToClient)) { Reject(op, "角色当前不可交互。"); continue; }
                try { op.Execute(); }
                catch (Exception exception) { Debug.LogException(exception, this); Reject(op, "操作执行失败。"); }
            }
        }
        private void OnServerDied()
        {
            player.CancelConsumableUse();
            serverInputs.DiscardPending(); RejectPendingActions(); RejectOperations("角色已死亡。");
            constraintRevision++;
            pendingHardBaseline = true; correctionReason = MovementRejectReason.AuthorityChanged;
            if (NetworkManager.singleton is AuthoritativeNetworkManager manager) manager.OnServerPlayerDied(this);
        }
        private void OnServerRevived()
        {
            serverInputs.DiscardPending(); RejectPendingActions();
            constraintRevision++;
            pendingHardBaseline = true; correctionReason = MovementRejectReason.AuthorityChanged;
        }
        private void Reject(Operation operation, string reason)
        {
            try { operation.Reject?.Invoke(reason); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }
        private void RejectOperations(string reason)
        { while (operations.Count > 0) Reject(operations.Dequeue(), reason); }
        private void FixedUpdate() => NetworkPlayerSimulation.FixedStep();
        internal void SampleOwner(long tick, float deltaTime)
        {
            if (!clientStarted || !isLocalPlayer || !isOwned) return;
            // Recover readiness before the gameplay gate: a disabled owner may have missed a reliable baseline.
            if (!serverStarted && !movementHistory.HasBaseline) { SampleMovementOwner(tick, deltaTime); return; }
            if (!localGameplayInputAllowed || (!serverStarted && !ready)) return;
            SampleMovementOwner(tick, deltaTime);
        }
        private void ReceiveInputs(PlayerInputPacket newest, PlayerInputPacket previousPacket, PlayerInputPacket olderPacket)
        {
            if (!serverStarted || !isLocalPlayer || !ValidateAccess(connectionToClient)) return;
            int timeout = Mathf.Max(1, Mathf.CeilToInt(inputTimeout / Time.fixedDeltaTime));
            long tick = NetworkPlayerSimulation.Tick;
            AcceptPacket(olderPacket, tick, timeout);
            AcceptPacket(previousPacket, tick, timeout);
            AcceptPacket(newest, tick, timeout);
        }
        private void SubmitOwnerActions(PlayerInputFrame frame)
        {
            if (frame.ReloadPressed) SubmitOwnerAction(frame, 2);
            if (frame.FirePressed) SubmitOwnerAction(frame, 1);
        }
        private void SubmitOwnerAction(PlayerInputFrame frame, byte kind)
        {
            ulong id = nextActionRequest++;
            ownerTriggerRequest = kind == 1 && frame.FireHeld ? id : 0;
            ulong weapon = player.PublishedFrame.WeaponId;
            if (!serverStarted)
            {
                var edge = new PlayerInputFrame(frame.Move, crouchHeld: frame.CrouchHeld, aimHeld: frame.AimHeld,
                    firePressed: kind == 1, fireHeld: frame.FireHeld, reloadPressed: kind == 2);
                player.AnticipateLocalActions(edge, id, Time.unscaledTimeAsDouble);
                CmdAction(id, kind, weapon, nextSequence);
            }
            else ReceiveAction(id, kind, weapon, nextSequence);
        }
        // Reliable edges have their own result. A movement watermark never means an action succeeded.
        [Command]
        private void CmdAction(ulong requestId, byte kind, ulong weaponId, ulong inputSequence)
            => ReceiveAction(requestId, kind, weaponId, inputSequence);
        [Command]
        private void CmdCancelActions(ulong throughRequest)
        {
            if (!serverStarted) return;
            lastActionRequest = Math.Max(lastActionRequest, throughRequest);
            RejectPendingActions();
        }
        private void ReceiveAction(ulong id, byte kind, ulong weapon, ulong inputSequence)
        {
            if (!serverStarted || id == 0 || id <= lastActionRequest) return;
            lastActionRequest = id;
            if (!ValidateAccess(connectionToClient) || lastPublishedTick <= 0 && !isLocalPlayer
                || (kind != 1 && kind != 2) || inputSequence == 0 || actionRequests.Count >= 32)
            { SendActionResult(id, false, weapon, 0, NetworkPlayerSimulation.Time, 0); return; }
            actionRequests.Enqueue(new ActionRequest { Id = id, Kind = kind, WeaponId = weapon, InputSequence = inputSequence,
                Received = Time.realtimeSinceStartupAsDouble });
        }
        private void SendActionResult(ulong id, bool accepted, ulong weapon, ulong shot, double time, uint ordinal)
        {
            if (serverStarted && NetworkServer.active && connectionToClient != null && !isLocalPlayer)
                TargetActionResult(connectionToClient, id, accepted, weapon, shot, time, ordinal);
        }
        [TargetRpc]
        private void TargetActionResult(NetworkConnectionToClient recipient, ulong id, bool accepted,
            ulong weapon, ulong shot, double time, uint ordinal)
        {
            if (clientStarted && !serverStarted && isLocalPlayer && isOwned)
                player.ApplyLocalActionResult(id, accepted, weapon, shot, time, ordinal);
        }
        private void RejectPendingActions()
        {
            while (actionRequests.Count > 0)
            {
                ActionRequest action = actionRequests.Dequeue();
                SendActionResult(action.Id, false, action.WeaponId, 0, NetworkPlayerSimulation.Time, 0);
            }
            triggerRequest = triggerWeapon = 0; triggerOrdinal = 0;
        }
        private void AcceptPacket(PlayerInputPacket packet, long tick, int timeout)
        {
            if (packet.Sequence != 0 && ObservationIsFresh(packet, tick)) serverInputs.Enqueue(packet, tick, timeout);
        }
        private bool ObservationIsFresh(PlayerInputPacket packet, long tick)
        {
            if (!packet.IsValid) return false;
            double oneWay = connectionToClient != null ? Math.Max(0d, connectionToClient.rtt * 0.5d) : 0d;
            double rtt = oneWay * 2d;
            // A snapshot travels down before its observed tick travels back up. Bootstrap tolerates
            // an unmeasured RTT; queue/held timeout remains measured separately from server receipt.
            double ageWindow = rtt > 0.001d
                ? rtt + snapshotEveryTicks * Time.fixedDeltaTime + OwnerStateMaxDelay + 0.1d : 1d;
            // Budget pressure may delay output beyond its fairness deadline. Input referring to the
            // last state actually sent must still be usable; receipt/held timeouts remain independent.
            double observationCutoff = Time.realtimeSinceStartupAsDouble - rtt - 0.1d;
            // Keep the last publication before the return-trip window as well. Immediately sending
            // a fresh state after a backlog must not invalidate inputs still observing the old one.
            while (ownerPublications.Count > 1 && ownerPublications[1].SentAt <= observationCutoff)
                ownerPublications.RemoveAt(0);
            if (ownerPublications.Count > 0)
                ageWindow = Math.Max(ageWindow, (tick - ownerPublications[0].Tick) * Time.fixedDeltaTime + rtt + 0.1d);
            int delayBudget = Mathf.Max(3, Mathf.CeilToInt((float)(ageWindow / Time.fixedDeltaTime)));
            // Host input is sampled in this server tick. Remote claims must refer to snapshots actually sent.
            long ceiling = isLocalPlayer ? tick : lastPublishedTick;
            if (packet.ObservedServerTick <= 0 || packet.ObservedServerTick > ceiling
                || packet.ObservedServerTick < tick - delayBudget) return false;
            return true;
        }
        internal void MoveServer(long tick, double time, float deltaTime)
        {
            if (!serverStarted) return;
            DetectExternalMotorChange();
            int timeout = Mathf.Max(1, Mathf.CeilToInt(inputTimeout / deltaTime));
            PlayerInputFrame frame = isLocalPlayer ? serverInputs.Consume(tick, timeout, gapWaitTicks) : TakeMovementInput(tick);
            ActionRequest action = default;
            while (actionRequests.Count > 0)
            {
                ActionRequest candidate = actionRequests.Peek();
                if (Time.realtimeSinceStartupAsDouble - candidate.Received > inputTimeout
                    || !ValidateAccess(connectionToClient) || !player.CanAcceptNetworkAction(candidate.Kind, candidate.WeaponId))
                { actionRequests.Dequeue(); SendActionResult(candidate.Id, false, candidate.WeaponId, 0, time, 0); continue; }
                if ((isLocalPlayer ? serverInputs.Acknowledged : (hasMovementReport ? movementReport.Input.Sequence : movementTimeline.ProcessedInputSequence)) < candidate.InputSequence) break;
                // Ack can skip a lost/expired sample. Require an actually consumed, still-fresh
                // input context, even when the reliable action itself arrived only just now.
                long inputReceived;
                bool hasReceipt = isLocalPlayer ? serverInputs.TryGetConsumedInputReceipt(candidate.InputSequence, out inputReceived)
                    : movementTimeline.TryGetConsumedInputReceipt(candidate.InputSequence, out inputReceived);
                if (!hasReceipt || tick - inputReceived > timeout)
                { actionRequests.Dequeue(); SendActionResult(candidate.Id, false, candidate.WeaponId, 0, time, 0); continue; }
                action = actionRequests.Dequeue();
                break;
            }
            if (!frame.FireHeld || (isLocalPlayer ? serverInputs.FireRequestId : currentFireRequest) != triggerRequest || action.Kind == 2
                || triggerWeapon != player.PublishedFrame.WeaponId)
            { triggerRequest = triggerWeapon = 0; triggerOrdinal = 0; }
            if (action.Kind == 1)
            { triggerRequest = action.Id; triggerWeapon = action.WeaponId; triggerOrdinal = 0; }
            frame = new PlayerInputFrame(frame.Move, look: frame.Look, crouchHeld: frame.CrouchHeld,
                aimHeld: frame.AimHeld, firePressed: action.Kind == 1,
                fireHeld: frame.FireHeld && triggerRequest != 0 && (isLocalPlayer ? serverInputs.FireRequestId : currentFireRequest) == triggerRequest,
                reloadPressed: action.Kind == 2,
                interactPressed: frame.InteractPressed);
            if (isLocalPlayer ? serverInputs.TimedOut : movementTimedOut)
            {
                if (!timeoutCleared) player.ClearIntent();
                timeoutCleared = true;
            }
            else timeoutCleared = false;
            // Rewind amount comes from server-measured RTT, not the client's chosen snapshot tick.
            acceptedRewindDelay = isLocalPlayer ? 0d : Math.Min(ServerCombatHistory.MaximumRewindSeconds,
                Math.Max(0d, connectionToClient != null ? connectionToClient.rtt * 0.5d : 0d) + interpolationDelay);
            if (!ValidateAccess(connectionToClient)) { frame = default; player.ClearIntent(); RejectOperations("角色当前不可交互。"); }
            var firingWeapon = player.Equipment != null ? player.Equipment.CurrentWeapon : null;
            ulong priorShot = firingWeapon != null ? firingWeapon.Item.WeaponState.ShotSequence : 0;
            player.SimulateNetworkTick(frame, tick, time, deltaTime, this);
            FinishMovementStep();
            // Freeze association at acceptance, before combat callbacks can kill/cancel the shooter.
            if (triggerRequest != 0 && firingWeapon != null && firingWeapon.ItemId.Value == triggerWeapon
                && firingWeapon.Item.WeaponState.ShotSequence > priorShot)
            {
                if (shotCorrelations.Count >= 64) shotCorrelations.Clear();
                shotCorrelations[(triggerWeapon, firingWeapon.Item.WeaponState.ShotSequence)]
                    = new ShotCorrelation { Request = triggerRequest, Ordinal = ++triggerOrdinal };
            }
            if (action.Id != 0)
            {
                bool accepted = player.WasNetworkActionAccepted(action.Kind);
                SendActionResult(action.Id, accepted, action.WeaponId, 0, time, 0);
                if (!accepted && action.Kind == 1) { triggerRequest = triggerWeapon = 0; triggerOrdinal = 0; }
            }
            if (NetworkServer.aoi is BackpackSpatialInterestManagement spatial)
                spatial.NotifyIdentityMoved(netIdentity);
        }
        internal void FlushCombat(ServerCombatHistory history, long tick, double time, float deltaTime)
        {
            if (!serverStarted || player.Equipment == null) return;
            double rewind = time - acceptedRewindDelay;
            player.Equipment.FlushPendingShots(null, history, rewind);
        }
        internal void CompleteDueUse(double now)
        {
            if (!serverStarted) return;
            player.Consumables?.CompleteDueUse(now);
            var current = player.Consumables != null ? player.Consumables.State : default;
            if (hasRuleFingerprint && !current.Equals(ruleAuthority.Consumable))
            {
                constraintRevision++; ruleAuthority.Consumable = current;
                pendingCorrection = true; correctionReason = MovementRejectReason.AuthorityChanged;
            }
        }
        internal void PublishServer(long tick)
        {
            if (!serverStarted) return;
            player.RefreshNetworkPublication();
            // A host already applied this state by authoritative simulation; it must not reapply its own RPC snapshot.
            if (clientStarted && isLocalPlayer && isOwned && tick > 0)
            {
                lastServerTick = tick;
                MarkInitialAuthoritativeStateReceived();
            }
            PublishOwnerMovement(tick);
            if (tick % snapshotEveryTicks == 0)
            {
                PlayerNetworkState state = PlayerNetworkState.Capture(player);
                foreach (NetworkConnectionToClient observer in netIdentity.observers.Values)
                {
                    NetworkConnectionToClient recipient = observer;
                    if (ReferenceEquals(recipient, connectionToClient)) continue;
                    NetworkSendScheduler.QueueLatest(recipient, netId, StateStream, 1, 256, 0.25d, 0d,
                        () => TargetState(recipient, state, 0), () => CanSendTo(recipient));
                }
            }
        }
        private bool CanSendTo(NetworkConnectionToClient recipient) => this != null && serverStarted
            && isActiveAndEnabled && netIdentity != null && netId != 0
            && NetworkServer.spawned.TryGetValue(netId, out NetworkIdentity identity) && identity == netIdentity
            && recipient != null && recipient.isReady && recipient.isAuthenticated
            && netIdentity.observers.TryGetValue(recipient.connectionId, out NetworkConnectionToClient observer)
            && ReferenceEquals(observer, recipient);
        [TargetRpc(channel = Channels.Unreliable)]
        private void TargetState(NetworkConnectionToClient recipient, PlayerNetworkState state, ulong acknowledged)
        {
            if (!clientStarted || !isActiveAndEnabled || serverStarted || isLocalPlayer && isOwned || state.Tick <= lastServerTick || !MovementValues.Valid(state.Consumable)) return;
            lastServerTick = state.Tick;
            player.Attributes.ApplyReplicatedHealth(state.MaxHealth, state.Health);
            snapshots.Insert(state, snapshotEveryTicks * Time.fixedDeltaTime, interpolationDelay);
            if (snapshots.Count == 1) ApplyRemote(snapshots.Step(0f));
            DrainPendingShots();
            MarkInitialAuthoritativeStateReceived();
        }
        private void Update()
        {
            if (!clientStarted || serverStarted || isLocalPlayer || snapshots.Count == 0) return;
            ApplyRemote(snapshots.Step(Time.unscaledDeltaTime));
            DrainPendingShots();
        }
        private void ApplyRemote(PlayerNetworkState state)
        { player.RestoreMotorState(state.Motor); player.ApplyNetworkPresentation(state); }
        private void OnShotResolved(WeaponShotRequest request, IReadOnlyList<WeaponHitResult> hits)
        {
            bool damage = false, obstructed = false;
            var impacts = new NetworkShotImpact[Mathf.Min(hits.Count, 32)];
            for (int i = 0; i < hits.Count; i++)
            {
                WeaponHitResult hit = hits[i]; damage |= hit.DamageApplied; obstructed |= hit.MuzzleObstructed;
                if (i < impacts.Length) impacts[i] = new NetworkShotImpact
                { Point = hit.Point, Normal = hit.Normal, Hit = hit.HasHit, Obstructed = hit.MuzzleObstructed, DamageApplied = hit.DamageApplied };
            }
            var shot = new NetworkShotPresentation
            {
                WeaponId = request.Shot.WeaponId.Value, Sequence = request.Shot.Sequence,
                Tick = player.SimulationTick, Time = request.Shot.Time, Automatic = request.Shot.Sustained,
                Origin = request.MuzzleOrigin, Direction = request.AimDirection,
                DamageApplied = damage, Obstructed = obstructed, Impacts = impacts,
                ServerTime = NetworkTime.time
            };
            if (shotCorrelations.TryGetValue((shot.WeaponId, shot.Sequence), out ShotCorrelation correlation))
            {
                shotCorrelations.Remove((shot.WeaponId, shot.Sequence));
                shot.RequestId = correlation.Request;
                shot.Ordinal = correlation.Ordinal;
                SendActionResult(shot.RequestId, true, shot.WeaponId, shot.Sequence, shot.Time, shot.Ordinal);
            }
            foreach (NetworkConnectionToClient observer in netIdentity.observers.Values)
            {
                NetworkConnectionToClient recipient = observer;
                NetworkSendScheduler.QueueEvent(recipient, netId, ShotStream, 2, 128 + impacts.Length * 32,
                    0.08d, ShotLifetime, () => TargetShot(recipient, shot), () => CanSendTo(recipient));
            }
        }
        [TargetRpc(channel = Channels.Unreliable)]
        private void TargetShot(NetworkConnectionToClient recipient, NetworkShotPresentation shot)
        {
            if (!clientStarted || !isActiveAndEnabled || serverStarted) return;
            if (isLocalPlayer && isOwned && shot.RequestId != 0)
                player.ApplyLocalActionResult(shot.RequestId, true, shot.WeaponId, shot.Sequence, shot.Time, shot.Ordinal);
            if (shotWatermarks.TryGetValue(shot.WeaponId, out ulong watermark) && shot.Sequence <= watermark) return;
            if (!shotWatermarks.ContainsKey(shot.WeaponId))
            {
                if (shotWeapons.Count == 64) shotWatermarks.Remove(shotWeapons.Dequeue());
                shotWeapons.Enqueue(shot.WeaponId);
            }
            shotWatermarks[shot.WeaponId] = shot.Sequence;
            if (IsShotExpired(shot)) return;
            // State and the short-lived unreliable feedback may arrive in either order at spawn.
            if (!ready || !isLocalPlayer && (snapshots.Count == 0 || shot.Time > snapshots.SampleTime))
            {
                if (pendingShots.Count == 16) pendingShots.Dequeue();
                pendingShots.Enqueue(new PendingShot { Shot = shot, Received = Time.realtimeSinceStartupAsDouble });
                return;
            }
            PresentShot(shot);
        }
        private static bool IsShotExpired(NetworkShotPresentation shot)
            => NetworkTime.time - shot.ServerTime > ShotLifetime;
        private void DrainPendingShots()
        {
            int count = pendingShots.Count;
            for (int i = 0; i < count; i++)
            {
                PendingShot pending = pendingShots.Dequeue();
                if (Time.realtimeSinceStartupAsDouble - pending.Received > ShotLifetime || IsShotExpired(pending.Shot)) continue;
                if (!ready || !isLocalPlayer && (snapshots.Count == 0 || pending.Shot.Time > snapshots.SampleTime))
                    pendingShots.Enqueue(pending);
                else PresentShot(pending.Shot);
            }
        }
        private void PresentShot(NetworkShotPresentation shot)
        {
            player.ApplyNetworkShot(shot.WeaponId, shot.Sequence, shot.Time,
                shot.Automatic, shot.DamageApplied, shot.Obstructed);
            ShotPresented?.Invoke(shot);
        }
        public override void OnStopServer()
        {
            serverStarted = false;
            player.Attributes.Died -= OnServerDied;
            player.Attributes.Revived -= OnServerRevived;
            if (player.Equipment != null) player.Equipment.ShotResolved -= OnShotResolved;
            if (inventory != null) { inventory.ServerOperationScheduler = null; inventory.ServerConsumableUse = null; inventory.ConsumableStateProvider = null; inventory.ServerAccessValidator = null; inventory.ServerBeforeEquipmentOrWeaponOperation = null; inventory.ServerBeforeWeaponOperation = null; }
            Cleanup();
            base.OnStopServer();
        }
        public override void OnStopClient()
        {
            clientStarted = false;
            ResetInitialAuthoritativeState();
            ResetLocalInputGate();
            if (replicatedEquipment != null) replicatedEquipment.AppearanceChanged -= OnAppearanceChanged;
            if (serverStarted)
            {
                player.ConfigureNetworkSimulation(true, false);
                snapshots.Clear();
                if (characterBackpack != null) characterBackpack.IsLocalPlayer = false;
                SetLocalCameras(false); SetClientPresentation(false);
            }
            else Cleanup();
            base.OnStopClient();
        }
        private void Cleanup()
        {
            player?.CancelConsumableUse();
            InvalidateMovementSession();
            movementTimeline.ClearSession();
            movementHeld = default; heldReceivedTick = lastMovementReceived = -1; currentFireRequest = 0;
            movementInitialized = hasRuleFingerprint = hasAuthorityPublication = hasCommittedMotor = false;
            pendingHardBaseline = pendingCorrection = false;
            RejectPendingActions();
            player?.ResetOwnerPresentation();
            player?.Equipment?.SetLocalPresentationWeapon(null);
            CancelPendingOutput();
            serverInputs.Clear(); snapshots.Clear(); ResetInitialAuthoritativeState();
            ResetLocalInputGate();
            nextSequence = 1; previous = older = default;
            nextActionRequest = 1; lastActionRequest = ownerTriggerRequest = 0;
            lastServerTick = lastPublishedTick = 0; ownerPublications.Clear();
            shotWatermarks.Clear(); shotWeapons.Clear(); pendingShots.Clear();
            shotCorrelations.Clear();
            acceptedRewindDelay = 0d; timeoutCleared = false;
            RejectOperations("网络会话已结束。");
            player?.ClearIntent();
            if (player != null)
            {
                player.ConfigureNetworkSimulation(false, false);
                player.Attributes?.ConfigureAuthority(false);
                player.Equipment?.ConfigureAuthority(false);
            }
            if (characterBackpack != null) characterBackpack.IsLocalPlayer = false;
            SetLocalCameras(false); SetClientPresentation(false);
            NetworkPlayerSimulation.Unregister(this);
        }
        private void OnEnable()
        { if (serverStarted || clientStarted) { NetworkPlayerSimulation.Register(this); RefreshRole(); } }
        private void OnDisable()
        {
            if (player == null) return;
            if (clientStarted && ready && isLocalPlayer && isOwned && !serverStarted
                && NetworkClient.isConnected && NetworkClient.ready)
                CmdCancelActions(nextActionRequest - 1);
            InvalidateMovementSession();
            CancelPendingOutput(); pendingShots.Clear(); ResetInitialAuthoritativeState();
            RejectPendingActions(); player.ResetOwnerPresentation();
            shotCorrelations.Clear();
            serverInputs.DiscardPending(); snapshots.Clear();
            RejectOperations("角色已停用。"); player.CancelConsumableUse(); player.ClearIntent();
            player.Input.SetNetworkLocalInput(false);
            if (characterBackpack != null) characterBackpack.IsLocalPlayer = false;
            NetworkPlayerSimulation.Unregister(this);
        }
        private void CancelPendingOutput()
        {
            if (netIdentity == null || netId == 0) return;
            NetworkSendScheduler.CancelSource(netId, StateStream);
            NetworkSendScheduler.CancelSource(netId, ShotStream);
            NetworkSendScheduler.CancelSource(netId, OwnerAckStream);
        }
    }
    public struct NetworkShotImpact
    {
        public Vector3 Point, Normal;
        public bool Hit, Obstructed, DamageApplied;
    }
    public struct NetworkShotPresentation
    {
        public ulong WeaponId, Sequence;
        public ulong RequestId;
        public uint Ordinal;
        public long Tick;
        public double Time;
        public double ServerTime;
        public Vector3 Origin, Direction;
        public bool Automatic, DamageApplied, Obstructed;
        public NetworkShotImpact[] Impacts;
    }
}
