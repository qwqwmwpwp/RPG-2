using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>已校验的运行字段写入计划；仅在整批库存校验通过后提交，不发业务通知。</summary>
    public sealed class RuntimeItemStateUpdate
    {
        internal readonly List<Node> Nodes = new List<Node>();

        internal sealed class Node
        {
            internal RuntimeItem Item;
            internal float Durability;
            internal BackpackTag Chamber;
            internal ulong Sequence;
            internal int RemainingRounds;
        }

        public void Apply()
        {
            foreach (Node node in Nodes)
            {
                if (node.Item.WeaponState != null)
                    node.Item.WeaponState.ApplyRuntime(node.Durability, node.Chamber, node.Sequence);
                else node.Item.MagazineState.ApplyRemainingRounds(node.RemainingRounds);
            }
        }
    }

    public static partial class RuntimeItemSnapshotCodec
    {
        public const byte NetworkRuntimeVersion = 1;

        /// <summary>只编码武器运行字段及弹匣绝对剩余数；混装弹序保存在匹配结构版本的基线中。</summary>
        public static bool TryCaptureNetworkRuntime(RuntimeItem root, out byte[] payload, out string error)
        {
            payload = null;
            try
            {
                var nodes = new Dictionary<ulong, RuntimeItem>();
                CollectRuntimeNodes(root, nodes, new HashSet<RuntimeItem>(), 0);
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(NetworkRuntimeVersion);
                    writer.Write((byte)nodes.Count);
                    foreach (RuntimeItem item in nodes.Values)
                    {
                        writer.Write(item.ItemId.Value);
                        if (item.WeaponState != null)
                        {
                            WeaponItemState state = item.WeaponState;
                            writer.Write((byte)1);
                            writer.Write(state.Durability);
                            WriteTag(writer, state.ChamberAmmo, true);
                            writer.Write(state.ShotSequence);
                        }
                        else
                        {
                            writer.Write((byte)2);
                            int remaining = item.MagazineState.RoundCount;
                            if (remaining > 256) throw new InvalidDataException("弹数超限。");
                            writer.Write((ushort)remaining);
                        }
                    }
                    if (stream.Length > MaxPayloadBytes) throw new InvalidDataException("运行载荷超限。");
                    payload = stream.ToArray();
                }
                error = null;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is IOException)
            { error = exception.Message; return false; }
        }

        /// <summary>与现有结构逐节点校验；此阶段不修改任何镜像字段。</summary>
        public static bool TryPrepareNetworkRuntime(RuntimeItem root, byte[] payload, ItemManifestCatalog catalog,
            out RuntimeItemStateUpdate update, out string error)
        {
            update = null;
            if (payload == null || payload.Length < 2 || payload.Length > MaxPayloadBytes)
            { error = "运行载荷长度非法。"; return false; }
            try
            {
                var nodes = new Dictionary<ulong, RuntimeItem>();
                CollectRuntimeNodes(root, nodes, new HashSet<RuntimeItem>(), 0);
                var prepared = new RuntimeItemStateUpdate();
                using (var stream = new MemoryStream(payload, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (reader.ReadByte() != NetworkRuntimeVersion || reader.ReadByte() != nodes.Count)
                        throw new InvalidDataException("运行载荷版本或状态节点数与结构不符。");
                    int count = nodes.Count;
                    for (int i = 0; i < count; i++)
                    {
                        ulong id = reader.ReadUInt64();
                        if (!nodes.TryGetValue(id, out RuntimeItem item))
                            throw new InvalidDataException("运行载荷含重复或不属于当前结构的节点。");
                        nodes.Remove(id);
                        var node = new RuntimeItemStateUpdate.Node { Item = item };
                        byte kind = reader.ReadByte();
                        if (kind == 1 && item.WeaponState != null)
                        {
                            node.Durability = reader.ReadSingle();
                            node.Chamber = ReadTag(reader, true);
                            node.Sequence = reader.ReadUInt64();
                            if (!item.WeaponState.ValidateRuntime(node.Durability, node.Chamber, catalog, out error))
                                return false;
                        }
                        else if (kind == 2 && item.MagazineState != null)
                        {
                            node.RemainingRounds = reader.ReadUInt16();
                            if (node.RemainingRounds > item.MagazineState.RoundCount)
                                throw new InvalidDataException("剩余弹数超过当前弹序基线；增加或重排弹药必须提供新结构版本。");
                        }
                        else throw new InvalidDataException("运行节点类型与结构不符。");
                        prepared.Nodes.Add(node);
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("运行载荷有尾随字节。");
                }
                update = prepared;
                error = null;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is IOException)
            { error = exception.Message; return false; }
        }

        private static void CollectRuntimeNodes(RuntimeItem item, Dictionary<ulong, RuntimeItem> nodes,
            HashSet<RuntimeItem> seen, int depth)
        {
            if (item == null || !item.ItemId.IsValid || depth > RuntimeItemSnapshot.MaxDepth
                || !seen.Add(item) || seen.Count > RuntimeItemSnapshot.MaxTreeItems)
                throw new InvalidDataException("运行物品图为空、含环、无效标识或超限。");
            if (item.WeaponState != null || item.MagazineState != null) nodes.Add(item.ItemId.Value, item);
            if (item.WeaponState == null) return;
            if (item.WeaponState.Magazine != null) CollectRuntimeNodes(item.WeaponState.Magazine, nodes, seen, depth + 1);
            foreach (RuntimeItem child in item.WeaponState.Attachments.Values) CollectRuntimeNodes(child, nodes, seen, depth + 1);
        }
    }
}
