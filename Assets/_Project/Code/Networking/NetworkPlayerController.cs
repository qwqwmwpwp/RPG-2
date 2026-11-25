using RPG4.CharacterAnimation;
using System;
using System.Collections.Generic;
using Cinemachine;
using Mirror;
using RPG4.Backpack.Network;
using RPG4.LoadoutTemplate;
using RPG4.LoadoutContracts;
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
        private readonly PlayerSnapshotTimeline snapshots = new PlayerSnapshotTimeline();
        private readonly Queue<Operation> operations = new Queue<Operation>();
        private struct Operation { internal Action Execute; internal Action<string> Reject; internal double Received; }
        private Player player;
        private readonly Dictionary<Behaviour, bool> visualBehaviours = new Dictionary<Behaviour, bool>();
        private NetworkRoleLoadout loadout;
        [SyncVar(hook = nameof(OnPublicEquipmentChanged))] private string publicEquipmentState;
        private RPG4.Input.PlayerInput localInput;
        private bool serverStarted, clientStarted, ready;
        private bool localGameplayInputAllowed = true, localGameplayInputExplicitlySet;
        private ulong nextSequence = 1;
        private long lastServerTick, lastPublishedTick;
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
                if (behaviour is CharacterPresentationSystem)
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
            loadout = GetComponent<NetworkRoleLoadout>();
            if (loadout != null) loadout.Changed += RefreshLocalInputGate;
            // Runs before Player.Awake, so no server/remote ever binds a device in Player initialization.
            player.ConfigureNetworkSimulation(false, false);
            GetComponent<PlayerEquipment>()?.ConfigureAuthority(false);

            SetLocalCameras(false);
        }
        public override void OnStartServer()
        {
            base.OnStartServer();
            serverStarted = true;
            ResetInitialAuthoritativeState();
            ApplyDefaultLocalInputGate();
            player.ConfigureNetworkSimulation(true, isLocalPlayer);
            if (player.Equipment != null)
            {
                player.Equipment.ConfigureAuthority(true, true);
                player.Equipment.ShotResolved += OnShotResolved;
            }
            BindPublicEquipment();
            player.Attributes.Died += OnServerDied;
            player.Attributes.Revived += OnServerRevived;
            NetworkPlayerSimulation.Register(this);
        }
        public override void OnStartClient()
        {
            base.OnStartClient();
            clientStarted = true;
            ApplyPublicEquipment(publicEquipmentState);
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
            player.LocalLoadout.UnbindNetwork();
            InvalidateMovementSession();
            ResetInitialAuthoritativeState();
            player.Input.SetNetworkLocalInput(false);
            player.ResetOwnerPresentation();
            player.Equipment?.SetLocalPresentationWeapon(null);
            player.Consumables?.ConfigureNetworkRequest(null);
            player.CancelConsumableUse();
            ClearLocalInputEdges();
            player.ClearIntent();

            SetLocalCameras(false);
            base.OnStopAuthority();
        }
        private void RefreshRole()
        {
            bool local = isLocalPlayer && isOwned;
            if (!local) player.LocalLoadout.UnbindNetwork();
            else if (!(NetworkManager.singleton is AuthoritativeNetworkManager manager && manager.UsesGameplaySceneBindings))
                player.LocalLoadout.BindNetwork(loadout);
            ApplyDefaultLocalInputGate();
            player.ConfigureNetworkSimulation(serverStarted, local);
            localInput.SetLocalGameplayInputAllowed(localGameplayInputAllowed);
            player.Consumables?.ConfigureNetworkRequest(local ? RequestConsumable : (Func<ConsumableUseOperation, bool>)null);
            if (!serverStarted) player.Equipment?.ConfigureAuthority(false);
            SetClientPresentation(clientStarted);

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
        private void RefreshLocalInputGate()
        {
            if (isLocalPlayer && isOwned)
                localInput?.SetLocalGameplayInputAllowed(localGameplayInputAllowed
                    && (loadout == null || !loadout.IsWindowOpen)
                    && (RPG4.UI.UIManager.Instance == null || !RPG4.UI.UIManager.Instance.BlocksGameplayInput));
        }
        private void OnDestroy()
        {
            UnbindPublicEquipment();
            if (!ReferenceEquals(loadout, null)) loadout.Changed -= RefreshLocalInputGate;
        }
        private void Update()
        {
            RefreshLocalInputGate();
            if (!clientStarted || serverStarted || isLocalPlayer || snapshots.Count == 0) return;
            ApplyRemote(snapshots.Step(Time.unscaledDeltaTime));
            DrainPendingShots();
        }
        private void ApplyRemote(PlayerNetworkState state)
        { player.RestoreMotorState(state.Motor); player.ApplyNetworkPresentation(state); }
        public override void OnStopServer()
        {
            serverStarted = false;
            player.Attributes.Died -= OnServerDied;
            player.Attributes.Revived -= OnServerRevived;
            UnbindPublicEquipment();
            if (player.Equipment != null) player.Equipment.ShotResolved -= OnShotResolved;

            Cleanup();
            base.OnStopServer();
        }
        public override void OnStopClient()
        {
            player.LocalLoadout.UnbindNetwork();
            clientStarted = false;
            ResetInitialAuthoritativeState();
            ResetLocalInputGate();
            if (serverStarted)
            {
                player.ConfigureNetworkSimulation(true, false);
                snapshots.Clear();

                SetLocalCameras(false); SetClientPresentation(false);
            }
            else Cleanup();
            base.OnStopClient();
        }
        private void Cleanup()
        {
            player?.CancelConsumableUse();
            player?.Consumables?.ConfigureNetworkRequest(null);
            lastConsumableRequest = 0;
            InvalidateMovementSession();
            movementTimeline.ClearSession();
            movementHeld = default; heldReceivedTick = lastMovementReceived = -1; currentFireRequest = 0;
            preparedServerInput = default; hasMovementReport = false; preparedActionThrough = 0;
            ownerCycleInput = default;
            executedServerHeld = default; executedHeldReceivedTick = -1;
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
                player.Equipment?.ConfigureAuthority(false);
            }

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
