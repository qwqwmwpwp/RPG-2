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
    public sealed class NetworkPlayerController : NetworkBehaviour
    {
        [SerializeField, Range(0.1f, 0.5f)] private float inputTimeout = 0.25f;
        [SerializeField, Range(1, 4)] private int gapWaitTicks = 2;
        [SerializeField, Range(1, 4)] private int snapshotEveryTicks = 1;
        [SerializeField, Range(0.02f, 0.25f)] private float interpolationDelay = 0.1f;
        [SerializeField] private Behaviour[] additionalClientPresentationBehaviours = Array.Empty<Behaviour>();
        private const int PredictionCapacity = 128, OperationCapacity = 32;
        private const int StateStream = 1, ShotStream = 2;
        private const double OwnerStateMaxDelay = 0.1d, ShotLifetime = 0.3d;
        private readonly ServerPlayerInputBuffer serverInputs = new ServerPlayerInputBuffer();
        private readonly List<PlayerInputPacket> predictions = new List<PlayerInputPacket>(PredictionCapacity);
        private readonly List<PlayerNetworkState> snapshots = new List<PlayerNetworkState>(32);
        private readonly Queue<Operation> operations = new Queue<Operation>();
        private struct Operation { internal Action Execute; internal Action<string> Reject; }
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
        private long lastServerTick, lastPublishedTick;
        private bool timeoutCleared;
        private double acceptedRewindDelay;
        private double receivedAt;
        private PlayerNetworkState latestState;
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
            player.Equipment?.ConfigureAuthority(true, true);
            if (player.Equipment != null) player.Equipment.ShotResolved += OnShotResolved;
            if (inventory != null)
            {
                inventory.ServerOperationScheduler = QueueOperation;
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
            ResetInitialAuthoritativeState();
            player.Input.SetNetworkLocalInput(false);
            ClearLocalInputEdges();
            player.ClearIntent(); predictions.Clear();
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
            predictions.Clear();
            previous = older = default;
            // Host's device feeds the same authoritative buffer; discard its queued samples without cancelling actions.
            if (serverStarted && isLocalPlayer && isOwned)
            {
                serverInputs.DiscardPending();
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
            operations.Enqueue(new Operation { Execute = execute, Reject = reject });
            return true;
        }
        internal void DrainOperations()
        {
            // Freeze this tick's budget: callbacks cannot grow the amount of work drained this tick.
            int count = operations.Count;
            for (int i = 0; i < count; i++)
            {
                Operation op = operations.Dequeue();
                if (!ValidateAccess(connectionToClient)) { Reject(op, "角色当前不可交互。"); continue; }
                try { op.Execute(); }
                catch (Exception exception) { Debug.LogException(exception, this); Reject(op, "操作执行失败。"); }
            }
        }
        private void OnServerDied()
        { serverInputs.DiscardPending(); RejectOperations("角色已死亡。"); }
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
            if (!clientStarted || !isLocalPlayer || !isOwned || !localGameplayInputAllowed || (!serverStarted && !ready)) return;
            PlayerInputFrame frame = player.Input.ConsumeSimulationInput();
            var packet = new PlayerInputPacket
            {
                Sequence = nextSequence++, ObservedServerTick = serverStarted ? tick : lastServerTick,
                Move = frame.Move, Look = frame.Look,
                Crouch = frame.CrouchHeld, Aim = frame.AimHeld, FirePressed = frame.FirePressed,
                FireHeld = frame.FireHeld, Reload = frame.ReloadPressed, Interact = frame.InteractPressed
            };
            if (serverStarted) ReceiveInputs(packet, previous, older);
            else
            {
                if (predictions.Count == PredictionCapacity) predictions.RemoveAt(0);
                predictions.Add(packet);
                player.PredictNetworkMovement(packet.ToFrame(), player.SimulationTick + 1,
                    player.SimulationTime + deltaTime, deltaTime);
                CmdInputs(packet, previous, older);
            }
            older = previous; previous = packet;
        }
        [Command(channel = Channels.Unreliable)]
        private void CmdInputs(PlayerInputPacket newest, PlayerInputPacket previousPacket, PlayerInputPacket olderPacket)
            => ReceiveInputs(newest, previousPacket, olderPacket);
        private void ReceiveInputs(PlayerInputPacket newest, PlayerInputPacket previousPacket, PlayerInputPacket olderPacket)
        {
            if (!serverStarted || !ValidateAccess(connectionToClient)) return;
            int timeout = Mathf.Max(1, Mathf.CeilToInt(inputTimeout / Time.fixedDeltaTime));
            long tick = NetworkPlayerSimulation.Tick;
            AcceptPacket(olderPacket, tick, timeout);
            AcceptPacket(previousPacket, tick, timeout);
            AcceptPacket(newest, tick, timeout);
        }
        private void AcceptPacket(PlayerInputPacket packet, long tick, int timeout)
        {
            if (packet.Sequence == 0) return;
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
                || packet.ObservedServerTick < tick - delayBudget) return;
            serverInputs.Enqueue(packet, tick, timeout);
        }
        internal void MoveServer(long tick, double time, float deltaTime)
        {
            if (!serverStarted) return;
            int timeout = Mathf.Max(1, Mathf.CeilToInt(inputTimeout / deltaTime));
            PlayerInputFrame frame = serverInputs.Consume(tick, timeout, gapWaitTicks);
            if (serverInputs.TimedOut)
            {
                if (!timeoutCleared) player.ClearIntent();
                timeoutCleared = true;
            }
            else timeoutCleared = false;
            // Rewind amount comes from server-measured RTT, not the client's chosen snapshot tick.
            acceptedRewindDelay = isLocalPlayer ? 0d : Math.Min(ServerCombatHistory.MaximumRewindSeconds,
                Math.Max(0d, connectionToClient != null ? connectionToClient.rtt * 0.5d : 0d) + interpolationDelay);
            if (!ValidateAccess(connectionToClient)) { frame = default; player.ClearIntent(); RejectOperations("角色当前不可交互。"); }
            player.SimulateNetworkTick(frame, tick, time, deltaTime);
            if (NetworkServer.aoi is BackpackSpatialInterestManagement spatial)
                spatial.NotifyIdentityMoved(netIdentity);
        }
        internal void FlushCombat(ServerCombatHistory history, long tick, double time, float deltaTime)
        {
            if (!serverStarted || player.Equipment == null) return;
            double rewind = time - acceptedRewindDelay;
            player.Equipment.FlushPendingShots(null, history, rewind);
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
            if (tick % snapshotEveryTicks == 0)
            {
                PlayerNetworkState state = PlayerNetworkState.Capture(player);
                ulong acknowledged = serverInputs.Acknowledged;
                foreach (NetworkConnectionToClient observer in netIdentity.observers.Values)
                {
                    NetworkConnectionToClient recipient = observer;
                    bool owner = ReferenceEquals(recipient, connectionToClient);
                    NetworkSendScheduler.QueueLatest(recipient, netId, StateStream, owner ? 3 : 1, 256,
                        owner ? OwnerStateMaxDelay : 0.25d, 0d,
                        () =>
                        {
                            TargetState(recipient, state, acknowledged);
                            if (ReferenceEquals(recipient, connectionToClient))
                            {
                                lastPublishedTick = state.Tick;
                                if (ownerPublications.Count == 128) ownerPublications.RemoveAt(0);
                                ownerPublications.Add(new OwnerPublication
                                { Tick = state.Tick, SentAt = Time.realtimeSinceStartupAsDouble });
                            }
                        }, () => CanSendTo(recipient));
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
            if (!clientStarted || !isActiveAndEnabled || serverStarted || state.Tick <= lastServerTick) return;
            lastServerTick = state.Tick;
            latestState = state;
            receivedAt = Time.timeAsDouble;
            player.Attributes.ApplyReplicatedHealth(state.MaxHealth, state.Health);
            if (isLocalPlayer && isOwned && inventory != null)
                inventory.UpdateWeaponAction(state.Reloading, state.ReloadPhase, state.Time, state.ReloadDuration);
            if (isLocalPlayer && isOwned)
            {
                predictions.RemoveAll(p => p.Sequence <= acknowledged);
                player.ApplyNetworkPresentation(state);
                player.RestoreMotorState(state.Motor);
                for (int i = 0; localGameplayInputAllowed && i < predictions.Count; i++)
                    player.PredictNetworkMovement(predictions[i].ToFrame(), state.Tick + i + 1,
                        state.Time + (i + 1) * Time.fixedDeltaTime, Time.fixedDeltaTime);
            }
            else
            {
                if (snapshots.Count == 32) snapshots.RemoveAt(0);
                snapshots.Add(state);
                if (snapshots.Count == 1) ApplyRemote(state);
            }
            DrainPendingShots();
            MarkInitialAuthoritativeStateReceived();
        }
        private void Update()
        {
            if (!clientStarted || serverStarted || isLocalPlayer || snapshots.Count == 0) return;
            double renderTime = latestState.Time + (Time.timeAsDouble - receivedAt) - interpolationDelay;
            while (snapshots.Count > 2 && snapshots[1].Time <= renderTime) snapshots.RemoveAt(0);
            PlayerNetworkState a = snapshots[0], b = snapshots.Count > 1 ? snapshots[1] : a;
            float t = b.Time > a.Time ? Mathf.Clamp01((float)((renderTime - a.Time) / (b.Time - a.Time))) : 1f;
            PlayerNetworkState result = t < 1f ? a : b;
            PlayerMotorState motor = result.Motor;
            motor.Position = Vector3.Lerp(a.Motor.Position, b.Motor.Position, t);
            motor.Velocity = Vector3.Lerp(a.Motor.Velocity, b.Motor.Velocity, t);
            motor.Yaw = Mathf.LerpAngle(a.Motor.Yaw, b.Motor.Yaw, t);
            result.Motor = motor;
            result.ViewYaw = Mathf.LerpAngle(a.ViewYaw, b.ViewYaw, t);
            result.ViewPitch = Mathf.Lerp(a.ViewPitch, b.ViewPitch, t);
            result.Time = a.Time + (b.Time - a.Time) * t;
            ApplyRemote(result);
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
            if (shotWatermarks.TryGetValue(shot.WeaponId, out ulong watermark) && shot.Sequence <= watermark) return;
            if (!shotWatermarks.ContainsKey(shot.WeaponId))
            {
                if (shotWeapons.Count == 64) shotWatermarks.Remove(shotWeapons.Dequeue());
                shotWeapons.Enqueue(shot.WeaponId);
            }
            shotWatermarks[shot.WeaponId] = shot.Sequence;
            if (IsShotExpired(shot)) return;
            // State and the short-lived unreliable feedback may arrive in either order at spawn.
            if (!ready)
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
            while (pendingShots.Count > 0)
            {
                PendingShot pending = pendingShots.Dequeue();
                if (Time.realtimeSinceStartupAsDouble - pending.Received <= ShotLifetime && !IsShotExpired(pending.Shot))
                    PresentShot(pending.Shot);
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
            if (player.Equipment != null) player.Equipment.ShotResolved -= OnShotResolved;
            if (inventory != null) { inventory.ServerOperationScheduler = null; inventory.ServerAccessValidator = null; inventory.ServerBeforeEquipmentOrWeaponOperation = null; inventory.ServerBeforeWeaponOperation = null; }
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
                predictions.Clear(); snapshots.Clear();
                if (characterBackpack != null) characterBackpack.IsLocalPlayer = false;
                SetLocalCameras(false); SetClientPresentation(false);
            }
            else Cleanup();
            base.OnStopClient();
        }
        private void Cleanup()
        {
            CancelPendingOutput();
            serverInputs.Clear(); predictions.Clear(); snapshots.Clear(); ResetInitialAuthoritativeState();
            ResetLocalInputGate();
            nextSequence = 1; previous = older = default;
            lastServerTick = lastPublishedTick = 0; ownerPublications.Clear();
            shotWatermarks.Clear(); shotWeapons.Clear(); pendingShots.Clear();
            receivedAt = acceptedRewindDelay = 0d; latestState = default; timeoutCleared = false;
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
            CancelPendingOutput(); pendingShots.Clear(); ResetInitialAuthoritativeState();
            serverInputs.DiscardPending(); predictions.Clear(); snapshots.Clear();
            RejectOperations("角色已停用。"); player.ClearIntent();
            player.Input.SetNetworkLocalInput(false);
            if (characterBackpack != null) characterBackpack.IsLocalPlayer = false;
            NetworkPlayerSimulation.Unregister(this);
        }
        private void CancelPendingOutput()
        {
            if (netIdentity == null || netId == 0) return;
            NetworkSendScheduler.CancelSource(netId, StateStream);
            NetworkSendScheduler.CancelSource(netId, ShotStream);
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
        public long Tick;
        public double Time;
        public double ServerTime;
        public Vector3 Origin, Direction;
        public bool Automatic, DamageApplied, Obstructed;
        public NetworkShotImpact[] Impacts;
    }
}
