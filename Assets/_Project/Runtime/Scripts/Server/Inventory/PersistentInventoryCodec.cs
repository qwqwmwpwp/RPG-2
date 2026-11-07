using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using RPG4.Raid.Domain;

namespace RPG4.Server.Inventory
{
    /// <summary>Versioned database envelope. The nested item codec remains the sole item-state format.</summary>
    internal static class PersistentInventoryCodec
    {
        internal const int MaxBytes = 8 * 1024 * 1024;
        internal static PersistentContainerData Empty(PersonalContainerKind kind) => new PersistentContainerData
        {
            Kind = kind, Rows = kind == PersonalContainerKind.Stash ? 30 : kind == PersonalContainerKind.Backpack ? 10 : 1,
            Columns = kind == PersonalContainerKind.Stash ? 10 : kind == PersonalContainerKind.Backpack ? 10 : 1
        };

        internal static byte[] Container(PersistentContainerData value)
        {
            Validate(value);
            return Write(w => WriteContainer(w, value));
        }
        internal static PersistentContainerData Container(byte[] bytes) => Read(bytes, ReadContainer);
        internal static PersistentContainerData Freeze(PersistentContainerData value) => Container(Container(value));
        internal static byte[] State(PersistentInventoryState value) => Write(w =>
        {
            w.Write(value.AccountId); w.Write(value.CharacterId); w.Write(value.Revision); w.Write(value.ActiveRaidId ?? string.Empty);
            WriteContainer(w, value.Stash); WriteContainer(w, value.Backpack); WriteContainer(w, value.Equipment);
        });
        internal static PersistentInventoryState State(byte[] bytes) => Read(bytes, r =>
        {
            var value = new PersistentInventoryState { AccountId = r.ReadUInt64(), CharacterId = r.ReadUInt64(), Revision = r.ReadInt64(), ActiveRaidId = r.ReadString() };
            if (value.ActiveRaidId.Length == 0) value.ActiveRaidId = null;
            value.ReadOnly = value.ActiveRaidId != null;
            value.Stash = ReadContainer(r); value.Backpack = ReadContainer(r); value.Equipment = ReadContainer(r);
            return value;
        });
        internal static byte[] Reservation(RaidReservation value) => Write(w =>
        {
            w.Write(value.RaidId); w.Write(value.WorldRunId); w.Write(value.AccountId); w.Write(value.CharacterId);
            w.Write(value.Revision); w.Write((byte)value.Status); WriteContainer(w, value.Backpack); WriteContainer(w, value.Equipment);
        });
        internal static RaidReservation Reservation(byte[] bytes) => Read(bytes, r => new RaidReservation
        {
            RaidId = r.ReadString(), WorldRunId = r.ReadString(), AccountId = r.ReadUInt64(), CharacterId = r.ReadUInt64(),
            Revision = r.ReadInt64(), Status = (PersistentRaidStatus)r.ReadByte(), Backpack = ReadContainer(r), Equipment = ReadContainer(r)
        });
        internal static byte[] Bag(PersistentDropBag value) => Write(w =>
        {
            w.Write(value.BagId); w.Write(value.RaidId); w.Write(value.WorldRunId); w.Write(value.Published);
            WriteContainer(w, value.Backpack); WriteContainer(w, value.Equipment);
        });
        internal static PersistentDropBag Bag(byte[] bytes) => Read(bytes, r => new PersistentDropBag
        {
            BagId = r.ReadString(), RaidId = r.ReadString(), WorldRunId = r.ReadString(), Published = r.ReadBoolean(),
            Backpack = ReadContainer(r), Equipment = ReadContainer(r)
        });
        internal static byte[] Settlement(RaidSettlementResult value) => Write(w =>
        {
            WriteBytes(w, State(value.Inventory)); w.Write((byte)value.Status); w.Write(value.DropBag != null);
            if (value.DropBag != null) WriteBytes(w, Bag(value.DropBag));
        });
        internal static RaidSettlementResult Settlement(byte[] bytes) => Read(bytes, r => new RaidSettlementResult
        {
            Inventory = State(ReadBytes(r)), Status = (PersistentRaidStatus)r.ReadByte(), DropBag = r.ReadBoolean() ? Bag(ReadBytes(r)) : null
        });
        internal static Dictionary<ulong, RuntimeItemSnapshot> Items(params PersistentContainerData[] containers)
        {
            var result = new Dictionary<ulong, RuntimeItemSnapshot>();
            foreach (PersistentContainerData container in containers)
            {
                Validate(container);
                foreach (PersistentItemData item in container.Items) AddTree(Decode(item), result);
            }
            return result;
        }

