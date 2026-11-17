using System;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>外部容器配置。改变布局时仍须能容纳当前显示物品；配置本身不会搬动物品。</summary>
    [Serializable]
    public sealed class ContainerUIConfiguration
    {
        public string Id = "backpack";
        public ContainerKind Kind = ContainerKind.Backpack;
        public ContainerLayoutMode LayoutMode = ContainerLayoutMode.Rectangle;
        public Vector2Int GridSize = new Vector2Int(BackpackContainerData.MaxColumns, 3);
        public Vector2Int[] ValidCells = Array.Empty<Vector2Int>();
        public Vector2 CellSize = new Vector2(70f, 70f);
        public Vector2 Spacing;
    }
}
