using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Mirror;
using RPG4.Backpack.Inventory;
using Operation = Mirror.SyncList<RPG4.Backpack.Network.NetworkInventoryEntry>.Operation;

namespace RPG4.Backpack.Network
{
    /// <summary>
    /// 同一有序流承载库存结构与运行差量。完整基线带两部分；稳定结构的 SET 只发运行载荷。
    /// 与 Mirror SyncList 一样记录 changesAhead，避免晚加入重复应用已包含在基线中的操作。
    /// </summary>
    internal sealed class InventorySyncList : SyncObject, IList<NetworkInventoryEntry>, IReadOnlyList<NetworkInventoryEntry>
    {
        private readonly List<NetworkInventoryEntry> objects = new List<NetworkInventoryEntry>();
        private readonly List<Change> changes = new List<Change>();
        private readonly List<Change> received = new List<Change>();
        private readonly Dictionary<int, int> pendingSets = new Dictionary<int, int>();
        private uint changesAhead;
        // 已写入任一基线/差量的前缀不可再合并；晚加入者按操作数跳过该前缀。
        private int exposedChanges;
        private bool suppressAutomaticSerialization;
        // 共享容器的原始 Mirror 流只维护权威投影；获授权内容由独立会话流发送。
        internal bool SuppressAutomaticSerialization
        {
            get => suppressAutomaticSerialization;
            set
            {
                if (suppressAutomaticSerialization == value) return;
                suppressAutomaticSerialization = value;
                ClearChanges();
            }
        }
        internal int PendingChangeCount => changes.Count;
        public Action<Operation, int, NetworkInventoryEntry, NetworkInventoryEntry> Callback;
        public int Count => objects.Count;
        public bool IsReadOnly => !IsWritable();
        public long StructuralEntrySendCount { get; private set; }
        public long RuntimeDeltaSendCount { get; private set; }

        private struct Change
        {
            internal Operation Operation;
            internal int Index;
            internal NetworkInventoryEntry Previous, Current;
            internal NetworkInventoryEntry[] Cleared;
        }

        public NetworkInventoryEntry this[int index]
        {
            get => objects[index];
            set
            {
                RequireWritable();
                NetworkInventoryEntry previous = objects[index];
                if (previous.StateEquals(value)) return;
                objects[index] = value;
                Record(new Change { Operation = Operation.OP_SET, Index = index, Previous = previous, Current = value });
            }
        }

        public void Add(NetworkInventoryEntry item)
        {
            RequireWritable();
            int index = objects.Count;
            objects.Add(item);
            Record(new Change { Operation = Operation.OP_ADD, Index = index, Current = item });
        }

        public void Insert(int index, NetworkInventoryEntry item)
        {
            RequireWritable();
            objects.Insert(index, item);
            Record(new Change { Operation = Operation.OP_INSERT, Index = index, Current = item });
        }

        public void RemoveAt(int index)
        {
            RequireWritable();
            NetworkInventoryEntry previous = objects[index];
            objects.RemoveAt(index);
            Record(new Change { Operation = Operation.OP_REMOVEAT, Index = index, Previous = previous });
        }

        public bool Remove(NetworkInventoryEntry item)
        {
            int index = objects.IndexOf(item);
            if (index < 0) return false;
            RemoveAt(index);
            return true;
        }

        public void Clear()
        {
            RequireWritable();
            objects.Clear();
            Record(new Change { Operation = Operation.OP_CLEAR });
        }

        public bool Contains(NetworkInventoryEntry item) => objects.Contains(item);
        public int IndexOf(NetworkInventoryEntry item) => objects.IndexOf(item);
        public void CopyTo(NetworkInventoryEntry[] array, int arrayIndex) => objects.CopyTo(array, arrayIndex);
        public IEnumerator<NetworkInventoryEntry> GetEnumerator() => objects.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void RequireWritable()
        {
            if (IsReadOnly) throw new InvalidOperationException("只有权威端可以修改库存同步流。");
        }

