using System;
using System.Collections.Generic;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.Networking
{
    public struct PlayerInputPacket
    {
        public ulong Sequence;
        public long ObservedServerTick;
        public Vector2 Move, Look;
        public bool Crouch, Aim, FirePressed, FireHeld, Reload, Interact;
        public PlayerInputFrame ToFrame(bool edges = true) => new PlayerInputFrame(Move, look: edges ? Look : Vector2.zero,
            crouchHeld: Crouch, aimHeld: Aim, firePressed: edges && FirePressed,
            fireHeld: FireHeld, reloadPressed: edges && Reload, interactPressed: edges && Interact);
        public bool IsValid => Sequence > 0 && Sequence < ulong.MaxValue - 128 && ObservedServerTick >= 0
            && Finite(Move.x) && Finite(Move.y) && Finite(Look.x) && Finite(Look.y)
            && Move.sqrMagnitude <= 1.01f && Mathf.Abs(Look.x) <= 360f && Mathf.Abs(Look.y) <= 180f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>Packets never drive simulation. One server tick consumes at most one ordered input.</summary>
    internal sealed class ServerPlayerInputBuffer
    {
        internal const int Capacity = 64;
        private struct Entry { internal PlayerInputPacket Packet; internal long Received; }
        private readonly SortedDictionary<ulong, Entry> queued = new SortedDictionary<ulong, Entry>();
        private ulong next = 1;
        private long gapSince = -1, lastReceived = -1;
        private PlayerInputPacket held;
        internal ulong Acknowledged { get; private set; }
        internal long AcceptedObservedTick { get; private set; }
        internal bool TimedOut { get; private set; } = true;
        internal bool Enqueue(PlayerInputPacket packet, long serverTick, int maxAgeTicks)
        {
            if (!packet.IsValid || packet.Sequence < next
                || queued.Count >= Capacity || queued.ContainsKey(packet.Sequence)) return false;
            if (packet.ObservedServerTick > serverTick + 2) return false;
            if (packet.ObservedServerTick < AcceptedObservedTick) return false;
            if (packet.Sequence - next >= Capacity)
            {
                // Recover after a loss burst without retaining an unbounded gap or running extra ticks.
                if (queued.Count != 0 || lastReceived >= 0 && serverTick - lastReceived <= maxAgeTicks) return false;
                next = packet.Sequence;
                Acknowledged = next - 1;
                held = default;
                gapSince = -1;
            }
            queued.Add(packet.Sequence, new Entry { Packet = packet, Received = serverTick });
            return true;
        }
        internal PlayerInputFrame Consume(long tick, int timeoutTicks, int gapTicks)
        {
            // Expired packets advance the acknowledgement watermark but never execute their edges/held.
            while (queued.Count > 0)
            {
                KeyValuePair<ulong, Entry> first = First();
                if (tick - first.Value.Received <= timeoutTicks
                    && first.Value.Packet.ObservedServerTick >= AcceptedObservedTick) break;
                queued.Remove(first.Key);
                next = first.Key + 1;
                Acknowledged = first.Key;
            }
            if (!queued.TryGetValue(next, out Entry entry) && queued.Count > 0)
            {
                if (gapSince < 0) gapSince = tick;
                if (tick - gapSince >= gapTicks)
                {
                    KeyValuePair<ulong, Entry> first = First();
                    next = first.Key;
                    Acknowledged = next - 1;
                    entry = first.Value;
                }
            }
            if (queued.ContainsKey(next))
            {
                queued.Remove(next);
                held = entry.Packet;
                Acknowledged = next++;
                AcceptedObservedTick = held.ObservedServerTick;
                TimedOut = false;
                lastReceived = entry.Received;
                gapSince = -1;
                return held.ToFrame();
            }
            if (lastReceived < 0 || tick - lastReceived > timeoutTicks)
            { held = default; TimedOut = true; }
            return held.ToFrame(false);
        }
        private KeyValuePair<ulong, Entry> First()
        {
            using (var iterator = queued.GetEnumerator()) { iterator.MoveNext(); return iterator.Current; }
        }
        internal void DiscardPending()
        {
            foreach (var item in queued) { Acknowledged = item.Key; next = item.Key + 1; }
            queued.Clear(); held = default; gapSince = -1; lastReceived = -1; TimedOut = true;
        }
        internal void Clear()
        {
            queued.Clear(); held = default; next = 1; Acknowledged = 0;
            AcceptedObservedTick = 0; TimedOut = true; gapSince = lastReceived = -1;
        }
    }
}
