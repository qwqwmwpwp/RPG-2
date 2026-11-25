using System;
using Mirror;
using RPG4.Backpack.Network;
using RPG4.PlayerIntent;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking
{
    public sealed partial class NetworkPlayerController : IPlayerNetworkMovementResolver
    {
        [SerializeField, Range(1, 4)] private int movementSendEveryTicks = 2;
        private const int OwnerAckStream = 3;
        private readonly ServerMovementTimeline movementTimeline = new ServerMovementTimeline();
        private readonly OwnerMovementHistory movementHistory = new OwnerMovementHistory();
        private uint movementEpoch, correctionId, constraintRevision = 1, authorityRevision;
        private uint ownerAuthorityRevision;
        private bool movementInitialized, pendingCorrection, pendingHardBaseline, hasMovementReport, movementAccepted;
        private bool movementTimedOut, ownerHistoryEnabled, hasAuthorityPublication, usedAuthorityMotor, resolverInvoked;
        private MovementRejectReason correctionReason;
        private PlayerMovementReport movementReport, previousReport, olderReport, oldestReport;
        private PlayerInputPacket movementHeld, executedServerHeld;
        private long executedHeldReceivedTick = -1;
        private PlayerInputFrame preparedServerInput;
        private ulong preparedActionThrough;

        private long heldReceivedTick = -1;
        private double nextOwnerBaselineRequest;
        private PlayerMotorState referenceMotor;
        private PlayerAuthoritySnapshot publishedAuthority;
        private long lastMovementReceived = -1, lastCorrectionTick = -5, baselineTick;
        private double lastBaselineRequest = double.NegativeInfinity;
        private ulong currentFireRequest;
        private bool hasRuleFingerprint;
        private PlayerAuthoritySnapshot ruleAuthority;
        private MovementRuleContext ruleConfiguration;
        private MovementKinematicsSettings ruleSettings;
        private bool ruleHurt, ownerPredictionPaused, hasCommittedMotor;
        private PlayerMotorState lastCommittedMotor;

        private void EnableOwnerMovementHistory()
        {
            bool enabled = clientStarted && !serverStarted && isLocalPlayer && isOwned && isActiveAndEnabled;
            if (enabled == ownerHistoryEnabled) return;
            ownerHistoryEnabled = enabled;
            movementHistory.Reset(enabled); ownerPredictionPaused = false;
            previousReport = olderReport = oldestReport = default;
        }
        private void InvalidateMovementSession()
        {
            ownerHistoryEnabled = false; movementHistory.Reset(false); ownerPredictionPaused = false;
            previousReport = olderReport = oldestReport = default;
            hasMovementReport = false;
            if (serverStarted) { pendingHardBaseline = true; correctionReason = MovementRejectReason.Disabled; }
        }
        private void ClearOwnerMovementInput()
        {
            if (!clientStarted || !isLocalPlayer || !isOwned) return;
            if (!serverStarted && !movementHistory.HasBaseline) return;
            if (!serverStarted && movementHistory.LastStep == 0 && movementHistory.Count == 0) return;
            previousReport = olderReport = oldestReport = default;
            if (serverStarted)
            {
                pendingHardBaseline = true; correctionReason = MovementRejectReason.Disabled;
                return;
            }
            movementHistory.Reset(true); ownerHistoryEnabled = true; ownerPredictionPaused = false;
            nextOwnerBaselineRequest = Time.unscaledTimeAsDouble + 0.3d;
            if (NetworkClient.isConnected && NetworkClient.ready)
                CmdRequestMovementBaseline(0);
        }
        private void SampleMovementOwner(long tick, float deltaTime)
        {
            if (!serverStarted && !movementHistory.HasBaseline)
            {
                if (Time.unscaledTimeAsDouble >= nextOwnerBaselineRequest)
                { nextOwnerBaselineRequest = Time.unscaledTimeAsDouble + 0.3d; CmdRequestMovementBaseline(0); }
                return;
            }
            if (!serverStarted && (ownerPredictionPaused || movementHistory.Count >= OwnerMovementHistory.Capacity))
            { PauseOwnerMovementPrediction(); return; }
            if (!player.IsAlive || !player.isActiveAndEnabled) return;
            PlayerInputFrame frame = player.BeginLocalInputCycle();
            ownerCycleInput = frame;
            try
            {
                if (!frame.FireHeld) ownerTriggerRequest = 0;
                ulong actionId = 0;
                byte actionKind = frame.ReloadPressed ? (byte)2 : frame.FirePressed ? (byte)1 : (byte)0;
                ulong weaponId = player.PublishedFrame?.WeaponId ?? 0;
                if (actionKind != 0 && weaponId != 0 && nextActionRequest < ulong.MaxValue)
                {
                    actionId = nextActionRequest++;
                    if (actionKind == 1) ownerTriggerRequest = actionId;
                    else ownerTriggerRequest = 0;
                }
                if (!serverStarted) player.UpdateLocalActionInput(frame, Time.unscaledTimeAsDouble);
                PlayerInputPacket packet = new PlayerInputPacket
                {
                    Sequence = nextSequence++, ObservedServerTick = serverStarted ? tick : lastServerTick,
                    FireRequestId = frame.FireHeld ? ownerTriggerRequest : 0,
                    Move = frame.Move, Look = frame.Look, Crouch = frame.CrouchHeld, Aim = frame.AimHeld,
                    FireHeld = frame.FireHeld, Interact = frame.InteractPressed
                };
                if (serverStarted)
                {
                    ReceiveInputs(packet, previous, older);
                    older = previous; previous = packet;
                    if (actionId != 0) ReceiveAction(actionId, actionKind, weaponId, packet.Sequence);
                    return;
                }
                player.PredictNetworkMovement(packet.ToFrame(), player.SimulationTick + 1, player.SimulationTime + deltaTime, deltaTime);
                PlayerMovementReport report = new PlayerMovementReport
                {
                    Epoch = movementHistory.Epoch, CorrectionId = movementHistory.CorrectionId, ConstraintRevision = movementHistory.ConstraintRevision,
                    Step = movementHistory.LastStep + 1, Input = packet, PredictedMotor = player.CaptureMotorState(),
                    Aiming = player.ExecutedMovementRules.Aiming, Strafe = player.ExecutedMovementRules.Plan.Strafe
                };
                if (!movementHistory.Store(new PlayerMovementRecord { Report = report, PredictionTime = player.SimulationTime }))
                { PauseOwnerMovementPrediction(); return; }
                if (actionId != 0 || report.Step % (ulong)Mathf.Max(1, movementSendEveryTicks) == 0)
                    CmdMovement(report, previousReport, olderReport, oldestReport);
                if (actionId != 0)
                {
                    player.AnticipateLocalActions(frame, actionId, Time.unscaledTimeAsDouble);
                    CmdAction(actionId, actionKind, weaponId, packet.Sequence);
                }
                oldestReport = olderReport; olderReport = previousReport; previousReport = report;
            }
            finally
            {
                // Host completes only after authority has executed this same device snapshot.
                if (!serverStarted) player.CompleteLocalInputCycle();
            }
        }
        private void PauseOwnerMovementPrediction()
        {
            if (!ownerPredictionPaused || Time.unscaledTimeAsDouble >= nextOwnerBaselineRequest)
            {
                nextOwnerBaselineRequest = Time.unscaledTimeAsDouble + 0.3d;
                CmdRequestMovementBaseline(movementHistory.Epoch);
            }
            // Preserve every unclosed record until the server baseline closes or retires it.
            ownerPredictionPaused = true;
        }
        private void DetectExternalMotorChange()
        {
            if (isLocalPlayer || !movementInitialized || !hasCommittedMotor) return;
            PlayerMotorState current = player.CaptureMotorState();
            if ((current.Position - lastCommittedMotor.Position).sqrMagnitude <= 0.00000001f
                && (current.Velocity - lastCommittedMotor.Velocity).sqrMagnitude <= 0.00000001f
                && Mathf.Abs(Mathf.DeltaAngle(current.Yaw, lastCommittedMotor.Yaw)) <= 0.001f
                && Mathf.Abs(current.VerticalVelocity - lastCommittedMotor.VerticalVelocity) <= 0.0001f
                && Mathf.Abs(current.TurnVelocity - lastCommittedMotor.TurnVelocity) <= 0.0001f
                && current.Crouched == lastCommittedMotor.Crouched) return;
            constraintRevision++;
            pendingHardBaseline = true; correctionReason = MovementRejectReason.AuthorityChanged;
        }
        [Command(channel = Channels.Unreliable)]
        private void CmdMovement(PlayerMovementReport newest, PlayerMovementReport previousPacket, PlayerMovementReport olderPacket, PlayerMovementReport oldestPacket)
        {
            if (!serverStarted || !movementInitialized || !ValidateAccess(connectionToClient)) return;
            ReceiveMovement(oldestPacket); ReceiveMovement(olderPacket); ReceiveMovement(previousPacket); ReceiveMovement(newest);
        }
        private void ReceiveMovement(in PlayerMovementReport report)
        {
            if (!report.IsValid || !ObservationIsFresh(report.Input, NetworkPlayerSimulation.Tick)) return;
            if (movementTimeline.Enqueue(report, NetworkPlayerSimulation.Tick))
                lastMovementReceived = NetworkPlayerSimulation.Tick;
        }
        [Command]
        private void CmdRequestMovementBaseline(uint installedEpoch)
        {
            // Recovery does not require a living/interactable character.
            if (!serverStarted || !isActiveAndEnabled || connectionToClient == null
                || connectionToClient.identity != netIdentity || !connectionToClient.isAuthenticated) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - lastBaselineRequest < 0.25d) return;
            lastBaselineRequest = now;
            pendingHardBaseline = true; correctionReason = MovementRejectReason.HistoryOverflow;
        }
        private PlayerInputFrame TakeMovementInput(long tick)
        {
            hasMovementReport = movementAccepted = usedAuthorityMotor = resolverInvoked = false; currentFireRequest = movementHeld.FireRequestId;
            movementTimeline.TimeoutTicks = Mathf.Max(1, Mathf.CeilToInt(inputTimeout / 0.02f));
            movementTimeline.GapWaitTicks = gapWaitTicks;
            if (!movementInitialized || pendingHardBaseline)
            { movementHeld = default; currentFireRequest = 0; RejectPendingActions(); return default; }
            MovementTakeResult take = movementTimeline.Take(tick, out movementReport);
            if (take == MovementTakeResult.RebaseRequired)
            { pendingHardBaseline = true; correctionReason = movementTimeline.RebaseReason; RejectPendingActions(); return default; }
            if (take == MovementTakeResult.Ready)
            {
                hasMovementReport = true; movementTimedOut = false;
                movementHeld = movementReport.Input; heldReceivedTick = movementTimeline.TakenReceivedTick;
                currentFireRequest = movementReport.Input.FireRequestId;
                return movementReport.Input.ToFrame();
            }
            if (lastMovementReceived >= 0 && tick - lastMovementReceived > movementTimeline.TimeoutTicks && !movementTimedOut)
            {
                movementTimedOut = true; pendingHardBaseline = true; correctionReason = MovementRejectReason.MissingInput;
                movementHeld = default; currentFireRequest = 0;
                RejectPendingActions();
            }
            if (heldReceivedTick < 0 || tick - heldReceivedTick > movementTimeline.TimeoutTicks)
            { movementHeld = default; currentFireRequest = 0; }
            return new PlayerInputFrame(Vector2.zero, crouchHeld: movementHeld.Crouch,
                aimHeld: movementHeld.Aim, fireHeld: movementHeld.FireHeld);
        }
        PlayerMovementCommit IPlayerNetworkMovementResolver.Resolve(in PlayerMovementEvaluation evaluation)
        {
            resolverInvoked = true;
            // Host uses the native authority motor and never receives its own reconciliation RPC.
            if (isLocalPlayer) return new PlayerMovementCommit(true, default);
            UpdateRuleRevision(evaluation);
            if (!hasMovementReport)
            {
                MovementPathResult path = player.MovementWorld.CheckPath(evaluation.StartMotor, evaluation.StartMotor);
                if (path.Kind == MovementPathKind.ClearFlatGround
                    && Mathf.Abs(path.GroundedRootY - evaluation.StartMotor.Position.y) <= 0.002f)
                    return new PlayerMovementCommit(false, evaluation.StartMotor);
                pendingCorrection = true;
                if (correctionReason == MovementRejectReason.None) correctionReason = MovementRejectReason.Collision;
                usedAuthorityMotor = true;
                return new PlayerMovementCommit(true, default);
            }
            MovementValidationContext context = MovementValidationContext.Create(movementEpoch, movementReport.Step, constraintRevision, evaluation, referenceMotor);
            MovementValidationResult result = PlayerMovementValidator.Validate(context, movementReport, player.MovementWorld);
            movementAccepted = result.Accepted;
            if (result.Accepted) referenceMotor = result.ReferenceMotor;
            else { pendingCorrection = true; correctionReason = result.Reason; }
            usedAuthorityMotor = result.UseAuthorityMotor;
            return result.ToCommit();
        }
        private void UpdateRuleRevision(in PlayerMovementEvaluation e)
        {
            // Input-selected aim/crouch/move are not authority constraints. Business permissions and
            // their effective speed are revisioned separately from interpolation phases.
            PlayerAuthoritySnapshot a = PlayerAuthoritySnapshot.Capture(player);
            MovementRuleContext configuration = player.MovementConfiguration;
            bool hurt = e.Time < a.HurtUntil;
            bool changed = hasRuleFingerprint && (a.Alive != ruleAuthority.Alive
                || a.WeaponAvailable != ruleAuthority.WeaponAvailable || a.WeaponId != ruleAuthority.WeaponId
                || !a.Consumable.Equals(ruleAuthority.Consumable)
                || a.Reloading != ruleAuthority.Reloading || hurt != ruleHurt
                || a.Action.ShotActive != ruleAuthority.Action.ShotActive || a.Action.FadingOut != ruleAuthority.Action.FadingOut
                || !configuration.Equals(ruleConfiguration) || !e.Settings.Equals(ruleSettings));
            if (changed)
            { constraintRevision++; pendingCorrection = true; correctionReason = MovementRejectReason.AuthorityChanged; }
            ruleAuthority = a; ruleConfiguration = configuration; ruleSettings = e.Settings; ruleHurt = hurt;
            hasRuleFingerprint = true;
        }
        private void FinishMovementStep()
        {
            if (isLocalPlayer) return;
            if (hasMovementReport) movementTimeline.CommitResult(movementReport, movementAccepted);
            if (!resolverInvoked)
            {
                usedAuthorityMotor = true;
                PlayerMotorState motor = player.CaptureMotorState();
                if (!motor.Equals(referenceMotor)) { pendingCorrection = true; correctionReason = MovementRejectReason.Kinematics; }
            }
            if (usedAuthorityMotor) referenceMotor = player.CaptureMotorState();
            lastCommittedMotor = player.CaptureMotorState(); hasCommittedMotor = true;
        }
        private void RecordOwnerPublication(long tick)
        {
            lastPublishedTick = Math.Max(lastPublishedTick, tick);
            if (ownerPublications.Count == 128) ownerPublications.RemoveAt(0);
            ownerPublications.Add(new OwnerPublication { Tick = tick, SentAt = Time.realtimeSinceStartupAsDouble });
        }
        private void PublishOwnerMovement(long tick)
        {
            if (isLocalPlayer || !CanSendTo(connectionToClient)) return;
            if (!movementInitialized || pendingHardBaseline)
            {
                movementEpoch++; if (movementEpoch == 0) movementEpoch = 1;
                correctionId = 0;
                preparedServerInput = default; hasMovementReport = false; preparedActionThrough = 0;
                movementTimeline.RetirePendingInputs();
                RejectPendingActions();
                PlayerMovementBaseline baseline = CaptureMovementBaseline(0);
                movementTimeline.Reset(baseline, tick);
                movementHeld = executedServerHeld = default; currentFireRequest = 0;
                executedHeldReceivedTick = -1;
                referenceMotor = lastCommittedMotor = baseline.State.Motor; hasCommittedMotor = true;
                movementInitialized = true; pendingHardBaseline = pendingCorrection = false;
                lastMovementReceived = tick;
                TargetMovementBaseline(connectionToClient, baseline); RecordOwnerPublication(tick);
            }
            else if (pendingCorrection && tick - lastCorrectionTick >= 5)
            {
                correctionId++;
                PlayerMovementBaseline baseline = CaptureMovementBaseline(movementTimeline.ProcessedStep);
                movementTimeline.ReturnTakenInput();
                // Returning an unexecuted report is not a release of the last executed trigger.
                movementHeld = executedServerHeld;
                heldReceivedTick = executedHeldReceivedTick;
                currentFireRequest = movementHeld.FireRequestId;
                preparedServerInput = movementHeld.ToFrame(false);
                hasMovementReport = false; preparedActionThrough = lastActionRequest;
                movementTimeline.CommitCorrection(baseline);
                TargetMovementCorrection(connectionToClient, new PlayerMovementCorrection { Baseline = baseline, Reason = correctionReason });
                RecordOwnerPublication(tick); lastCorrectionTick = tick; pendingCorrection = false;
            }
            PlayerAuthoritySnapshot authority = PlayerAuthoritySnapshot.Capture(player);
            if (!hasAuthorityPublication || AuthorityChanged(publishedAuthority, authority))
            {
                publishedAuthority = authority; hasAuthorityPublication = true;
                TargetAuthorityState(connectionToClient, new PlayerAuthorityState { Revision = ++authorityRevision, ConstraintRevision = constraintRevision, Snapshot = authority });
                RecordOwnerPublication(tick);
            }
            if (!pendingCorrection && tick % 2 == 0)
            {
                PlayerMovementAck ack = new PlayerMovementAck { Epoch = movementEpoch, CorrectionId = correctionId,
                    ConstraintRevision = constraintRevision, AcceptedStep = movementTimeline.AcceptedStep,
                    ProcessedInputSequence = movementTimeline.ProcessedInputSequence, ServerTick = tick };
                NetworkConnectionToClient target = connectionToClient;
                NetworkSendScheduler.QueueLatest(target, netId, OwnerAckStream, 3, 48, OwnerStateMaxDelay, 0d,
                    () => { TargetMovementAck(target, ack); RecordOwnerPublication(ack.ServerTick); }, () => CanSendTo(target));
            }
        }
        private PlayerMovementBaseline CaptureMovementBaseline(ulong step) => new PlayerMovementBaseline
        { Epoch = movementEpoch, CorrectionId = correctionId, ConstraintRevision = constraintRevision,
            Step = step, ProcessedInputSequence = movementTimeline.ProcessedInputSequence, State = PlayerNetworkState.Capture(player),
            LastMotorMode = player.LastMotorMode };
        private static bool AuthorityChanged(PlayerAuthoritySnapshot a, PlayerAuthoritySnapshot b) => !a.Consumable.Equals(b.Consumable) || a.Health != b.Health
            || a.MaxHealth != b.MaxHealth || a.Alive != b.Alive || a.HurtUntil != b.HurtUntil || a.WeaponId != b.WeaponId
            || a.WeaponAvailable != b.WeaponAvailable || a.Reloading != b.Reloading || a.ShotSequence != b.ShotSequence
            || a.ShotTime != b.ShotTime || a.ReloadDuration != b.ReloadDuration || a.ShotDuration != b.ShotDuration
            || a.FadeDuration != b.FadeDuration || a.Action.ShotActive != b.Action.ShotActive || a.Action.FadingOut != b.Action.FadingOut;
        [TargetRpc]
        private void TargetMovementBaseline(NetworkConnectionToClient target, PlayerMovementBaseline baseline) => InstallMovementBaseline(baseline);
        [TargetRpc]
        private void TargetMovementCorrection(NetworkConnectionToClient target, PlayerMovementCorrection correction) => InstallMovementBaseline(correction.Baseline);
        private void InstallMovementBaseline(in PlayerMovementBaseline baseline)
        {
            if (!clientStarted || serverStarted || !isLocalPlayer || !isOwned || !isActiveAndEnabled) return;
            EnableOwnerMovementHistory();
            if (baseline.Epoch > movementHistory.Epoch && baseline.IsValid)
                movementHistory.RetireExpiredInputs(baseline.State.Time - inputTimeout);
            if (!movementHistory.TryRebase(baseline, out PlayerInputPacket[] replay)) return;
            player.Attributes.ApplyReplicatedHealth(baseline.State.MaxHealth, baseline.State.Health);
            if (!movementHistory.HasBaseline || movementHistory.Epoch != baseline.Epoch || movementHistory.CorrectionId != baseline.CorrectionId) return;
            ownerPredictionPaused = false;
            baselineTick = baseline.State.Tick; lastServerTick = Math.Max(lastServerTick, baseline.State.Tick);
            bool discontinuity = !ready || player.PublishedFrame.Alive != baseline.State.Alive;
            player.BeginOwnerReconcile();
            try
            {
                player.ApplyNetworkPresentation(baseline.State);
                player.RestoreMotorState(baseline.State.Motor, baseline.LastMotorMode);
                previousReport = olderReport = oldestReport = default;
                for (int i = 0; i < replay.Length; i++)
                {
                    player.PredictNetworkMovement(replay[i].ToFrame(), baseline.State.Tick + i + 1,
                        baseline.State.Time + (i + 1) * 0.02d, 0.02f, replay: true);
                    PlayerMovementRecord record = movementHistory.Records[i];
                    record.Report.CorrectionId = baseline.CorrectionId; record.Report.ConstraintRevision = movementHistory.ConstraintRevision;
                    record.Report.PredictedMotor = player.CaptureMotorState();
                    record.PredictionTime = player.SimulationTime;
                    record.Report.Aiming = player.ExecutedMovementRules.Aiming;
                    record.Report.Strafe = player.ExecutedMovementRules.Plan.Strafe;
                    movementHistory.ReplaceReplay(i, record);
                    // Bounded groups carry every replayed input once; only prediction may replace a queued step.
                    if (i % 4 == 3 || i == replay.Length - 1)
                        CmdMovement(record.Report, previousReport, olderReport, oldestReport);
                    oldestReport = olderReport; olderReport = previousReport; previousReport = record.Report;
                }
            }
            finally { player.EndOwnerReconcile(discontinuity); }
            movementHistory.RetryPendingAck();
            MarkInitialAuthoritativeStateReceived(); DrainPendingShots();
        }
        [TargetRpc(channel = Channels.Unreliable)]
        private void TargetMovementAck(NetworkConnectionToClient target, PlayerMovementAck ack)
        {
            if (!clientStarted || serverStarted || !isLocalPlayer || !isOwned || !isActiveAndEnabled) return;
            if (!movementHistory.HasBaseline || ack.Epoch != movementHistory.Epoch) return;
            lastServerTick = Math.Max(lastServerTick, ack.ServerTick);
            movementHistory.Acknowledge(ack);
        }
        [TargetRpc]
        private void TargetAuthorityState(NetworkConnectionToClient target, PlayerAuthorityState state)
        {
            if (!clientStarted || serverStarted || !isLocalPlayer || !isOwned || !isActiveAndEnabled
                || !MovementValues.Valid(state.Snapshot.Consumable)
                || !movementHistory.HasBaseline || state.Revision <= ownerAuthorityRevision || state.Snapshot.Tick < baselineTick
                || state.ConstraintRevision < movementHistory.ConstraintRevision) return;
            ownerAuthorityRevision = state.Revision; lastServerTick = Math.Max(lastServerTick, state.Snapshot.Tick);
            player.Attributes.ApplyReplicatedHealth(state.Snapshot.MaxHealth, state.Snapshot.Health);
            if (!movementHistory.HasBaseline) return;
            player.ApplyNetworkAuthorityState(state.Snapshot);
            movementHistory.ApplyConstraintRevision(state.ConstraintRevision);
        }
    }
}