        private void Record(Change change)
        {
            if (!suppressAutomaticSerialization && IsRecording())
            {
                bool merged = false;
                if (change.Operation == Operation.OP_SET && pendingSets.TryGetValue(change.Index, out int position)
                    && position >= exposedChanges)
                {
                    Change previous = changes[position];
                    previous.Current = change.Current;
                    changes[position] = previous;
                    merged = true;
                }
                if (!merged)
                {
                    if (change.Operation == Operation.OP_SET) pendingSets[change.Index] = changes.Count;
                    else pendingSets.Clear(); // 任一结构操作都是索引与合并屏障。
                    changes.Add(change);
                }
                OnDirty?.Invoke();
            }
            Notify(change);
        }

        private void Notify(Change change) => Callback?.Invoke(change.Operation, change.Index, change.Previous, change.Current);

        public override void ClearChanges() { changes.Clear(); pendingSets.Clear(); exposedChanges = 0; }
        public void ResetStatistics() { StructuralEntrySendCount = RuntimeDeltaSendCount = 0; }

        public override void Reset()
        {
            objects.Clear();
            changes.Clear();
            received.Clear();
            pendingSets.Clear();
            changesAhead = 0;
            exposedChanges = 0;
            suppressAutomaticSerialization = false;
        }

        public override void OnSerializeAll(NetworkWriter writer)
        {
            if (suppressAutomaticSerialization)
            {
                writer.WriteUInt(0); // 空条目基线。
                writer.WriteUInt(0); // changesAhead。
                return;
            }
            exposedChanges = changes.Count;
            pendingSets.Clear();
            WriteSnapshot(writer, (uint)changes.Count);
        }

        // 定向版本批不重放基线之前的操作，因此不携带 Mirror changesAhead。
        internal void WriteSnapshot(NetworkWriter writer) => WriteSnapshot(writer, 0);

        private void WriteSnapshot(NetworkWriter writer, uint ahead)
        {
            writer.WriteUInt((uint)objects.Count);
            foreach (NetworkInventoryEntry entry in objects) WriteFull(writer, entry);
            StructuralEntrySendCount += objects.Count;
            writer.WriteUInt(ahead);
        }

        public override void OnSerializeDelta(NetworkWriter writer)
        {
            if (suppressAutomaticSerialization) { writer.WriteUInt(0); return; }
            exposedChanges = changes.Count;
            pendingSets.Clear();
            writer.WriteUInt((uint)changes.Count);
            foreach (Change change in changes)
            {
                writer.WriteByte((byte)change.Operation);
                if (change.Operation == Operation.OP_CLEAR) continue;
                if (change.Operation != Operation.OP_ADD) writer.WriteInt(change.Index);
                if (change.Operation == Operation.OP_REMOVEAT) continue;
                if (change.Operation == Operation.OP_SET)
                {
                    bool runtimeOnly = CanWriteRuntimeOnly(change.Previous, change.Current);
                    writer.WriteBool(runtimeOnly);
                    if (runtimeOnly)
                    {
                        writer.WriteULong(change.Current.itemId);
                        writer.WriteULong(change.Current.structureVersion);
                        writer.WriteULong(change.Previous.runtimeVersion);
                        writer.WriteULong(change.Current.runtimeVersion);
                        writer.WriteBytesAndSize(change.Current.runtimeState);
                        RuntimeDeltaSendCount++;
                        continue;
                    }
                }
                WriteFull(writer, change.Current);
                StructuralEntrySendCount++;
            }
        }

        private static bool CanWriteRuntimeOnly(NetworkInventoryEntry previous, NetworkInventoryEntry current)
            => current.structureVersion != 0 && current.runtimeState != null
                && previous.StructureStateEquals(current) && previous.upperLeftIndex == current.upperLeftIndex
                && previous.width == current.width && previous.height == current.height;

        public override void OnDeserializeAll(NetworkReader reader)
        {
            uint count = reader.ReadUInt();
            if (count > InventoryContainer.MaxSlotCount) throw new InvalidDataException("库存基线条目数超限。");
            var baseline = new List<NetworkInventoryEntry>((int)count);
            for (uint i = 0; i < count; i++) baseline.Add(ReadFull(reader));
            uint ahead = reader.ReadUInt();
            objects.Clear();
            objects.AddRange(baseline);
            changes.Clear();
            pendingSets.Clear();
            exposedChanges = 0;
            changesAhead = ahead;
        }