        /// <summary>Clone a trusted snapshot while assigning fresh persistent ids to every node in each item tree.</summary>
        internal static PersistentContainerData CloneWithNewIds(PersistentContainerData value, Func<ulong> allocateId)
        {
            Validate(value);
            if (allocateId == null) throw new ArgumentNullException(nameof(allocateId));
            var clone = new PersistentContainerData { Kind = value.Kind, Rows = value.Rows, Columns = value.Columns,
                Items = new PersistentItemData[value.Items.Length] };
            for (int i = 0; i < value.Items.Length; i++)
            {
                PersistentItemData source = value.Items[i];
                RuntimeItemSnapshot snapshot = Decode(source);
                RuntimeItemSnapshot remapped = Remap(snapshot, allocateId);
                if (!RuntimeItemSnapshotCodec.TryEncodeSnapshot(remapped, out byte[] payload, out string error))
                    throw new InventoryRaidException("inventory_item_invalid");
                clone.Items[i] = new PersistentItemData { ItemId = remapped.ItemId.Value, ItemType = remapped.ItemType.Name,
                    StackCount = remapped.StackCount, UpperLeftIndex = source.UpperLeftIndex, Payload = payload };
            }
            Validate(clone);
            return clone;
        }

        private static RuntimeItemSnapshot Remap(RuntimeItemSnapshot source, Func<ulong> allocateId)
        {
            var children = new List<InstalledItemSnapshot>(source.InstalledChildren.Count);
            foreach (InstalledItemSnapshot child in source.InstalledChildren)
                children.Add(new InstalledItemSnapshot(child.SlotId, Remap(child.Item, allocateId)));
            // v2 decoding exposes legacy magazine/attachment references through Weapon while also
            // migrating them into InstalledChildren. Rebuild the weapon state without those old
            // references so the constructor does not compare old ids with the remapped subtree.
            WeaponStateSnapshot weapon = source.Weapon == null ? null : new WeaponStateSnapshot(
                source.Weapon.Durability, source.Weapon.ChamberAmmo, source.Weapon.ShotSequence);
            return new RuntimeItemSnapshot(new ItemId(allocateId()), source.ItemType, source.StackCount,
                CopyNumbers(source.Numbers), weapon, source.Magazine, children);
        }

