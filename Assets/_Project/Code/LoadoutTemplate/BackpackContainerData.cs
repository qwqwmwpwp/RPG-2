using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>独立储物数据。格子形状、占用和物品实例由此持有。</summary>
    public sealed class BackpackContainerData
    {
        public const int MaxColumns = 5;
        public static bool IsSupportedCoordinate(Vector2Int coordinate) => coordinate.x >= 0
            && coordinate.x < MaxColumns && coordinate.y >= 0 && coordinate.y < int.MaxValue;

        internal sealed class PreparedContents
        {
            internal readonly Dictionary<string, ItemDisplayData> ById = new Dictionary<string, ItemDisplayData>(StringComparer.Ordinal);
            internal readonly Dictionary<Vector2Int, string> Occupied = new Dictionary<Vector2Int, string>();
            internal IReadOnlyList<ItemDisplayData> Items;
        }
        private readonly HashSet<Vector2Int> cells;
        private readonly IReadOnlyList<Vector2Int> cellList;
        private PreparedContents contents;
        private BackpackContainerData(string id, ContainerKind kind, Sprite icon, List<Vector2Int> validCells, bool rejectContainers)
        {
            Id = id; Kind = kind; Icon = icon; RejectContainers = rejectContainers;
            cells = new HashSet<Vector2Int>(validCells); cellList = Array.AsReadOnly(validCells.ToArray());
        }
        public string Id { get; }
        public ContainerKind Kind { get; }
        public Sprite Icon { get; }
        public bool RejectContainers { get; }
        public int Revision { get; private set; }
        public IReadOnlyList<Vector2Int> ValidCells => cellList;
        public IReadOnlyList<ItemDisplayData> Items => contents.Items;
        public bool IsValidCell(Vector2Int coordinate) => cells.Contains(coordinate);
        public bool TryGetItem(string id, out ItemDisplayData item)
        { item = null; return id != null && contents.ById.TryGetValue(id, out item); }
        public bool TryGetItemAt(Vector2Int coordinate, out ItemDisplayData item)
        { item = null; return contents.Occupied.TryGetValue(coordinate, out string id) && contents.ById.TryGetValue(id, out item); }
        public ContainerSnapshot ToSnapshot() => new ContainerSnapshot(Id, Kind, Icon, Items, ValidCells,
            revision: Revision, rejectContainers: RejectContainers);
        public static bool TryCreate(string id, ContainerKind kind, Sprite icon,
            IEnumerable<Vector2Int> validCells, IEnumerable<ItemDisplayData> items,
            out BackpackContainerData data, out string error, bool rejectContainers = true)
        {
            data = null; error = null;
            if (string.IsNullOrWhiteSpace(id) || (int)kind < (int)ContainerKind.None || (int)kind > (int)ContainerKind.SafeBox)
                return Fail("容器编号或类别无效。", out error);
            var shape = new List<Vector2Int>(); var unique = new HashSet<Vector2Int>();
            if (validCells == null) return Fail("未提供容器有效格。", out error);
            foreach (Vector2Int cell in validCells)
            {
                if (!IsSupportedCoordinate(cell) || !unique.Add(cell))
                    return Fail("容器有效格坐标无效或重复；列坐标必须在 0 到 4 之间。", out error);
                shape.Add(cell);
            }
            var candidate = new BackpackContainerData(id, kind, icon, shape, rejectContainers);
            if (!candidate.TryPrepare(items, out PreparedContents prepared, out error)) return false;
            candidate.contents = prepared; data = candidate; return true;
        }
        internal bool TryPrepare(IEnumerable<ItemDisplayData> items, out PreparedContents prepared, out string error)
        {
            var ordered = new List<ItemDisplayData>(items ?? Array.Empty<ItemDisplayData>());
            prepared = null;
            var snapshot = new ContainerSnapshot(Id, Kind, Icon, validCells: ValidCells, rejectContainers: RejectContainers);
            if (!LoadoutOperations.Validate(snapshot, ordered, out error)) return false;
            prepared = new PreparedContents();
            foreach (ItemDisplayData item in ordered)
            {
                prepared.ById.Add(item.Id, item);
                for (int y = 0; y < item.Size.y; y++)
                for (int x = 0; x < item.Size.x; x++) prepared.Occupied.Add(item.Origin + new Vector2Int(x, y), item.Id);
            }
            prepared.Items = Array.AsReadOnly(ordered.ToArray()); return true;
        }
        internal void Commit(PreparedContents prepared) { contents = prepared; Revision++; }
        internal static ItemDisplayData At(ItemDisplayData item, Vector2Int origin) => item.At(origin);
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
