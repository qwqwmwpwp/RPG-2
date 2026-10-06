using System;
using UnityEngine;

namespace RPG4.Backpack.Core
{
    /// <summary>每帧网格悬停参数：由鼠标位置推导的格坐标、线性索引与象限；唯一每帧缓存，交互分支只读它与其派生的空间查询结果。</summary>
    [Serializable]
    public struct TileParams : IEquatable<TileParams>
    {
        public const int NoIndex = -1; // 无效索引。

        [SerializeField]
        private GridCoord tileCoordinates;

        [SerializeField]
        private int tileIndex;

        [SerializeField]
        private TileQuadrant tileQuadrant;

        /// <summary>按格坐标、线性索引与象限创建悬停参数。</summary>
        public TileParams(GridCoord tileCoordinates, int tileIndex, TileQuadrant tileQuadrant)
        {
            this.tileCoordinates = tileCoordinates;
            this.tileIndex = tileIndex;
            this.tileQuadrant = tileQuadrant;
        }

        /// <summary>所在格坐标；未命中任何格时为 <see cref="GridCoord.Invalid"/>。</summary>
        public GridCoord TileCoordinates
        {
            get => tileCoordinates;
            set => tileCoordinates = value;
        }

        /// <summary>所在格的线性索引（行优先）；未命中时为 <see cref="NoIndex"/>。</summary>
        public int TileIndex
        {
            get => tileIndex;
            set => tileIndex = value;
        }

        /// <summary>格内象限；未命中时为 <see cref="TileQuadrant.None"/>。</summary>
        public TileQuadrant Quadrant
        {
            get => tileQuadrant;
            set => tileQuadrant = value;
        }

        public static TileParams Empty => new TileParams(GridCoord.Invalid, NoIndex, TileQuadrant.None); // 未命中任何格的空参数。

        public bool IsValid => tileIndex >= 0 && tileQuadrant != TileQuadrant.None && tileCoordinates.IsValid; // 坐标合法、索引有效且象限已判定。

        /// <summary>复位回 <see cref="Empty"/>（鼠标离开网格时调用）。</summary>
        public void Reset()
        {
            tileCoordinates = GridCoord.Invalid;
            tileIndex = NoIndex;
            tileQuadrant = TileQuadrant.None;
        }

        /// <summary>全字段比较，用于「本帧参数 vs 上帧参数」的变更判定。</summary>
        public static bool operator ==(TileParams left, TileParams right) =>
            left.tileCoordinates == right.tileCoordinates
            && left.tileIndex == right.tileIndex
            && left.tileQuadrant == right.tileQuadrant;

        /// <summary>任一字段不同即为不等。</summary>
        public static bool operator !=(TileParams left, TileParams right) => !(left == right);

        /// <inheritdoc/>
        public bool Equals(TileParams other) => this == other;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is TileParams other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = tileCoordinates.GetHashCode();
                hashCode = (hashCode * 397) ^ tileIndex;
                hashCode = (hashCode * 397) ^ (int)tileQuadrant;
                return hashCode;
            }
        }

        /// <summary>调试文本：坐标、索引与象限。</summary>
        public override string ToString() =>
            $"TileParams(coord={tileCoordinates}, index={tileIndex}, quadrant={tileQuadrant})";
    }
}
