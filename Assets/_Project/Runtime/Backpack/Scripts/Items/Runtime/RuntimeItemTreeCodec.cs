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
            return TryCaptureTree(item, 0, new HashSet<RuntimeItem>(), new HashSet<ulong>(), ref count, out snapshot, out error, true);
        }

        /// <summary>稳定物品图；高频运行字段由独立载荷提供。</summary>
        public static bool TryCaptureStructure(RuntimeItem item, out RuntimeItemSnapshot snapshot, out string error)
        {
            int count = 0;
            return TryCaptureTree(item, 0, new HashSet<RuntimeItem>(), new HashSet<ulong>(), ref count, out snapshot, out error, false);
        }

        private static bool TryCaptureTree(RuntimeItem item, int depth, HashSet<RuntimeItem> seen,
            HashSet<ulong> ids, ref int count, out RuntimeItemSnapshot snapshot, out string error, bool includeRuntime)
        {
            snapshot = default;
            if (item == null || depth > RuntimeItemSnapshot.MaxDepth || ++count > RuntimeItemSnapshot.MaxTreeItems
                || !seen.Add(item) || (item.ItemId.IsValid && !ids.Add(item.ItemId.Value)) || (depth > 0 && !item.ItemId.IsValid))
            { error = "物品树超限、包含循环或重复/无效子物品标识。"; return false; }
            if (!TryCaptureNumbers(item, out RuntimeItemSnapshot basic, out error)) return false;
            WeaponStateSnapshot weapon = null;
            // 弹序是低频基线；运行载荷只表达从头消耗后的绝对剩余数。
            MagazineStateSnapshot magazine = item.MagazineState == null ? null : new MagazineStateSnapshot(item.MagazineState.Rounds);
            if (item.WeaponState != null)
            {
                WeaponItemState state = item.WeaponState;
                weapon = new WeaponStateSnapshot(includeRuntime ? state.Durability : 0f,
                    includeRuntime ? state.ChamberAmmo : default, includeRuntime ? state.ShotSequence : 0UL);
            }
            var children = new List<InstalledItemSnapshot>();
            foreach (var pair in item.Assembly.InstalledChildren)
            {
                if (!TryCaptureTree(pair.Value, depth + 1, seen, ids, ref count, out RuntimeItemSnapshot child, out error, includeRuntime)) return false;
                children.Add(new InstalledItemSnapshot(pair.Key, child));
            }
            snapshot = new RuntimeItemSnapshot(basic.ItemId, basic.ItemType, basic.StackCount, CopyNumbers(basic.Numbers), weapon, magazine, children);
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
            if (snapshot.Version == 1)
            { error = "v1快照须先通过清单恢复默认状态，再抓取v3完整快照。"; return false; }
            if (snapshot.Version != 2 && snapshot.Version != RuntimeItemSnapshot.CurrentVersion)
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
                snapshot = new RuntimeItemSnapshot(id, type, stackCount, numbers, null, null, Array.Empty<InstalledItemSnapshot>(), 1); return true;
            }
            try
            {
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    byte version = reader.ReadByte();
                    if (version != 2 && version != RuntimeItemSnapshot.CurrentVersion) throw new InvalidDataException("不支持的物品状态版本。");
                    int count = 0;
                    RuntimeItemSnapshot decoded = ReadItem(reader, 0, new HashSet<ulong>(), ref count, version);
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
            if (item.Version != 2 && item.Version != RuntimeItemSnapshot.CurrentVersion)
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
            }
            else if (kind == 2)
            {
                writer.Write((ushort)item.Magazine.Rounds.Count);
                foreach (BackpackTag round in item.Magazine.Rounds) WriteTag(writer, round);
            }
            writer.Write((byte)item.InstalledChildren.Count);
            foreach (InstalledItemSnapshot child in item.InstalledChildren)
            { WriteSlotId(writer, child.SlotId); WriteItem(writer, child.Item, depth + 1, ids, ref count); }
        }

        private static void WriteSlotId(BinaryWriter writer, string slotId)
        {
            if (!ItemAssemblySlots.IsValid(slotId)) throw new InvalidDataException("槽标识无效。");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(slotId);
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }

        private static string ReadSlotId(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length > ItemAssemblySlots.MaxSlotIdLength * 4 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("槽标识长度无效。");
            string slotId = new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
            if (!ItemAssemblySlots.IsValid(slotId)) throw new InvalidDataException("槽标识无效。");
            return slotId;
        }

        private static RuntimeItemSnapshot ReadItem(BinaryReader reader, int depth, HashSet<ulong> ids, ref int count, byte version)
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
                if (version == 2)
                {
                    byte hasMagazine = reader.ReadByte();
                    if (hasMagazine > 1) throw new InvalidDataException("弹匣标志非法。");
                    RuntimeItemSnapshot? child = hasMagazine == 1 ? ReadItem(reader, depth + 1, ids, ref count, version) : (RuntimeItemSnapshot?)null;
                    int attachmentCount = reader.ReadByte();
                    if (attachmentCount > 1) throw new InvalidDataException("配件数量超限。");
                    var attachments = new List<AttachmentStateSnapshot>();
                    for (int i = 0; i < attachmentCount; i++)
                        attachments.Add(new AttachmentStateSnapshot((WeaponAttachmentSlot)reader.ReadByte(), ReadItem(reader, depth + 1, ids, ref count, version)));
                    weapon = new WeaponStateSnapshot(durability, chamber, sequence, child, attachments);
                }
                else weapon = new WeaponStateSnapshot(durability, chamber, sequence);
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
            var children = new List<InstalledItemSnapshot>();
            if (version == 2) children.AddRange(RuntimeItemSnapshot.LegacyChildren(weapon));
            else
            {
                int childCount = reader.ReadByte();
                if (childCount >= RuntimeItemSnapshot.MaxTreeItems) throw new InvalidDataException("子物品数量超限。");
                for (int i = 0; i < childCount; i++)
                {
                    string slotId = ReadSlotId(reader);
                    children.Add(new InstalledItemSnapshot(slotId, ReadItem(reader, depth + 1, ids, ref count, version)));
                }
            }
            return new RuntimeItemSnapshot(id, type, stack, numbers, weapon, magazine, children, version);
        }
    }
}
