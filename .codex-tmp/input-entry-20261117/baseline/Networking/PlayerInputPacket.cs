using System;
using System.Collections.Generic;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.Networking
{
    public struct PlayerInputPacket
    {
        public ulong Sequence;
        public ulong FireRequestId;
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

    /// <summary>Packets never drive simulation. Queued samples collapse into one fixed-step input.</summary>
    internal sealed class ServerPlayerInputBuffer
    {
        internal const int Capacity = 64;
        private struct Entry { internal PlayerInputPacket Packet; internal long Received; }
        private readonly SortedDictionary<ulong, Entry> queued = new SortedDictionary<ulong, Entry>();
        private readonly Dictionary<ulong, long> consumedReceipts = new Dictionary<ulong, long>();
        private readonly Queue<ulong> consumedOrder = new Queue<ulong>();
        private ulong next = 1;
        private long gapSince = -1, lastReceived = -1;
        private PlayerInputPacket held;
        internal ulong Acknowledged { get; private set; }
        internal long AcceptedObservedTick { get; private set; }
        internal bool TimedOut { get; private set; } = true;
        internal ulong FireRequestId => held.FireRequestId;
        internal bool TryGetConsumedInputReceipt(ulong sequence, out long receivedTick)
            => consumedReceipts.TryGetValue(sequence, out receivedTick);
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
            // Do not spend one gap wait per obsolete segment after a loss burst.
            while (queued.Count > 0)
            {
                KeyValuePair<ulong, Entry> first = First();
                if (tick - first.Value.Received <= timeoutTicks
                    && first.Value.Packet.ObservedServerTick >= AcceptedObservedTick) break;
                queued.Remove(first.Key);
                next = first.Key + 1;
                Acknowledged = first.Key;
                gapSince = -1;
            }
            if (!queued.ContainsKey(next) && queued.Count > 0)
            {
                if (gapSince < 0) gapSince = tick;
                if (tick - gapSince >= gapTicks)
                {
                    KeyValuePair<ulong, Entry> first = First();
                    next = first.Key;
                    Acknowledged = next - 1;
                }
            }
            Vector2 look = Vector2.zero;
            bool interact = false, consumed = false;
            // Newest held values win; every received look delta is applied once. Never run
            // extra simulation steps to drain a backlog. Weapon edges use the reliable action stream.
            while (queued.TryGetValue(next, out Entry entry))
            {
                queued.Remove(next);
                Acknowledged = next++;
                // Ack also retires expired samples; it never certifies that their actions ran.
                if (tick - entry.Received > timeoutTicks || entry.Packet.ObservedServerTick < AcceptedObservedTick)
                    continue;
                held = entry.Packet;
                if (consumedOrder.Count == Capacity) consumedReceipts.Remove(consumedOrder.Dequeue());
                consumedOrder.Enqueue(held.Sequence);
                consumedReceipts[held.Sequence] = entry.Received;
                look += held.Look;
                interact |= held.Interact;
                consumed = true;
                AcceptedObservedTick = held.ObservedServerTick;
                TimedOut = false;
                lastReceived = entry.Received;
                gapSince = -1;
            }
            if (lastReceived < 0 || tick - lastReceived > timeoutTicks)
            { held = default; TimedOut = true; }
            if (consumed)
                return new PlayerInputFrame(held.Move, look: look, crouchHeld: held.Crouch,
                    aimHeld: held.Aim, fireHeld: held.FireHeld, interactPressed: interact);
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
            consumedReceipts.Clear(); consumedOrder.Clear();
        }
        internal void Clear()
        {
            queued.Clear(); held = default; next = 1; Acknowledged = 0;
            consumedReceipts.Clear(); consumedOrder.Clear();
            AcceptedObservedTick = 0; TimedOut = true; gapSince = lastReceived = -1;
        }
    }
}