        private static FragmentNumberState[] CopyNumbers(IReadOnlyList<FragmentNumberState> source)
        {
            var copy = new FragmentNumberState[source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }
        private static void AddTree(RuntimeItemSnapshot item, Dictionary<ulong, RuntimeItemSnapshot> result)
        {
            if (result.ContainsKey(item.ItemId.Value)) throw new InventoryRaidException("inventory_duplicate_item");
            result.Add(item.ItemId.Value, item);
            foreach (InstalledItemSnapshot child in item.InstalledChildren) AddTree(child.Item, result);
        }
        internal static Dictionary<string, long> Quantities(Dictionary<ulong, RuntimeItemSnapshot> items)
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            void Add(string key, long count) { result.TryGetValue(key, out long previous); result[key] = checked(previous + count); }
            foreach (RuntimeItemSnapshot item in items.Values)
            {
                Add(item.ItemType.Name, item.StackCount);
                if (item.Magazine != null) foreach (BackpackTag round in item.Magazine.Rounds) Add(round.Name, 1);
                if (item.Weapon != null && !item.Weapon.ChamberAmmo.IsEmpty) Add(item.Weapon.ChamberAmmo.Name, 1);
            }
            return result;
        }
        internal static void RequireSameQuantities(Dictionary<ulong, RuntimeItemSnapshot> before, Dictionary<ulong, RuntimeItemSnapshot> after)
        {
            Dictionary<string, long> a = Quantities(before), b = Quantities(after);
            if (a.Count != b.Count || a.Any(pair => !b.TryGetValue(pair.Key, out long count) || count != pair.Value))
                throw new InventoryRaidException("inventory_quantity_mismatch");
        }
        internal static RuntimeItemSnapshot Decode(PersistentItemData item)
        {
            try
            {
                if (item == null || item.ItemId == 0 || item.Payload == null || item.Payload.Length > RuntimeItemSnapshotCodec.MaxPayloadBytes
                    || !RuntimeItemSnapshotCodec.TryDecodeSnapshot(new ItemId(item.ItemId), new BackpackTag(item.ItemType), item.StackCount,
                        item.Payload, out RuntimeItemSnapshot snapshot, out _)) throw new InventoryRaidException("inventory_item_invalid");
                return snapshot;
            }
            catch (ArgumentException) { throw new InventoryRaidException("inventory_item_invalid"); }
        }
        internal static void Validate(PersistentContainerData value)
        {
            if (value == null || !Enum.IsDefined(typeof(PersonalContainerKind), value.Kind)) throw new InventoryRaidException("inventory_container_invalid");
            PersistentContainerData shape = Empty(value.Kind);
            if (value.Rows != shape.Rows || value.Columns != shape.Columns || value.Items == null || value.Items.Length > value.Rows * value.Columns)
                throw new InventoryRaidException("inventory_shape_invalid");
            var positions = new HashSet<int>();
            foreach (PersistentItemData item in value.Items)
            {
                Decode(item);
                if (item.UpperLeftIndex < 0 || item.UpperLeftIndex >= value.Rows * value.Columns || !positions.Add(item.UpperLeftIndex))
                    throw new InventoryRaidException("inventory_position_invalid");
                if (value.Kind == PersonalContainerKind.Equipment && item.StackCount != 1) throw new InventoryRaidException("inventory_equipment_invalid");
            }
        }
        private static void WriteContainer(BinaryWriter w, PersistentContainerData value)
        {
            Validate(value); w.Write((byte)value.Kind); w.Write(value.Rows); w.Write(value.Columns); w.Write(value.Items.Length);
            foreach (PersistentItemData item in value.Items.OrderBy(i => i.UpperLeftIndex))
            { w.Write(item.ItemId); w.Write(item.ItemType); w.Write(item.StackCount); w.Write(item.UpperLeftIndex); WriteBytes(w, item.Payload); }
        }
        private static PersistentContainerData ReadContainer(BinaryReader r)
        {
            var value = new PersistentContainerData { Kind = (PersonalContainerKind)r.ReadByte(), Rows = r.ReadInt32(), Columns = r.ReadInt32() };
            int count = r.ReadInt32();
            if (count < 0 || count > 300) throw new InventoryRaidException("inventory_item_count_invalid");
            value.Items = new PersistentItemData[count];
            for (int i = 0; i < count; i++) value.Items[i] = new PersistentItemData
            { ItemId = r.ReadUInt64(), ItemType = r.ReadString(), StackCount = r.ReadInt32(), UpperLeftIndex = r.ReadInt32(), Payload = ReadBytes(r) };
            Validate(value); return value;
        }
        private static byte[] Write(Action<BinaryWriter> action)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) { writer.Write((byte)1); action(writer); }
                if (stream.Length > MaxBytes) throw new InventoryRaidException("inventory_payload_too_large");
                return stream.ToArray();
            }
        }
        private static T Read<T>(byte[] bytes, Func<BinaryReader, T> action)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new InventoryRaidException("inventory_payload_invalid");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadByte() != 1) throw new InventoryRaidException("inventory_payload_version");
                T result = action(reader);
                if (stream.Position != stream.Length) throw new InventoryRaidException("inventory_payload_trailing_bytes");
                return result;
            }
        }
        private static void WriteBytes(BinaryWriter w, byte[] value) { w.Write(value.Length); w.Write(value); }
        private static byte[] ReadBytes(BinaryReader r)
        {
            int length = r.ReadInt32();
            if (length < 0 || length > MaxBytes || length > r.BaseStream.Length - r.BaseStream.Position)
                throw new InventoryRaidException("inventory_payload_length");
            return r.ReadBytes(length);
        }
    }
}
