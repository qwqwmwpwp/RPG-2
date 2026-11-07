using System;
using System.Collections.Generic;
using RPG4.Backpack.Items;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Reversible local effects only; never owns a Weapon, ammunition, damage or simulation events.</summary>
    internal sealed class PlayerLocalActionAnticipation
    {
        private const int Capacity = 128;
        private const double ConfirmationTimeout = 1.5d;
        private sealed class Request
        {
            internal ulong Id, WeaponId;
            internal byte Kind;
            internal double Started, LastResult;
            internal bool Accepted, Rejected, Canceled;
            internal bool ObservedReload;
            internal long InitialConfirmedTick;
            internal ulong BaseShotSequence;
            internal float RenderedReloadPhase;
            internal uint NextOrdinal;
            internal WeaponDefinitionFragment Definition;
            internal readonly Dictionary<uint, Cue> Cues = new Dictionary<uint, Cue>();
            internal readonly Queue<uint> CueOrder = new Queue<uint>();
        }
        private sealed class Cue
        {
            internal Request Request;
            internal uint Ordinal;
            internal ulong Sequence, ServerSequence;
            internal double Started;
            internal bool Presented, Anticipated;
        }
        private readonly struct ShotKey : IEquatable<ShotKey>
        {
            internal readonly ulong Weapon, Sequence;
            internal ShotKey(ulong weapon, ulong sequence) { Weapon = weapon; Sequence = sequence; }
            public bool Equals(ShotKey other) => Weapon == other.Weapon && Sequence == other.Sequence;
            public override bool Equals(object obj) => obj is ShotKey other && Equals(other);
            public override int GetHashCode() => unchecked(Weapon.GetHashCode() * 397 ^ Sequence.GetHashCode());
        }
        private readonly Dictionary<ulong, Request> requests = new Dictionary<ulong, Request>();
        private readonly Queue<ulong> requestOrder = new Queue<ulong>();
        private readonly Dictionary<ShotKey, Cue> confirmedShots = new Dictionary<ShotKey, Cue>();
        private readonly Queue<ShotKey> confirmedOrder = new Queue<ShotKey>();
        private readonly Queue<Cue> pendingCues = new Queue<Cue>();
        private readonly Dictionary<ulong, ulong> presentedCueWatermarks = new Dictionary<ulong, ulong>();
        private readonly Queue<ulong> presentedWeapons = new Queue<ulong>();
        private Request fireSession, reload;
        private Cue lastShot;
        private bool held, aiming;
        private ulong nextCue, cadenceWeapon;
        private double nextShotAt;
        private PlayerNetworkState latestConfirmed;
        private double confirmedReceivedAt;
        private bool hasConfirmed;

        internal void ObserveConfirmed(PlayerNetworkState state, double now)
        { latestConfirmed = state; confirmedReceivedAt = now; hasConfirmed = true; }

        internal void UpdateInput(PlayerInputFrame input)
        {
            held = input.FireHeld;
            aiming = input.AimHeld;
            if (!held) fireSession = null;
        }

        internal void Begin(PlayerInputFrame input, ulong requestId, double now, ulong weaponId,
            WeaponDefinitionFragment definition, bool authoritativeReloading, bool knownUsableRound,
            ulong baseShotSequence)
        {
            if (requestId == 0 || weaponId == 0 || definition == null || requests.ContainsKey(requestId)) return;
            byte kind = input.ReloadPressed ? (byte)2 : input.FirePressed ? (byte)1 : (byte)0;
            if (kind == 0) return;
            if (requestOrder.Count == Capacity)
            {
                ulong expired = requestOrder.Dequeue();
                if (requests.TryGetValue(expired, out Request old)) old.Rejected = true;
                requests.Remove(expired);
            }
            var request = new Request { Id = requestId, WeaponId = weaponId, Kind = kind,
                Started = now, LastResult = now, Definition = definition, BaseShotSequence = baseShotSequence,
                InitialConfirmedTick = hasConfirmed ? latestConfirmed.Tick : 0 };
            requests.Add(requestId, request); requestOrder.Enqueue(requestId);
            aiming = input.AimHeld;
            if (kind == 2)
            {
                fireSession = null; lastShot = null;
                reload = request;
            }
            else if (!authoritativeReloading && reload == null && knownUsableRound)
            {
                held = input.FireHeld;
                fireSession = definition.FireMode == WeaponFireMode.Automatic && held ? request : null;
                if (cadenceWeapon != weaponId) { cadenceWeapon = weaponId; nextShotAt = now; }
                if (now >= nextShotAt) Emit(request, now);
            }
        }

        private void Emit(Request request, double now)
        {
            if (request.NextOrdinal == uint.MaxValue) { fireSession = null; return; }
            var cue = new Cue { Request = request, Ordinal = ++request.NextOrdinal,
                Sequence = ++nextCue, Started = now, Anticipated = true };
            if (request.CueOrder.Count == Capacity) request.Cues.Remove(request.CueOrder.Dequeue());
            request.Cues.Add(cue.Ordinal, cue); request.CueOrder.Enqueue(cue.Ordinal);
            if (pendingCues.Count == Capacity) pendingCues.Dequeue();
            pendingCues.Enqueue(cue); lastShot = cue;
            nextShotAt = now + Math.Max(0.01d, request.Definition.EffectiveShotInterval);
        }

        internal void ApplyResult(ulong requestId, bool accepted, ulong weaponId, ulong shotSequence,
            uint ordinal, double now)
        {
            if (!requests.TryGetValue(requestId, out Request request) || request.WeaponId != weaponId
                || request.Rejected) return;
            request.LastResult = now;
            if (!accepted)
            {
                request.Rejected = true;
                if (fireSession == request) fireSession = null;
                if (reload == request) reload = null;
                if (lastShot?.Request == request) lastShot = null;
                return;
            }
            request.Accepted = true;
            if (shotSequence == 0 || ordinal == 0) return;
            if (!request.Cues.TryGetValue(ordinal, out Cue cue))
            {
                // The authoritative shot can arrive before the local cadence reaches this ordinal.
                // Reserve it without enqueueing a cue, so a later render sample cannot emit it again.
                cue = new Cue { Request = request, Ordinal = ordinal, Started = now };
                if (request.CueOrder.Count == Capacity) request.Cues.Remove(request.CueOrder.Dequeue());
                request.Cues.Add(ordinal, cue); request.CueOrder.Enqueue(ordinal);
                request.NextOrdinal = Math.Max(request.NextOrdinal, ordinal);
                if (fireSession == request && cadenceWeapon == weaponId)
                    nextShotAt = Math.Max(nextShotAt, now + Math.Max(0.01d, request.Definition.EffectiveShotInterval));
            }
            cue.ServerSequence = shotSequence;
            var key = new ShotKey(weaponId, shotSequence);
            if (confirmedShots.ContainsKey(key)) return;
            if (confirmedOrder.Count == Capacity) confirmedShots.Remove(confirmedOrder.Dequeue());
            confirmedShots.Add(key, cue); confirmedOrder.Enqueue(key);
        }

        internal PlayerLocalWeaponPresentation Get(PlayerPublishedFrame confirmed, PlayerStateData data,
            double now, ulong equippedWeapon, bool knownUsableRound)
        {
            if (confirmed == null || equippedWeapon == 0) { Cancel(); return default; }
            bool confirmedReload = hasConfirmed && latestConfirmed.WeaponId == equippedWeapon
                && reload != null && latestConfirmed.Tick > reload.InitialConfirmedTick && latestConfirmed.Reloading;
            if (confirmedReload)
            {
                reload.ObservedReload = true;
                reload.LastResult = Math.Max(reload.LastResult, confirmedReceivedAt);
            }
            else if (reload != null && reload.ObservedReload && hasConfirmed
                && latestConfirmed.Tick > reload.InitialConfirmedTick && !latestConfirmed.Reloading)
                reload = null;
            bool Expired(Request request) => request == null || request.Rejected || request.Canceled
                || request.WeaponId != equippedWeapon || now - request.LastResult > ConfirmationTimeout;
            if (Expired(fireSession)) fireSession = null;
            if (Expired(reload)) reload = null;
            if (lastShot != null && Expired(lastShot.Request)) lastShot = null;
            if (fireSession != null && held && reload == null && !confirmed.Reloading && knownUsableRound && now >= nextShotAt)
                Emit(fireSession, now); // At most one cue per render sample; never catch up a burst after a stall.

            bool localReload = reload != null;
            if (localReload && reload.Accepted && !confirmed.Reloading
                && now - reload.Started >= reload.Definition.ReloadDuration) reload = null;
            localReload = reload != null;
            float reloadPhase = confirmed.ReloadPhase;
            if (localReload)
            {
                double duration = Math.Max(0.01d, reload.Definition.ReloadDuration);
                float phase = confirmedReload ? latestConfirmed.ReloadPhase + (float)((now - confirmedReceivedAt) / duration)
                    : (float)((now - reload.Started) / duration);
                // Confirmation takes over the clock, but never rewinds an already shown reload gesture.
                reload.RenderedReloadPhase = Mathf.Clamp01(Mathf.Max(reload.RenderedReloadPhase, phase));
                reloadPhase = reload.RenderedReloadPhase;
            }

            Cue shot = lastShot;
            if (shot != null && presentedCueWatermarks.TryGetValue(equippedWeapon, out ulong newestCue)
                && shot.Sequence < newestCue) shot = null;
            WeaponDefinitionFragment definition = localReload ? reload.Definition : shot?.Request.Definition;
            double elapsed = shot != null ? Math.Max(0d, now - shot.Started) : double.PositiveInfinity;
            bool shotActive = shot != null && elapsed < definition.ShotDuration;
            bool fading = shot != null && !shotActive && elapsed < definition.ShotDuration + definition.FadeOutDuration;
            // A confirmed continuation keeps the same visual identity; acceptance never restarts the animation.
            bool continuation = shot != null && confirmed.WeaponId == equippedWeapon
                && confirmedShots.TryGetValue(new ShotKey(equippedWeapon, confirmed.ShotSequence), out Cue mapped)
                && ReferenceEquals(mapped, shot) && (confirmed.WeaponAction.ShotActive || confirmed.WeaponAction.FadingOut);
            if (!localReload && !shotActive && !fading && !continuation) return default;
            bool automatic = shot != null && shot.Request.Definition.FireMode == WeaponFireMode.Automatic;
            WeaponActionSnapshot action = continuation && !shotActive && !fading ? confirmed.WeaponAction
                : new WeaponActionSnapshot(false, false, shotActive && !localReload, fading && !localReload,
                    automatic && !localReload, shotActive ? Mathf.Clamp01((float)(elapsed / definition.ShotDuration)) : 0f);
            bool reloading = localReload || confirmed.Reloading;
            UpperBodyDemand demand = PlayerMovementDemand.Compose(data, true, aiming, reloading,
                action.ShotActive, action.FadingOut, fireSession != null && held);
            return new PlayerLocalWeaponPresentation(true, equippedWeapon, shot?.Sequence ?? 0,
                localReload ? reload.Id : shot.Request.Id, shot?.Ordinal ?? 0, action, demand, reloading,
                reloadPhase, definition.ShotDuration, definition.FadeOutDuration,
                definition.ReloadDuration, demand.AimCamera);
        }

        internal bool TryDequeue(double now, out PlayerAnticipatedShot shot)
        {
            while (pendingCues.Count > 0)
            {
                Cue cue = pendingCues.Dequeue();
                if (cue.Request.Rejected || now - cue.Started > 0.25d) continue;
                shot = new PlayerAnticipatedShot(cue.Request.Id, cue.Ordinal, cue.Request.WeaponId, cue.Sequence);
                return true;
            }
            shot = default; return false;
        }
        internal void MarkPresented(PlayerAnticipatedShot shot)
        {
            if (requests.TryGetValue(shot.RequestId, out Request request) && request.WeaponId == shot.WeaponId
                && request.Cues.TryGetValue(shot.Ordinal, out Cue cue) && cue.Sequence == shot.CueSequence)
            {
                cue.Presented = true;
                if (!presentedCueWatermarks.TryGetValue(shot.WeaponId, out ulong latest))
                {
                    if (presentedWeapons.Count == Capacity) presentedCueWatermarks.Remove(presentedWeapons.Dequeue());
                    presentedWeapons.Enqueue(shot.WeaponId);
                }
                presentedCueWatermarks[shot.WeaponId] = Math.Max(latest, cue.Sequence);
            }
        }
        internal bool ShouldPresentConfirmed(ulong weaponId, ulong sequence)
            => !confirmedShots.TryGetValue(new ShotKey(weaponId, sequence), out Cue cue) || !cue.Presented;
        internal bool TryGetPresentationSequence(ulong weaponId, ulong sequence, out ulong cueSequence)
        {
            if (confirmedShots.TryGetValue(new ShotKey(weaponId, sequence), out Cue cue) && cue.Anticipated)
            { cueSequence = cue.Sequence; return true; }
            cueSequence = 0; return false;
        }
        internal bool ShouldDeferConfirmedShot(ulong weaponId, ulong sequence, double now)
        {
            if (weaponId == 0 || sequence == 0) return false;
            if (confirmedShots.TryGetValue(new ShotKey(weaponId, sequence), out Cue mapped))
                return mapped.Anticipated && presentedCueWatermarks.TryGetValue(weaponId, out ulong latest)
                    && mapped.Sequence < latest;
            foreach (Request request in requests.Values)
            {
                if (request.Kind != 1 || request.WeaponId != weaponId || request.Rejected || request.Canceled
                    || now - request.LastResult > ConfirmationTimeout) continue;
                foreach (Cue cue in request.Cues.Values)
                    if (cue.Anticipated && cue.ServerSequence == 0 && now - cue.Started <= ConfirmationTimeout)
                        return true;
            }
            return false;
        }
        internal Vector2 GetVisualRecoil(PlayerPublishedFrame confirmed, double now)
        {
            Cue cue = lastShot;
            if (cue == null || cue.Request.Rejected || !cue.Anticipated || confirmed == null) return Vector2.zero;
            // Exact correlation retires this cue's recoil. Until correlation arrives, any newer
            // authoritative shot retires the speculative kick conservatively; no sequence is guessed.
            if (confirmed.WeaponId == cue.Request.WeaponId && (cue.ServerSequence != 0
                ? confirmed.ShotSequence >= cue.ServerSequence : confirmed.ShotSequence > cue.Request.BaseShotSequence))
                return Vector2.zero;
            double elapsed = Math.Max(0d, now - cue.Started);
            if (elapsed > 0.12d) return Vector2.zero;
            float weight = Mathf.Exp(-(float)elapsed / 0.04f);
            return new Vector2(cue.Request.Definition.RecoilYaw, -cue.Request.Definition.RecoilPitch) * weight;
        }
        internal void Cancel()
        {
            foreach (Request request in requests.Values) request.Canceled = true;
            fireSession = reload = null; lastShot = null;
            held = aiming = false; pendingCues.Clear();
        }
        internal void Reset()
        {
            Cancel(); requests.Clear(); requestOrder.Clear(); confirmedShots.Clear(); confirmedOrder.Clear();
            presentedCueWatermarks.Clear(); presentedWeapons.Clear();
            cadenceWeapon = 0; nextShotAt = 0d;
            latestConfirmed = default; hasConfirmed = false; confirmedReceivedAt = 0d;
        }
    }
}
