using System;
using System.Collections.Generic;
using Mirror;
using RPG4.Backpack.Network;
using RPG4.Combat;
using RPG4.PlayerIntent;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking
{
    public sealed partial class NetworkPlayerController
    {
        private readonly ServerPlayerInputBuffer serverInputs = new ServerPlayerInputBuffer();

        private readonly List<OwnerPublication> ownerPublications = new List<OwnerPublication>(128);
        private struct OwnerPublication { internal long Tick; internal double SentAt; }

        private bool timeoutCleared;
        private double acceptedRewindDelay;
        private PlayerInputPacket previous, older;
        private PlayerInputFrame ownerCycleInput;

        internal void SampleOwner(long tick, float deltaTime)
        {
            ownerCycleInput = default;
            if (!clientStarted || !isLocalPlayer || !isOwned) return;
            // Recover readiness before the gameplay gate: a disabled owner may have missed a reliable baseline.
            if (!serverStarted && !movementHistory.HasBaseline) { SampleMovementOwner(tick, deltaTime); return; }
            if (!localGameplayInputAllowed || (!serverStarted && !ready)) return;
            SampleMovementOwner(tick, deltaTime);
        }
        internal void CompleteOwnerInputCycle()
        {
            if (serverStarted && isLocalPlayer) player.CompleteLocalInputCycle();
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
            PlayerInputFrame frame;
            ulong actionThrough;
            if (isLocalPlayer)
            {
                // Host's owner already rotated its character buffer; do not delay it a second time.
                frame = serverInputs.Consume(tick, timeout, gapWaitTicks);
                PlayerInputFrame local = ownerCycleInput;
                ownerCycleInput = default;
                // Local-only view/HUD values do not travel in movement packets.
                frame = new PlayerInputFrame(frame.Move, frame.Look, local.Zoom, frame.CrouchHeld,
                    frame.AimHeld, fireHeld: frame.FireHeld, interactPressed: frame.InteractPressed,
                    quickUsePressed: local.QuickUsePressed);
                actionThrough = lastActionRequest;
                hasMovementReport = movementAccepted = usedAuthorityMotor = resolverInvoked = false;
            }
            else
            {
                frame = preparedServerInput;
                actionThrough = preparedActionThrough;
                preparedServerInput = default; preparedActionThrough = 0;
                movementAccepted = usedAuthorityMotor = resolverInvoked = false;
                if (!ValidateAccess(connectionToClient) || pendingHardBaseline
                    || movementTimeline.RebaseReason != MovementRejectReason.None
                    || hasMovementReport && (movementReport.Epoch != movementEpoch
                    || tick - heldReceivedTick > timeout))
                {
                    frame = default; hasMovementReport = false; currentFireRequest = 0;
                    pendingHardBaseline = true;
                    if (movementTimeline.RebaseReason != MovementRejectReason.None)
                        correctionReason = movementTimeline.RebaseReason;
                    RejectPendingActions();
                }
                if (!hasMovementReport && (heldReceivedTick < 0 || tick - heldReceivedTick > timeout))
                { frame = default; currentFireRequest = 0; }
                if (hasMovementReport)
                    movementTimeline.RecordConsumedInput(movementReport.Input.Sequence, heldReceivedTick);
            }
            ActionRequest action = default;
            while (actionRequests.Count > 0 && actionRequests.Peek().Id <= actionThrough)
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
            frame = new PlayerInputFrame(frame.Move, frame.Look, frame.Zoom, frame.CrouchHeld,
                frame.AimHeld, action.Kind == 1, frame.FireHeld && triggerRequest != 0,
                action.Kind == 2, frame.InteractPressed, frame.ViewAngles, frame.QuickUsePressed);
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
            if (NetworkServer.aoi is SpatialInterestManagement spatial)
                spatial.NotifyIdentityMoved(netIdentity);
            if (!isLocalPlayer)
            {
                executedServerHeld = new PlayerInputPacket { Crouch = frame.CrouchHeld, Aim = frame.AimHeld,
                    FireHeld = frame.FireHeld, FireRequestId = currentFireRequest };
                executedHeldReceivedTick = heldReceivedTick;
                // Selection and its report/receipt metadata remain frozen until the next server cycle.
                preparedServerInput = TakeMovementInput(tick);
                preparedActionThrough = lastActionRequest;
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
    }
}
