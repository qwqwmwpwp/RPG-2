using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>维护一次显示投影的有序布局、有效格、物品和占格索引。</summary>
    internal sealed class ContainerDisplayProjection
    {
        internal static readonly ContainerDisplayProjection Empty = new ContainerDisplayProjection();
        private readonly List<Vector2Int> coordinates = new List<Vector2Int>();
        private readonly HashSet<Vector2Int> validCells = new HashSet<Vector2Int>();
        private readonly Dictionary<Vector2Int, string> occupancy = new Dictionary<Vector2Int, string>();
        private readonly Dictionary<string, ItemDisplaySnapshot> items =
            new Dictionary<string, ItemDisplaySnapshot>(StringComparer.Ordinal);

        internal IReadOnlyList<Vector2Int> Coordinates => coordinates;
        internal IReadOnlyDictionary<string, ItemDisplaySnapshot> Items => items;

        /// <summary>按配置生成候选布局；预置模式读取首次绑定前的坐标。</summary>
        internal static bool TryBuildConfiguredShape(ContainerUIConfiguration configuration,
            IReadOnlyList<Vector2Int> authored, out ContainerDisplayProjection candidate, out string error)
        {
            candidate = new ContainerDisplayProjection();
            switch (configuration.LayoutMode)
            {
                case ContainerLayoutMode.Rectangle:
                    long count = (long)configuration.GridSize.x * configuration.GridSize.y;
                    if (configuration.GridSize.x < 1 || configuration.GridSize.x > BackpackContainerData.MaxColumns
                        || configuration.GridSize.y < 1 || count > int.MaxValue)
                        return Fail("初始格子列数必须在 1 到 5 之间，行数须为正数且总格数不能超出索引范围。", out error);
                    for (int y = 0; y < configuration.GridSize.y; y++)
                    for (int x = 0; x < configuration.GridSize.x; x++)
                    {
                        var coordinate = new Vector2Int(x, y);
                        candidate.coordinates.Add(coordinate);
                        candidate.validCells.Add(coordinate);
                    }
                    error = null;
                    return true;
                case ContainerLayoutMode.AuthoredCells:
                    return candidate.TryAddCoordinates(authored, false, out error);
                case ContainerLayoutMode.Coordinates:
                    return candidate.TryAddCoordinates(configuration.ValidCells, false, out error);
                default: return Fail("容器布局模式无效。", out error);
            }
        }

        /// <summary>校验快照布局并建立候选索引；未指定布局时沿用已配置的形状。</summary>
        internal static bool TryFromSnapshot(ContainerDisplaySnapshot snapshot, IReadOnlyList<Vector2Int> initialCoordinates,
            out ContainerDisplayProjection candidate, out string error)
        {
            candidate = new ContainerDisplayProjection();
            return candidate.TryAddCoordinates(snapshot.ValidCells ?? initialCoordinates, true, out error)
                && candidate.TryIndexItems(snapshot.Items, snapshot.RejectContainers, out error);
        }

        /// <summary>依次保存有效坐标，拒绝越界或重复的格子。</summary>
        private bool TryAddCoordinates(IReadOnlyList<Vector2Int> source, bool fromSnapshot, out string error)
        {
            error = null;
            if (source == null) return true;
            foreach (Vector2Int coordinate in source)
            {
                if (!BackpackContainerData.IsSupportedCoordinate(coordinate) || !validCells.Add(coordinate))
                    return Fail(fromSnapshot ? "快照有效格坐标无效或重复；列坐标必须在 0 到 4 之间。"
                        : "有效格坐标无效或重复；列坐标必须在 0 到 4 之间。", out error);
                coordinates.Add(coordinate);
            }
            return true;
        }

        /// <summary>在候选布局上验证全部物品，并记录每格对应的物品编号。</summary>
        internal bool TryIndexItems(IEnumerable<ItemDisplaySnapshot> source, bool rejectContainers, out string error)
        {
            error = null;
            if (source == null) return true;
            foreach (ItemDisplaySnapshot item in source)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || items.ContainsKey(item.Id))
                    return Fail("物品快照不能包含空物品、空编号或重复编号。", out error);
                if (!Evaluate(item, rejectContainers, out error)) return false;
                items.Add(item.Id, item);
                for (int y = 0; y < item.Size.y; y++)
                for (int x = 0; x < item.Size.x; x++)
                    occupancy.Add(item.Origin + new Vector2Int(x, y), item.Id);
            }
            return true;
        }

        /// <summary>检查物品数量、容器限制以及旋转后占地是否覆盖空闲有效格。</summary>
        private bool Evaluate(ItemDisplaySnapshot item, bool rejectContainers, out string error)
        {
            error = null;
            Vector2Int origin = item.Origin;
            Vector2Int size = item.Size;
            if (item.Count < 1) return Fail("物品编号或显示数量无效。", out error);
            if (rejectContainers && item.IsContainer) return Fail("容器内部禁止存放任何容器。", out error);
            if (size.x < 1 || size.y < 1 || origin.x < 0 || origin.y < 0
                || (long)origin.x + size.x > BackpackContainerData.MaxColumns || (long)origin.y + size.y > int.MaxValue
                || (long)size.x * size.y > validCells.Count)
                return Fail("物品超出有效格范围。", out error);
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                Vector2Int coordinate = origin + new Vector2Int(x, y);
                if (!validCells.Contains(coordinate)) return Fail("物品覆盖了无效格或容器边界。", out error);
                if (occupancy.ContainsKey(coordinate)) return Fail("目标格已有其他物品。", out error);
            }
            return true;
        }

        /// <summary>查询当前布局是否包含指定格。</summary>
        internal bool ContainsCell(Vector2Int coordinate) => validCells.Contains(coordinate);

        /// <summary>按占格索引查找覆盖此格的物品。</summary>
        internal bool TryGetItemAt(Vector2Int coordinate, out ItemDisplaySnapshot item)
        {
            item = null;
            return occupancy.TryGetValue(coordinate, out string id) && items.TryGetValue(id, out item);
        }

        /// <summary>按编号查找投影中的物品。</summary>
        internal bool TryGetItem(string id, out ItemDisplaySnapshot item)
        {
            item = null;
            return id != null && items.TryGetValue(id, out item);
        }

        /// <summary>返回候选布局或物品被拒绝的原因。</summary>
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
