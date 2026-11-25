using System;
using System.Collections.Generic;
using Mirror;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
using RPG4.Combat;
using RPG4.LoadoutContracts;
using RPG4.PlayerIntent;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking
{
    public sealed partial class NetworkPlayerController
    {
        private int lastConsumableRequest;

        private readonly Dictionary<ulong, ulong> shotWatermarks = new Dictionary<ulong, ulong>();
        private readonly Queue<ulong> shotWeapons = new Queue<ulong>();
        private readonly Queue<PendingShot> pendingShots = new Queue<PendingShot>();
        private struct PendingShot { internal NetworkShotPresentation Shot; internal double Received; }

        private struct ActionRequest { internal ulong Id, WeaponId, InputSequence; internal byte Kind; internal double Received; }
        private readonly Queue<ActionRequest> actionRequests = new Queue<ActionRequest>(32);
        private ulong nextActionRequest = 1, lastActionRequest, ownerTriggerRequest, triggerRequest, triggerWeapon;
        private uint triggerOrdinal;
        private struct ShotCorrelation { internal ulong Request; internal uint Ordinal; }
        private readonly Dictionary<(ulong Weapon, ulong Shot), ShotCorrelation> shotCorrelations
            = new Dictionary<(ulong Weapon, ulong Shot), ShotCorrelation>();

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
            if (!serverStarted || id == 0) return;
            bool fresh = id > lastActionRequest;
            if (fresh) lastActionRequest = id;
            if (!fresh || (kind != 1 && kind != 2) || inputSequence == 0 || weapon == 0
                || actionRequests.Count >= OperationCapacity || !ValidateAccess(connectionToClient)
                || loadout == null || !loadout.IsReady || !player.Combat.WeaponActions)
            { SendActionResult(id, false, weapon, 0, NetworkPlayerSimulation.Time, 0); return; }
            actionRequests.Enqueue(new ActionRequest { Id = id, Kind = kind, WeaponId = weapon,
                InputSequence = inputSequence, Received = Time.realtimeSinceStartupAsDouble });
        }

        private bool RequestConsumable(ConsumableUseOperation operation)
        {
            if (!clientStarted || !isLocalPlayer || !isOwned || !localGameplayInputAllowed
                || loadout == null || !loadout.IsReady || !player.IsAlive) return false;
            if (serverStarted) ReceiveConsumable(operation.RequestId, operation.ItemId.Value);
            else CmdUseConsumable(operation.RequestId, operation.ItemId.Value);
            return true;
        }
        [Command] private void CmdUseConsumable(int requestId, ulong itemId) => ReceiveConsumable(requestId, itemId);
        private void ReceiveConsumable(int requestId, ulong itemId)
        {
            var operation = new ConsumableUseOperation(requestId, new ItemId(itemId));
            void Complete(ConsumableUseResult result)
            {
                if (isLocalPlayer) player.Consumables?.ApplyResult(result);
                else if (connectionToClient != null) TargetConsumableResult(connectionToClient, result.RequestId,
                    result.Success, result.ItemId.Value, result.ActionId, result.Error);
            }
            if (!player.Combat.Consumables)
            { Complete(new ConsumableUseResult(requestId, false, operation.ItemId, 0, Player.NetworkCombatPausedMessage)); return; }
            if (!serverStarted || requestId <= 0 || requestId <= lastConsumableRequest || itemId == 0
                || loadout == null || !loadout.IsReady || !ValidateAccess(connectionToClient))
            { Complete(new ConsumableUseResult(requestId, false, operation.ItemId, 0, "角色当前不能使用物品。")); return; }
            lastConsumableRequest = requestId;
            if (!QueueOperation(() => Complete(player.Consumables.TryStart(loadout.Role, operation, NetworkPlayerSimulation.Time)),
                reason => Complete(new ConsumableUseResult(requestId, false, operation.ItemId, 0, reason))))
                Complete(new ConsumableUseResult(requestId, false, operation.ItemId, 0, "角色物品请求队列已满。"));
        }
        [TargetRpc] private void TargetConsumableResult(NetworkConnectionToClient recipient, int requestId,
            bool success, ulong itemId, ulong actionId, string error)
        {
            if (clientStarted && !serverStarted && isLocalPlayer && isOwned)
                player.Consumables?.ApplyResult(new ConsumableUseResult(requestId, success, new ItemId(itemId), actionId, error));
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

        private void OnShotResolved(WeaponShotRequest request, IReadOnlyList<WeaponHitResult> hits)
        {
            if (!player.Combat.WeaponActions) return;
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
            if (!clientStarted || !isActiveAndEnabled || serverStarted || !player.Combat.WeaponActions) return;
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
    }
}
