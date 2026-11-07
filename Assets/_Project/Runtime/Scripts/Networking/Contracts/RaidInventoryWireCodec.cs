using RPG4.Raid.Domain;
using System;
using System.IO;
using System.Text;

namespace RPG4.Networking.Contracts
{
    public static class RaidInventoryWireCodec
    {
        public const int MaxBytes = 8 * 1024 * 1024;
        public const int ChunkBytes = 24000;
        public static byte[] Encode(PersistentInventoryState state)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(state.Revision); writer.Write(state.ActiveRaidId ?? ""); writer.Write(state.ReadOnly);
                Write(writer, state.Stash); Write(writer, state.Backpack); Write(writer, state.Equipment);
                if (stream.Length > MaxBytes) throw new InvalidDataException("Inventory snapshot exceeds limit.");
                return stream.ToArray();
            }
        }
        public static PersistentInventoryState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaxBytes) throw new InvalidDataException();
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var state = new PersistentInventoryState { Revision = reader.ReadInt64(), ActiveRaidId = reader.ReadString(), ReadOnly = reader.ReadBoolean() };
                if (state.ActiveRaidId.Length > 64) throw new InvalidDataException();
                state.Stash = Read(reader); state.Backpack = Read(reader); state.Equipment = Read(reader);
                if (stream.Position != stream.Length) throw new InvalidDataException();
                return state;
            }
        }
        private static void Write(BinaryWriter writer, PersistentContainerData container)
        {
            if (container == null || container.Items == null) throw new InvalidDataException();
            writer.Write((byte)container.Kind); writer.Write(container.Rows); writer.Write(container.Columns); writer.Write(container.Items.Length);
            foreach (PersistentItemData item in container.Items)
            {
                writer.Write(item.ItemId); writer.Write(item.ItemType); writer.Write(item.StackCount); writer.Write(item.UpperLeftIndex);
                writer.Write(item.Payload.Length); writer.Write(item.Payload);
            }
        }
        private static PersistentContainerData Read(BinaryReader reader)
        {
            var value = new PersistentContainerData { Kind = (PersonalContainerKind)reader.ReadByte(), Rows = reader.ReadInt32(), Columns = reader.ReadInt32() };
            int count = reader.ReadInt32();
            if (value.Kind > PersonalContainerKind.Equipment || value.Rows < 1 || value.Columns < 1
                || (long)value.Rows * value.Columns > 65536 || count < 0 || count > value.Rows * value.Columns) throw new InvalidDataException();
            value.Items = new PersistentItemData[count];
            for (int i = 0; i < count; i++)
            {
                var item = new PersistentItemData { ItemId = reader.ReadUInt64(), ItemType = reader.ReadString(), StackCount = reader.ReadInt32(), UpperLeftIndex = reader.ReadInt32() };
                int length = reader.ReadInt32();
                if (item.ItemType.Length > 128 || item.ItemId == 0 || item.StackCount < 1 || length < 1 || length > 65536
                    || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException();
                item.Payload = reader.ReadBytes(length); value.Items[i] = item;
            }
            return value;
        }
    }
}
