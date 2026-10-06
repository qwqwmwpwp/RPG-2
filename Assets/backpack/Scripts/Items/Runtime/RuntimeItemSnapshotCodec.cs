using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>运行时数值状态的显式编解码器：把 FragmentNumberState 列表编码为定长二进制载荷（小端布局 [version:1][count:2] + 每节点 [fragmentIndex:2][modifierIndex:2][value:4]）；显式遍历而非反射、版本化且有界、不写日志也不改全局状态。</summary>
    public static class RuntimeItemSnapshotCodec
    {
        public const int HeaderBytes = 3; // 载荷头长度：版本 1 字节 + 节点数量 2 字节。

        public const int NodeBytes = 8; // 单个节点长度：片段下标 2 + Modifier 下标 2 + 数值 4。

        public const int MaxPayloadBytes = HeaderBytes + RuntimeItemSnapshot.MaxNumberStateCount * NodeBytes; // 允许的最大载荷长度，超过即判定为异常数据。

        /// <summary>从运行时物品抓取完整快照；物品为空或存在仍处于「待随机化」的数值节点时失败。</summary>
        public static bool TryCapture(RuntimeItem item, out RuntimeItemSnapshot snapshot, out string error)
        {
            snapshot = default;
            if (item == null)
            {
                error = "物品为空，无法抓取运行时快照。";
                return false;
            }

            if (!TryCollectNodes(item, null, out List<NumberNode> nodes, out error))
            {
                return false;
            }

            FragmentNumberState[] numbers = new FragmentNumberState[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                NumberNode node = nodes[i];
                LabeledNumberFragment fragment = node.Fragment;
                if (fragment.RandomizeOnManifest)
                {
                    error = $"{node} 仍处于「待随机化」状态（RandomizeOnManifest=true），物品未经运行时初始化，拒绝抓取快照。";
                    return false;
                }

                numbers[i] = new FragmentNumberState(node.FragmentIndex, node.ModifierIndex, fragment.Value);
            }

            snapshot = new RuntimeItemSnapshot(item.ItemId, item.ItemType, item.StackCount, numbers);
            error = null;
            return true;
        }

        /// <summary>把快照数值写回运行时物品：结构必须逐节点对应、数值必须在模板 Min..Max 范围内，写入后清除「待随机化」标记以避免客户端恢复后再随机化一次。</summary>
        public static bool TryApplyNumbers(
            RuntimeItem item,
            IReadOnlyList<FragmentNumberState> numbers,
            out string error)
        {
            if (item == null)
            {
                error = "物品为空，无法写入运行时快照。";
                return false;
            }

            if (numbers == null)
            {
                error = "数值状态列表为空。";
                return false;
            }

            if (!TryCollectNodes(item, null, out List<NumberNode> nodes, out error))
            {
                return false;
            }

            if (nodes.Count != numbers.Count)
            {
                error = $"数值节点数量不匹配：模板期望 {nodes.Count} 个，快照提供 {numbers.Count} 个。";
                return false;
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                NumberNode node = nodes[i];
                FragmentNumberState state = numbers[i];
                if (node.FragmentIndex != state.FragmentIndex || node.ModifierIndex != state.ModifierIndex)
                {
                    error = $"数值节点顺序不匹配：模板第 {i} 个节点为 {node}，快照为 {state}。";
                    return false;
                }

                if (!node.Fragment.TryApplyRuntimeNumber(state.Value, node.ToString(), out error))
                {
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>编码数值状态列表；节点数量超限、下标越界或数值非有限数时失败。</summary>
        public static bool TryEncodeNumbers(
            IReadOnlyList<FragmentNumberState> numbers,
            out byte[] payload,
            out string error)
        {
            payload = null;
            if (numbers == null)
            {
                error = "数值状态列表为空。";
                return false;
            }

            if (numbers.Count > RuntimeItemSnapshot.MaxNumberStateCount)
            {
                error = $"数值节点数量 {numbers.Count} 超过上限 {RuntimeItemSnapshot.MaxNumberStateCount}。";
                return false;
            }

            byte[] buffer = new byte[HeaderBytes + numbers.Count * NodeBytes];
            buffer[0] = RuntimeItemSnapshot.CurrentVersion;
            WriteUInt16(buffer, 1, (ushort)numbers.Count);

            int offset = HeaderBytes;
            for (int i = 0; i < numbers.Count; i++)
            {
                FragmentNumberState state = numbers[i];
                if (state.FragmentIndex < 0 || state.FragmentIndex > FragmentNumberState.MaxIndex
                    || state.ModifierIndex < FragmentNumberState.SelfModifierIndex
                    || state.ModifierIndex > FragmentNumberState.MaxIndex)
                {
                    error = $"第 {i} 个节点 {state} 的下标越界，无法编码。";
                    return false;
                }

                if (float.IsNaN(state.Value) || float.IsInfinity(state.Value))
                {
                    error = $"第 {i} 个节点 {state} 的数值 {state.Value} 不是有限数，无法编码。";
                    return false;
                }

                WriteUInt16(buffer, offset, (ushort)state.FragmentIndex);
                WriteInt16(buffer, offset + 2, (short)state.ModifierIndex);
                WriteInt32(buffer, offset + 4, ToInt32Bits(state.Value));
                offset += NodeBytes;
            }

            payload = buffer;
            error = null;
            return true;
        }

        /// <summary>解码数值状态列表；版本不符、长度越界、数量越界、数值非法或存在尾随字节均失败。</summary>
        public static bool TryDecodeNumbers(byte[] payload, out FragmentNumberState[] numbers, out string error)
        {
            numbers = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空。";
                return false;
            }

            if (payload.Length > MaxPayloadBytes)
            {
                error = $"载荷长度 {payload.Length} 超过上限 {MaxPayloadBytes}。";
                return false;
            }

            if (payload.Length < HeaderBytes)
            {
                error = $"载荷长度 {payload.Length} 不足以容纳 {HeaderBytes} 字节包头。";
                return false;
            }

            byte version = payload[0];
            if (version != RuntimeItemSnapshot.CurrentVersion)
            {
                error = $"快照版本 {version} 不受支持（期望 {RuntimeItemSnapshot.CurrentVersion}）。";
                return false;
            }

            int count = ReadUInt16(payload, 1);
            if (count > RuntimeItemSnapshot.MaxNumberStateCount)
            {
                error = $"载荷声明的节点数量 {count} 超过上限 {RuntimeItemSnapshot.MaxNumberStateCount}。";
                return false;
            }

            int expectedLength = HeaderBytes + count * NodeBytes;
            if (payload.Length != expectedLength)
            {
                error = $"载荷长度 {payload.Length} 与声明的节点数量 {count} 不匹配（期望 {expectedLength}）。";
                return false;
            }

            FragmentNumberState[] result = new FragmentNumberState[count];
            int offset = HeaderBytes;
            for (int i = 0; i < count; i++)
            {
                int fragmentIndex = ReadUInt16(payload, offset);
                int modifierIndex = ReadInt16(payload, offset + 2);
                float value = ToSingle(ReadInt32(payload, offset + 4));
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    error = $"载荷第 {i} 个节点的数值 {value} 不是有限数。";
                    return false;
                }

                if (modifierIndex < FragmentNumberState.SelfModifierIndex)
                {
                    error = $"载荷第 {i} 个节点的 Modifier 下标 {modifierIndex} 非法。";
                    return false;
                }

                result[i] = new FragmentNumberState(fragmentIndex, modifierIndex, value);
                offset += NodeBytes;
            }

            numbers = result;
            error = null;
            return true;
        }

        /// <summary>按「模板遍历顺序」收集全部数值节点：顶层 LabeledNumberFragment，以及 ConsumableFragment / EquipmentFragment 下的 Modifier；buffer 为 null 时新建，可复用则先清空。</summary>
        private static bool TryCollectNodes(
            RuntimeItem item,
            List<NumberNode> buffer,
            out List<NumberNode> nodes,
            out string error)
        {
            nodes = buffer ?? new List<NumberNode>();
            nodes.Clear();

            IReadOnlyList<ItemFragment> fragments = item.Fragments;
            for (int i = 0; i < fragments.Count; i++)
            {
                ItemFragment fragment = fragments[i];
                if (fragment == null)
                {
                    error = $"Fragments[{i}] 为空，无法遍历运行时数值节点。";
                    return false;
                }

                switch (fragment)
                {
                    case LabeledNumberFragment number:
                        if (nodes.Count >= RuntimeItemSnapshot.MaxNumberStateCount)
                        {
                            error = $"数值节点数量超过上限 {RuntimeItemSnapshot.MaxNumberStateCount}。";
                            return false;
                        }

                        nodes.Add(new NumberNode(i, FragmentNumberState.SelfModifierIndex, number));
                        break;
                    case ConsumableFragment consumable:
                        if (!TryCollectModifiers(
                            consumable.Modifiers, i, nodes, out error))
                        {
                            return false;
                        }

                        break;
                    case EquipmentFragment equipment:
                        if (!TryCollectModifiers(
                            equipment.Modifiers, i, nodes, out error))
                        {
                            return false;
                        }

                        break;
                }
            }

            error = null;
            return true;
        }

        /// <summary>收集单个片段下的 Modifier 数值节点，并校验节点数量上限。</summary>
        private static bool TryCollectModifiers(
            IReadOnlyList<LabeledNumberFragment> modifiers,
            int fragmentIndex,
            List<NumberNode> nodes,
            out string error)
        {
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (modifiers[i] == null)
                {
                    error = $"Fragments[{fragmentIndex}].Modifiers[{i}] 为空，无法遍历运行时数值节点。";
                    return false;
                }

                if (nodes.Count >= RuntimeItemSnapshot.MaxNumberStateCount)
                {
                    error = $"数值节点数量超过上限 {RuntimeItemSnapshot.MaxNumberStateCount}。";
                    return false;
                }

                nodes.Add(new NumberNode(fragmentIndex, i, modifiers[i]));
            }

            error = null;
            return true;
        }

        /// <summary>以小端写入 16 位无符号整数。</summary>
        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        /// <summary>以小端读取 16 位无符号整数。</summary>
        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        /// <summary>以小端写入 16 位有符号整数。</summary>
        private static void WriteInt16(byte[] buffer, int offset, short value)
        {
            WriteUInt16(buffer, offset, unchecked((ushort)value));
        }

        /// <summary>以小端读取 16 位有符号整数。</summary>
        private static short ReadInt16(byte[] buffer, int offset)
        {
            return unchecked((short)ReadUInt16(buffer, offset));
        }

        /// <summary>以小端写入 32 位有符号整数。</summary>
        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        /// <summary>以小端读取 32 位有符号整数。</summary>
        private static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
        }

        /// <summary>把 float 的位模式转为 int（位级重解释，无分配）。</summary>
        private static int ToInt32Bits(float value) => new FloatBits { Float = value }.Int;

        /// <summary>把 int 的位模式转为 float（位级重解释，无分配）。</summary>
        private static float ToSingle(int bits) => new FloatBits { Int = bits }.Float;

        /// <summary>数值节点：坐标 + 可读写的运行时数值节点引用。</summary>
        private readonly struct NumberNode
        {
            /// <summary>以坐标与片段引用构造。</summary>
            public NumberNode(int fragmentIndex, int modifierIndex, LabeledNumberFragment fragment)
            {
                FragmentIndex = fragmentIndex;
                ModifierIndex = modifierIndex;
                Fragment = fragment;
            }

            public int FragmentIndex { get; } // 顶层片段下标。

            public int ModifierIndex { get; } // 嵌套 Modifier 下标。

            public LabeledNumberFragment Fragment { get; } // 对应的可读写数值片段。

            /// <summary>可诊断文本，例如 Fragments[0] 或 Fragments[0].Modifiers[1]。</summary>
            public override string ToString()
            {
                return ModifierIndex == FragmentNumberState.SelfModifierIndex
                    ? "Fragments[" + FragmentIndex + "]"
                    : "Fragments[" + FragmentIndex + "].Modifiers[" + ModifierIndex + "]";
            }
        }

        /// <summary>float 与 int 的位级重叠视图（避免 unsafe，且不依赖 BitConverter 的分配）。</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Float; // float 视图。

            [FieldOffset(0)] public int Int; // int 位视图。
        }
    }
}
