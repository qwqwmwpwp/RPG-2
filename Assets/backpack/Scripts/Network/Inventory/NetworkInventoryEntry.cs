using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    /// <summary>库存网络投影：身份、结构/弹序基线、当前运行状态及权威布局，不持有运行时实例或 Unity 引用。完整恢复需要同时读取两个载荷。</summary>
    public struct NetworkInventoryEntry : IEquatable<NetworkInventoryEntry>
    {
        public ulong itemId; // 运行时实例标识（0 表示未登记，属于非法状态）。

        public string itemType; // 物品类型标签串（模板身份）。

        public int stackCount; // 堆叠数；唯一来源是服务端运行时物品的 StackCount。

        public byte[] fragmentState; // 结构、随机化数值及弹序基线；零版本的旧接口仍可承载完整快照。

        // 网络投影拆分：fragmentState 只随结构变化，runtimeState 是当前运行基线。
        public ulong structureVersion;
        public ulong runtimeVersion;
        public byte[] runtimeState;

        public int upperLeftIndex;
        public int width;
        public int height;

        /// <summary>以运行时物品信息构造条目；载荷为空时抛出 <see cref="ArgumentNullException"/>。</summary>
        public NetworkInventoryEntry(ItemId itemId, BackpackTag itemType, int stackCount, byte[] fragmentState, int upperLeftIndex = 0, int width = 1, int height = 1,
            ulong structureVersion = 0, ulong runtimeVersion = 0, byte[] runtimeState = null)
        {
            this.fragmentState = fragmentState ?? throw new ArgumentNullException(nameof(fragmentState));
            this.itemId = itemId.Value;
            this.itemType = itemType.ToString();
            this.stackCount = stackCount;
            this.upperLeftIndex = upperLeftIndex;
            this.width = width;
            this.height = height;
            this.structureVersion = structureVersion;
            this.runtimeVersion = runtimeVersion;
            this.runtimeState = runtimeState;
        }

        public ItemId Id => new ItemId(itemId); // 物品标识。

        public BackpackTag Tag => new BackpackTag(itemType); // 物品类型标签。

        public byte SnapshotVersion => fragmentState != null && fragmentState.Length > 0 ? fragmentState[0] : (byte)0; // 载荷声明的快照版本；载荷缺失时为 0。

        public bool IsEmpty => itemId == ItemId.NoneValue; // 是否为 default 空条目（SyncList 的默认操作参数）。

        /// <summary>结构校验：标识、类型标签、堆叠数与载荷长度 / 版本；不做完整解码（节点级校验发生在恢复物品时），只拦截明显非法的数据。</summary>
        public bool TryValidate(out string error)
        {
            if (IsEmpty)
            {
                error = "条目标识为 None，拒绝接受。";
                return false;
            }

            if (string.IsNullOrEmpty(itemType))
            {
                error = $"条目 {itemId} 的物品类型为空。";
                return false;
            }

            if (itemType.Length > RuntimeItemSnapshot.MaxItemTypeLength)
            {
                error = $"条目 {itemId} 的物品类型长度 {itemType.Length} 超过上限 {RuntimeItemSnapshot.MaxItemTypeLength}。";
                return false;
            }

            if (!new BackpackTag(itemType).IsValid)
            {
                error = $"条目 {itemId} 的物品类型 '{itemType}' 格式非法。";
                return false;
            }

            if (stackCount < 1)
            {
                error = $"条目 {itemId} 的堆叠数 {stackCount} 必须大于 0。";
                return false;
            }

            if (upperLeftIndex < 0 || upperLeftIndex >= RPG4.Backpack.Inventory.InventoryContainer.MaxSlotCount
                || width < 1 || height < 1 || (long)width * height > RPG4.Backpack.Inventory.InventoryContainer.MaxSlotCount)
            {
                error = $"条目 {itemId} 的位置或占格尺寸非法。";
                return false;
            }

            if (fragmentState == null || fragmentState.Length == 0)
            {
                error = $"条目 {itemId} 的运行时状态载荷为空。";
                return false;
            }

            if (fragmentState.Length < RuntimeItemSnapshotCodec.HeaderBytes
                || fragmentState.Length > RuntimeItemSnapshotCodec.MaxPayloadBytes)
            {
                error = $"条目 {itemId} 的运行时状态载荷长度 {fragmentState.Length} 越界"
                    + $"（允许 {RuntimeItemSnapshotCodec.HeaderBytes}..{RuntimeItemSnapshotCodec.MaxPayloadBytes}）。";
                return false;
            }

            if (fragmentState[0] != 1 && fragmentState[0] != RuntimeItemSnapshot.CurrentVersion)
            {
                error = $"条目 {itemId} 的快照版本 {fragmentState[0]} 不受支持（期望 {RuntimeItemSnapshot.CurrentVersion}）。";
                return false;
            }

            if (structureVersion != 0 || runtimeVersion != 0 || runtimeState != null)
            {
                if (structureVersion == 0 || runtimeVersion == 0 || runtimeState == null
                    || runtimeState.Length < 2 || runtimeState.Length > RuntimeItemSnapshotCodec.MaxPayloadBytes
                    || runtimeState[0] != RuntimeItemSnapshotCodec.NetworkRuntimeVersion)
                {
                    error = $"条目 {itemId} 的结构/运行版本或运行载荷非法。";
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>状态等价：标识、类型、堆叠数、权威位置、占格尺寸与载荷内容全部一致；用于服务端「只在实际变化时下发」的判断，因此必须比较载荷内容而不是引用。</summary>
        public bool StateEquals(NetworkInventoryEntry other)
        {
            return upperLeftIndex == other.upperLeftIndex && width == other.width && height == other.height
                && ItemStateEquals(other);
        }

        /// <summary>比较物品载荷；仅移动布局时可保留同一运行时镜像实例。</summary>
        public bool ItemStateEquals(NetworkInventoryEntry other)
        {
            return StructureStateEquals(other) && runtimeVersion == other.runtimeVersion
                && PayloadEquals(runtimeState, other.runtimeState);
        }

        public bool StructureStateEquals(NetworkInventoryEntry other)
        {
            return itemId == other.itemId && stackCount == other.stackCount
                && structureVersion == other.structureVersion
                && string.Equals(itemType, other.itemType, StringComparison.Ordinal)
                && PayloadEquals(fragmentState, other.fragmentState);
        }

        /// <inheritdoc/>
        public bool Equals(NetworkInventoryEntry other) => StateEquals(other);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is NetworkInventoryEntry other && StateEquals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = itemId.GetHashCode();
                hash = (hash * 397) ^ stackCount;
                hash = (hash * 397) ^ upperLeftIndex;
                hash = (hash * 397) ^ width;
                hash = (hash * 397) ^ height;
                hash = (hash * 397) ^ structureVersion.GetHashCode();
                hash = (hash * 397) ^ runtimeVersion.GetHashCode();
                hash = (hash * 397) ^ (itemType == null ? 0 : itemType.GetHashCode());
                if (fragmentState != null)
                {
                    for (int i = 0; i < fragmentState.Length; i++)
                    {
                        hash = (hash * 397) ^ fragmentState[i];
                    }
                }

                if (runtimeState != null)
                    for (int i = 0; i < runtimeState.Length; i++) hash = (hash * 397) ^ runtimeState[i];

                return hash;
            }
        }

        /// <summary>可诊断文本，例如 <c>ItemId(7) core.ore x3 v1</c>。</summary>
        public override string ToString()
        {
            return Id + " " + itemType + " x" + stackCount + " v" + SnapshotVersion;
        }

        /// <summary>值相等运算符。</summary>
        public static bool operator ==(NetworkInventoryEntry left, NetworkInventoryEntry right) => left.StateEquals(right);

        /// <summary>值不等运算符。</summary>
        public static bool operator !=(NetworkInventoryEntry left, NetworkInventoryEntry right) => !left.StateEquals(right);

        /// <summary>逐字节比较两份载荷内容（长度一致才算相等）。</summary>
        private static bool PayloadEquals(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }
    }
}
