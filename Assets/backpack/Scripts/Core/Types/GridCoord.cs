using System;
using UnityEngine;

namespace RPG4.Backpack.Core
{
    /// <summary>网格坐标：单位为「格」，原点在网格左上角，X 向右、Y 向下递增；不承担 Index↔Position 换算，换算统一由 <see cref="GridUtils"/> 承担。</summary>
    [Serializable]
    public struct GridCoord : IEquatable<GridCoord>
    {
        public static readonly GridCoord Invalid = new GridCoord(-1, -1); // 无效坐标（两轴均为 -1）。

        public static readonly GridCoord Zero = new GridCoord(0, 0); // 原点。

        public static readonly GridCoord One = new GridCoord(1, 1); // 单位格，可直接用作尺寸语义。

        [SerializeField]
        private int x;

        [SerializeField]
        private int y;

        /// <summary>按列、行创建坐标。</summary>
        public GridCoord(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        /// <summary>按 <see cref="Vector2Int"/> 的 x、y 创建坐标。</summary>
        public GridCoord(Vector2Int value)
        {
            x = value.x;
            y = value.y;
        }

        /// <summary>列（水平方向，向右为正）。</summary>
        public int X
        {
            get => x;
            set => x = value;
        }

        /// <summary>行（垂直方向，向下为正）。</summary>
        public int Y
        {
            get => y;
            set => y = value;
        }

        public bool IsValid => x >= 0 && y >= 0; // 两轴均非负即合法。

        /// <summary>转为 <see cref="Vector2Int"/>。</summary>
        public Vector2Int ToVector2Int() => new Vector2Int(x, y);

        /// <summary>由 <see cref="Vector2Int"/> 构造坐标。</summary>
        public static GridCoord FromVector2Int(Vector2Int value) => new GridCoord(value.x, value.y);

        /// <summary>逐轴相加。</summary>
        public static GridCoord operator +(GridCoord left, GridCoord right) =>
            new GridCoord(left.x + right.x, left.y + right.y);

        /// <summary>逐轴相减。</summary>
        public static GridCoord operator -(GridCoord left, GridCoord right) =>
            new GridCoord(left.x - right.x, left.y - right.y);

        /// <summary>两轴均相等即视为相等。</summary>
        public static bool operator ==(GridCoord left, GridCoord right) => left.x == right.x && left.y == right.y;

        /// <summary>任一轴不等即为不等。</summary>
        public static bool operator !=(GridCoord left, GridCoord right) => !(left == right);

        /// <inheritdoc/>
        public bool Equals(GridCoord other) => this == other;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is GridCoord other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => (x * 397) ^ y;

        /// <summary>调试文本：(列, 行)。</summary>
        public override string ToString() => $"({x}, {y})";
    }
}
