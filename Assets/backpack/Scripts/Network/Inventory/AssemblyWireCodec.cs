using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    /// <summary>Bounded presentation DTO. Never serializes runtime instances or Unity objects.</summary>
    public static class AssemblyWireCodec
    {
        public const int MaxBytes = 262144, MaxStringLength = 64, MaxSlots = 1024, MaxCandidates = 64;
        public const int ChunkBytes = 16384, MaxChunks = MaxBytes / ChunkBytes;
        private static void Text(BinaryWriter writer, string value, int limit = 512)
        {
            value = value ?? string.Empty;
            if (value.Length > limit) throw new InvalidDataException("Assembly text exceeds its limit.");
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        private static string Text(BinaryReader reader, int limit = 512)
        {
            int count = reader.ReadUInt16();
            if (count > limit * 4 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException();
            string value = new UTF8Encoding(false, true).GetString(reader.ReadBytes(count));
            if (value.Length > limit) throw new InvalidDataException();
            return value.Length == 0 ? null : value;
        }
        private static void Item(BinaryWriter writer, WeaponUiItem item)
        {
            writer.Write(item != null);
            if (item == null) return;
            writer.Write(item.ItemId.Value); Text(writer, item.Name); writer.Write(item.Count);
        }
        private static WeaponUiItem Item(BinaryReader reader, Func<ItemId, WeaponUiItem> resolve)
        {
            if (!reader.ReadBoolean()) return null;
            var id = new ItemId(reader.ReadUInt64()); string name = Text(reader); int count = reader.ReadInt32();
            if (!id.IsValid || count < 1) throw new InvalidDataException();
            return new WeaponUiItem(id, name, resolve?.Invoke(id)?.Icon, count);
        }
        public static byte[] Encode(AssemblyInspectionSnapshot value)
        {
            if (value == null || value.Slots.Count > MaxSlots || value.Candidates.Count > MaxCandidates) throw new InvalidDataException();
            return Write(writer => {
                writer.Write((byte)1); Item(writer, value.RootItem);
                writer.Write(value.RootItemId.Value); writer.Write(value.ParentItemId.Value); Text(writer, value.SlotId, MaxStringLength);
                writer.Write(value.SessionId); writer.Write(value.RequestId); writer.Write(value.StructureRevision);
                writer.Write(value.CandidateOffset); writer.Write(value.TotalCandidateCount); writer.Write(value.NextCandidateOffset);
                Text(writer, value.CurrentSummary); Text(writer, value.PreviewSummary); Text(writer, value.OperationBlockReason);
                writer.Write((ushort)value.Slots.Count);
                foreach (AssemblySlotSnapshot slot in value.Slots) {
                    writer.Write(slot.ParentItemId.Value); Text(writer, slot.SlotId, MaxStringLength); Text(writer, slot.DisplayName);
                    writer.Write(slot.Required); writer.Write((byte)slot.Depth); Item(writer, slot.InstalledItem);
                    writer.Write(slot.CanRemove); Text(writer, slot.Reason);
                }
                writer.Write((byte)value.Candidates.Count);
                foreach (AssemblyCandidateSnapshot candidate in value.Candidates) {
                    Item(writer, candidate.Item); writer.Write(candidate.CanApply); Text(writer, candidate.Reason);
                    writer.Write(candidate.Dimensions.X); writer.Write(candidate.Dimensions.Y); Text(writer, candidate.CombatSummary);
                    writer.Write(candidate.ConflictItemId.Value); Text(writer, candidate.ConflictSlotId, MaxStringLength);
                }
            });
        }
        public static AssemblyInspectionSnapshot DecodeInspection(byte[] bytes, Func<ItemId, WeaponUiItem> resolve = null)
        {
            return Read(bytes, reader => {
                if (reader.ReadByte() != 1) throw new InvalidDataException();
                WeaponUiItem root = Item(reader, resolve); var rootId = new ItemId(reader.ReadUInt64());
                var parentId = new ItemId(reader.ReadUInt64()); string slotId = Text(reader, MaxStringLength);
                int session = reader.ReadInt32(), request = reader.ReadInt32(), revision = reader.ReadInt32();
                int offset = reader.ReadInt32(), total = reader.ReadInt32(), next = reader.ReadInt32();
                if (offset < 0 || total < 0 || next < -1) throw new InvalidDataException();
                string current = Text(reader), preview = Text(reader), reason = Text(reader);
                int count = reader.ReadUInt16(); if (count > MaxSlots) throw new InvalidDataException();
                var slots = new List<AssemblySlotSnapshot>(count);
                for (int i = 0; i < count; i++) {
                    var parent = new ItemId(reader.ReadUInt64()); string slot = Text(reader, MaxStringLength), name = Text(reader);
                    bool required = reader.ReadBoolean(); int depth = reader.ReadByte();
                    if (!parent.IsValid || string.IsNullOrEmpty(slot) || depth > 4) throw new InvalidDataException();
                    WeaponUiItem item = Item(reader, resolve); bool canRemove = reader.ReadBoolean();
                    slots.Add(new AssemblySlotSnapshot(parent, slot, name, required, depth, item, canRemove, Text(reader)));
                }
                count = reader.ReadByte(); if (count > MaxCandidates) throw new InvalidDataException();
                var candidates = new List<AssemblyCandidateSnapshot>(count);
                for (int i = 0; i < count; i++) {
                    WeaponUiItem item = Item(reader, resolve); bool apply = reader.ReadBoolean(); string rejection = Text(reader);
                    int x = reader.ReadInt32(), y = reader.ReadInt32();
                    // Even a disabled candidate can be larger than this inventory; dimensions are display metadata here.
                    if (item == null || x < 1 || y < 1) throw new InvalidDataException();
                    string summary = Text(reader); var conflict = new ItemId(reader.ReadUInt64()); string conflictSlot = Text(reader, MaxStringLength);
                    candidates.Add(new AssemblyCandidateSnapshot(item, apply, rejection, new GridCoord(x, y), summary, conflict, conflictSlot));
                }
                return new AssemblyInspectionSnapshot(root, slots, candidates, current, preview, reason, revision,
                    session, request, rootId, parentId, slotId, offset, total, next);
            });
        }
        public static byte[] Encode(AssemblyOperationResult result) => Write(writer => {
            writer.Write((byte)1); writer.Write(result.RequestId); writer.Write(result.SessionId);
            writer.Write(result.RootItemId.Value); writer.Write(result.ParentItemId.Value); Text(writer, result.SlotId, MaxStringLength);
            writer.Write(result.Success); Text(writer, result.Error); writer.Write(result.StructureRevision);
            writer.Write(result.ConflictItemId.Value); Text(writer, result.ConflictSlotId, MaxStringLength);
        });
        public static AssemblyOperationResult DecodeResult(byte[] bytes) => Read(bytes, reader => {
            if (reader.ReadByte() != 1) throw new InvalidDataException();
            int request = reader.ReadInt32(), session = reader.ReadInt32();
            var root = new ItemId(reader.ReadUInt64()); var parent = new ItemId(reader.ReadUInt64()); string slot = Text(reader, MaxStringLength);
            bool success = reader.ReadBoolean(); string error = Text(reader); int revision = reader.ReadInt32();
            var conflict = new ItemId(reader.ReadUInt64()); string conflictSlot = Text(reader, MaxStringLength);
            return new AssemblyOperationResult(request, session, root, parent, slot, success, error, revision, conflict, conflictSlot);
        });
        private static byte[] Write(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
                write(writer); if (stream.Length > MaxBytes) throw new InvalidDataException(); return stream.ToArray();
            }
        }
        private static T Read<T>(byte[] bytes, Func<BinaryReader, T> read)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new InvalidDataException();
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) {
                T value = read(reader); if (stream.Position != stream.Length) throw new InvalidDataException(); return value;
            }
        }
    }
    /// <summary>One bounded response, discarded by its owner on new request/timeout.</summary>
    public sealed class AssemblyWireBuffer
    {
        private readonly byte[][] chunks;
        private int received, total;
        public double Deadline { get; }
        public AssemblyWireBuffer(int count, double now)
        {
            if (count < 1 || count > AssemblyWireCodec.MaxChunks) throw new InvalidDataException();
            chunks = new byte[count][]; Deadline = now + 10d;
        }
        public bool Add(int index, int count, byte[] payload, double now, out byte[] bytes)
        {
            bytes = null;
            if (now > Deadline || count != chunks.Length || index < 0 || index >= count || payload == null
                || payload.Length < 1 || payload.Length > AssemblyWireCodec.ChunkBytes) throw new InvalidDataException();
            if (chunks[index] != null) {
                if (chunks[index].Length != payload.Length) throw new InvalidDataException();
                for (int i = 0; i < payload.Length; i++) if (chunks[index][i] != payload[i]) throw new InvalidDataException();
                return false;
            }
            total = checked(total + payload.Length); if (total > AssemblyWireCodec.MaxBytes) throw new InvalidDataException();
            chunks[index] = payload; received++;
            if (received != chunks.Length) return false;
            bytes = new byte[total]; int offset = 0;
            foreach (byte[] chunk in chunks) { Buffer.BlockCopy(chunk, 0, bytes, offset, chunk.Length); offset += chunk.Length; }
            return true;
        }
    }

}
