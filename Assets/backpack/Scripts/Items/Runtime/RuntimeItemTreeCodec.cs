using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    public static partial class RuntimeItemSnapshotCodec
    {
        public static bool TryCapture(RuntimeItem item, out RuntimeItemSnapshot snapshot, out string error)
        {
            int count = 0;
            return TryCaptureTree(item, 0, new HashSet<RuntimeItem>(), new HashSet<ulong>(), ref count, out snapshot, out error);
        }

        private static bool TryCaptureTree(RuntimeItem item, int depth, HashSet<RuntimeItem> seen,
            HashSet<ulong> ids, ref int count, out RuntimeItemSnapshot snapshot, out string error)
        {
            snapshot = default;
            if (item == null || depth > RuntimeItemSnapshot.MaxDepth || ++count > RuntimeItemSnapshot.MaxTreeItems
                || !seen.Add(item) || (item.ItemId.IsValid && !ids.Add(item.ItemId.Value)) || (depth > 0 && !item.ItemId.IsValid))
            { error = "物品树超限、包含循环或重复/无效子物品标识。"; return false; }
            if (!TryCaptureNumbers(item, out RuntimeItemSnapshot basic, out error)) return false;
            WeaponStateSnapshot weapon = null;
            MagazineStateSnapshot magazine = item.MagazineState == null ? null : new MagazineStateSnapshot(item.MagazineState.Rounds);
            if (item.WeaponState != null)
            {
                WeaponItemState state = item.WeaponState;
                RuntimeItemSnapshot? magazineItem = null;
                if (state.Magazine != null)
                {
                    if (!TryCaptureTree(state.Magazine, depth + 1, seen, ids, ref count, out RuntimeItemSnapshot child, out error)) return false;
                    magazineItem = child;
                }
                var attachments = new List<AttachmentStateSnapshot>();
                foreach (KeyValuePair<WeaponAttachmentSlot, RuntimeItem> pair in state.Attachments)
                {
                    if (!TryCaptureTree(pair.Value, depth + 1, seen, ids, ref count, out RuntimeItemSnapshot child, out error)) return false;
                    attachments.Add(new AttachmentStateSnapshot(pair.Key, child));
                }
                weapon = new WeaponStateSnapshot(state.Durability, state.ChamberAmmo, state.ShotSequence, magazineItem, attachments);
            }
            snapshot = new RuntimeItemSnapshot(basic.ItemId, basic.ItemType, basic.StackCount, CopyNumbers(basic.Numbers), weapon, magazine);
            return true;
        }

        internal static FragmentNumberState[] CopyNumbers(IReadOnlyList<FragmentNumberState> numbers)
        {
            var copy = new FragmentNumberState[numbers.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = numbers[i];
            return copy;
        }

        public static bool TryEncodeSnapshot(RuntimeItemSnapshot snapshot, out byte[] payload, out string error)
        {
            payload = null;
            if (snapshot.Version == 1) return TryEncodeNumbers(snapshot.Numbers, out payload, out error);
            if (snapshot.Version != RuntimeItemSnapshot.CurrentVersion)
            { error = "不支持的物品状态版本。"; return false; }
            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(RuntimeItemSnapshot.CurrentVersion);
                    int count = 0;
                    WriteItem(writer, snapshot, 0, new HashSet<ulong>(), ref count);
                    if (stream.Length > MaxPayloadBytes) throw new InvalidDataException("物品状态载荷超过上限。");
                    payload = stream.ToArray();
                }
                error = null; return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is IOException)
            { error = exception.Message; return false; }
        }

        public static bool TryDecodeSnapshot(ItemId id, BackpackTag type, int stackCount, byte[] payload,
            out RuntimeItemSnapshot snapshot, out string error)
        {
            snapshot = default;
            if (payload == null || payload.Length < HeaderBytes || payload.Length > MaxPayloadBytes)
            { error = "物品状态载荷长度非法。"; return false; }
            if (payload[0] == 1)
            {
                if (stackCount < 1 || !type.IsValid || type.Name.Length > RuntimeItemSnapshot.MaxItemTypeLength)
                { error = "旧快照数量或物品类型非法。"; return false; }
                if (!TryDecodeNumbers(payload, out FragmentNumberState[] numbers, out error)) return false;
                snapshot = new RuntimeItemSnapshot(id, type, stackCount, numbers); return true;
            }
            try
            {
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (reader.ReadByte() != RuntimeItemSnapshot.CurrentVersion) throw new InvalidDataException("不支持的物品状态版本。");
                    int count = 0;
                    RuntimeItemSnapshot decoded = ReadItem(reader, 0, new HashSet<ulong>(), ref count);
                    if (stream.Position != stream.Length || decoded.ItemId != id || !decoded.ItemType.Equals(type) || decoded.StackCount != stackCount)
                        throw new InvalidDataException("载荷身份与条目不符，或有尾随字节。");
                    snapshot = decoded;
                }
                error = null; return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is IOException)
            { error = exception.Message; return false; }
        }

        private static void CheckNode(ItemId id, int depth, HashSet<ulong> ids, ref int count)
        {
            if (depth > RuntimeItemSnapshot.MaxDepth || ++count > RuntimeItemSnapshot.MaxTreeItems
                || (depth > 0 && !id.IsValid) || (id.IsValid && !ids.Add(id.Value)))
                throw new InvalidDataException("物品树超限或有重复/无效子标识。");
        }

        private static void WriteTag(BinaryWriter writer, BackpackTag tag, bool optional = false)
        {
            // BackpackTag仅允许ASCII字母、数字、下划线和点，因此字符数与UTF8字节数一致。
            WeaponDefinitionValidation.Tag(tag, optional, "快照");
            byte[] bytes = Encoding.UTF8.GetBytes(tag.Name);
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }

        private static BackpackTag ReadTag(BinaryReader reader, bool optional = false)
        {
            int length = reader.ReadUInt16();
            if (length > RuntimeItemSnapshot.MaxItemTypeLength || reader.BaseStream.Length - reader.BaseStream.Position < length)
                throw new InvalidDataException("快照标签长度非法。");
            var tag = new BackpackTag(Encoding.UTF8.GetString(reader.ReadBytes(length)));
            WeaponDefinitionValidation.Tag(tag, optional, "快照"); return tag;
        }

        private static void WriteItem(BinaryWriter writer, RuntimeItemSnapshot item, int depth, HashSet<ulong> ids, ref int count)
        {
            if (item.Version != RuntimeItemSnapshot.CurrentVersion)
                throw new InvalidDataException("完整物品树不能混入旧版或未知版本子快照。");
            CheckNode(item.ItemId, depth, ids, ref count);
            writer.Write(item.ItemId.Value); WriteTag(writer, item.ItemType); writer.Write(item.StackCount);
            if (item.StackCount < 1 || !TryEncodeNumbers(item.Numbers, out byte[] numbers, out string error))
                throw new InvalidDataException("物品数量或数值状态非法。");
            writer.Write((ushort)numbers.Length); writer.Write(numbers);
            byte kind = item.Weapon != null ? (byte)1 : item.Magazine != null ? (byte)2 : (byte)0;
            if (item.Weapon != null && item.Magazine != null) throw new InvalidDataException("物品状态类型冲突。");
            writer.Write(kind);
            if (kind == 1)
            {
                WeaponStateSnapshot state = item.Weapon;
                writer.Write(state.Durability); WriteTag(writer, state.ChamberAmmo, true); writer.Write(state.ShotSequence);
                writer.Write(state.Magazine.HasValue);
                if (state.Magazine.HasValue) WriteItem(writer, state.Magazine.Value, depth + 1, ids, ref count);
                writer.Write((byte)state.Attachments.Count);
                foreach (AttachmentStateSnapshot attachment in state.Attachments)
                { writer.Write((byte)attachment.Slot); WriteItem(writer, attachment.Item, depth + 1, ids, ref count); }
            }
            else if (kind == 2)
            {
                writer.Write((ushort)item.Magazine.Rounds.Count);
                foreach (BackpackTag round in item.Magazine.Rounds) WriteTag(writer, round);
            }
        }

        private static RuntimeItemSnapshot ReadItem(BinaryReader reader, int depth, HashSet<ulong> ids, ref int count)
        {
            var id = new ItemId(reader.ReadUInt64()); CheckNode(id, depth, ids, ref count);
            BackpackTag type = ReadTag(reader); int stack = reader.ReadInt32();
            int length = reader.ReadUInt16();
            if (length < HeaderBytes || length > HeaderBytes + RuntimeItemSnapshot.MaxNumberStateCount * NodeBytes
                || reader.BaseStream.Length - reader.BaseStream.Position < length) throw new InvalidDataException("数值状态长度非法。");
            if (!TryDecodeNumbers(reader.ReadBytes(length), out FragmentNumberState[] numbers, out string error)) throw new InvalidDataException(error);
            byte kind = reader.ReadByte();
            WeaponStateSnapshot weapon = null; MagazineStateSnapshot magazine = null;
            if (kind == 1)
            {
                float durability = reader.ReadSingle(); BackpackTag chamber = ReadTag(reader, true); ulong sequence = reader.ReadUInt64();
                byte hasMagazine = reader.ReadByte();
                if (hasMagazine > 1) throw new InvalidDataException("弹匣标志非法。");
                RuntimeItemSnapshot? child = hasMagazine == 1 ? ReadItem(reader, depth + 1, ids, ref count) : (RuntimeItemSnapshot?)null;
                int attachmentCount = reader.ReadByte();
                if (attachmentCount > 1) throw new InvalidDataException("配件数量超限。");
                var attachments = new List<AttachmentStateSnapshot>();
                for (int i = 0; i < attachmentCount; i++)
                    attachments.Add(new AttachmentStateSnapshot((WeaponAttachmentSlot)reader.ReadByte(), ReadItem(reader, depth + 1, ids, ref count)));
                weapon = new WeaponStateSnapshot(durability, chamber, sequence, child, attachments);
            }
            else if (kind == 2)
            {
                int roundCount = reader.ReadUInt16();
                if (roundCount > 256) throw new InvalidDataException("弹数超限。");
                var rounds = new List<BackpackTag>();
                for (int i = 0; i < roundCount; i++) rounds.Add(ReadTag(reader));
                magazine = new MagazineStateSnapshot(rounds);
            }
            else if (kind != 0) throw new InvalidDataException("物品状态类型非法。");
            return new RuntimeItemSnapshot(id, type, stack, numbers, weapon, magazine);
        }
    }
}