        public override void OnDeserializeDelta(NetworkReader reader)
        {
            uint count = reader.ReadUInt();
            if (count > reader.Remaining) throw new InvalidDataException("库存增量操作数非法。");
            uint ahead = changesAhead;
            received.Clear();
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    Operation op = (Operation)reader.ReadByte();
                    if (op < Operation.OP_ADD || op > Operation.OP_CLEAR) throw new InvalidDataException("库存操作类型非法。");
                    bool apply = ahead == 0;
                    var change = new Change { Operation = op };
                    if (op != Operation.OP_ADD && op != Operation.OP_CLEAR) change.Index = reader.ReadInt();
                    if (op == Operation.OP_ADD || op == Operation.OP_INSERT || op == Operation.OP_SET)
                    {
                        bool runtimeOnly = op == Operation.OP_SET && reader.ReadBool();
                        if (runtimeOnly)
                        {
                            ulong id = reader.ReadULong();
                            ulong structure = reader.ReadULong();
                            ulong previousRuntime = reader.ReadULong();
                            ulong runtime = reader.ReadULong();
                            byte[] state = reader.ReadBytesAndSize();
                            if (apply)
                            {
                                NetworkInventoryEntry previous = objects[change.Index];
                                if (id != previous.itemId || structure == 0 || structure != previous.structureVersion
                                    || previousRuntime != previous.runtimeVersion || runtime == 0)
                                    throw new InvalidDataException("运行增量与当前物品结构/运行基线版本不匹配。");
                                change.Current = previous;
                                change.Current.runtimeVersion = runtime;
                                change.Current.runtimeState = state;
                            }
                        }
                        else change.Current = ReadFull(reader);
                    }
                    if (!apply) { ahead--; continue; }
                    ApplyReceived(ref change);
                    received.Add(change);
                }
            }
            catch
            {
                // 无回调地回滚已读取操作，不让错误包留下可被强制恢复读取的半批基线。
                for (int i = received.Count - 1; i >= 0; i--) Undo(received[i]);
                received.Clear();
                throw;
            }
            changesAhead = ahead;
            foreach (Change change in received) Notify(change);
            received.Clear();
        }

        private void ApplyReceived(ref Change change)
        {
            switch (change.Operation)
            {
                case Operation.OP_ADD:
                    change.Index = objects.Count;
                    objects.Add(change.Current);
                    break;
                case Operation.OP_INSERT:
                    objects.Insert(change.Index, change.Current);
                    break;
                case Operation.OP_SET:
                    change.Previous = objects[change.Index];
                    objects[change.Index] = change.Current;
                    break;
                case Operation.OP_REMOVEAT:
                    change.Previous = objects[change.Index];
                    objects.RemoveAt(change.Index);
                    break;
                case Operation.OP_CLEAR:
                    change.Cleared = objects.ToArray();
                    objects.Clear();
                    break;
            }
        }

        private void Undo(Change change)
        {
            switch (change.Operation)
            {
                case Operation.OP_ADD:
                case Operation.OP_INSERT: objects.RemoveAt(change.Index); break;
                case Operation.OP_SET: objects[change.Index] = change.Previous; break;
                case Operation.OP_REMOVEAT: objects.Insert(change.Index, change.Previous); break;
                case Operation.OP_CLEAR: objects.AddRange(change.Cleared); break;
            }
        }

        private static void WriteFull(NetworkWriter writer, NetworkInventoryEntry entry)
        {
            writer.WriteULong(entry.itemId);
            writer.WriteString(entry.itemType);
            writer.WriteInt(entry.stackCount);
            writer.WriteBytesAndSize(entry.fragmentState);
            writer.WriteInt(entry.upperLeftIndex);
            writer.WriteInt(entry.width);
            writer.WriteInt(entry.height);
            writer.WriteULong(entry.structureVersion);
            writer.WriteULong(entry.runtimeVersion);
            writer.WriteBytesAndSize(entry.runtimeState);
        }

        private static NetworkInventoryEntry ReadFull(NetworkReader reader) => new NetworkInventoryEntry
        {
            itemId = reader.ReadULong(), itemType = reader.ReadString(), stackCount = reader.ReadInt(),
            fragmentState = reader.ReadBytesAndSize(), upperLeftIndex = reader.ReadInt(),
            width = reader.ReadInt(), height = reader.ReadInt(), structureVersion = reader.ReadULong(),
            runtimeVersion = reader.ReadULong(), runtimeState = reader.ReadBytesAndSize()
        };
    }
}
