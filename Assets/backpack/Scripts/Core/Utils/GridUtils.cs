using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.Backpack.Core
{
    /// <summary>网格工具：Index↔Position 换算（<c>Y * Columns + X</c> / <c>(Index % Columns, Index / Columns)</c>）、Bounds 判定与 Clamp、行优先二维遍历；零状态零业务，只用 <see cref="GridCoord"/> 一套坐标口径。</summary>
    public static class GridUtils
    {
        // ① Index ↔ Position 换算

        /// <summary>网格坐标 → 线性索引：<c>Position.Y * Columns + Position.X</c>；列数必须大于 0。</summary>
        public static int GetIndexFromPosition(GridCoord coord, int columns)
        {
            return coord.Y * columns + coord.X;
        }

        /// <summary>线性索引 → 网格坐标：<c>(Index % Columns, Index / Columns)</c>；列数必须大于 0。</summary>
        public static GridCoord GetPositionFromIndex(int index, int columns)
        {
            return new GridCoord(index % columns, index / columns);
        }

        // ② Bounds 判定与 Clamp

        /// <summary>以「起点索引 + 物品尺寸」推右上角边界，判断物品是否完全落在网格内（横纵均不越界）。</summary>
        public static bool IsInGridBounds(int startIndex, GridCoord itemDimensions, int columns, int rows)
        {
            if (columns <= 0 || rows <= 0) return false;
            if (startIndex < 0 || startIndex >= columns * rows) return false;

            int endColumn = (startIndex % columns) + itemDimensions.X;
            int endRow = (startIndex / columns) + itemDimensions.Y;
            return endColumn <= columns && endRow <= rows;
        }

        /// <summary>点是否落在矩形界线内；闭区间，四条边上的点视为在内。</summary>
        public static bool IsWithinBounds(Vector2 boundaryPos, Vector2 widgetSize, Vector2 point)
        {
            return point.x >= boundaryPos.x && point.x <= boundaryPos.x + widgetSize.x &&
                   point.y >= boundaryPos.y && point.y <= boundaryPos.y + widgetSize.y;
        }

        /// <summary>弹窗类控件的回折定位：右 / 下越界则整体左移 / 上移一个控件宽高，最后对 0 取 Max 防止超出左上角；不是 <see cref="Mathf.Clamp(float, float, float)"/> 式的逐轴夹取，勿替换。</summary>
        public static Vector2 GetClampedWidgetPosition(Vector2 boundary, Vector2 widgetSize, Vector2 point)
        {
            Vector2 clampedPos = point;

            if (point.x + widgetSize.x > boundary.x)
            {
                clampedPos.x = point.x - widgetSize.x;
            }
            if (point.y + widgetSize.y > boundary.y)
            {
                clampedPos.y = point.y - widgetSize.y;
            }

            clampedPos.x = Mathf.Max(clampedPos.x, 0f);
            clampedPos.y = Mathf.Max(clampedPos.y, 0f);

            return clampedPos;
        }

        /// <summary>网格坐标钳制到 <c>[0, columns) × [0, rows)</c> 内。</summary>
        public static GridCoord ClampToGrid(GridCoord coord, int columns, int rows)
        {
            if (columns <= 0 || rows <= 0) return GridCoord.Invalid;

            return new GridCoord(
                Mathf.Clamp(coord.X, 0, columns - 1),
                Mathf.Clamp(coord.Y, 0, rows - 1));
        }

        // ③ 二维遍历（行优先）

        /// <summary>全网格遍历，行优先（外层 Row、内层 Column），索引 <c>Row * Columns + Column</c>。</summary>
        public static void ForEach2D(int columns, int rows, Action<int, int, int> action)
        {
            if (action == null) return;

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    action(row, column, row * columns + column);
                }
            }
        }

        /// <summary>区域遍历：以起点坐标 + 尺寸为界，行优先。越界保护只判定容器索引有效性（<c>index &gt;= 0 &amp;&amp; index &lt; slots.Count</c>），不做「列溢出回绕到下一行」的行边界判定，调用方需先用 <see cref="IsInGridBounds(int, GridCoord, int, int)"/> 守卫。</summary>
        public static void ForEach2D<T>(IList<T> slots, int startIndex, GridCoord dimensions, int columns, Action<T> action)
        {
            if (slots == null || action == null) return;
            if (columns <= 0) return;

            GridCoord origin = GetPositionFromIndex(startIndex, columns);
            if (!origin.IsValid) return;

            for (int j = 0; j < dimensions.Y; j++)
            {
                for (int i = 0; i < dimensions.X; i++)
                {
                    int index = GetIndexFromPosition(origin + new GridCoord(i, j), columns);
                    if (index >= 0 && index < slots.Count)
                    {
                        action(slots[index]);
                    }
                }
            }
        }

        /// <summary>区域遍历的「索引 + 坐标」版本，行优先输出 <c>(index, coord)</c>；无容器故不做越界裁剪，调用方需自行用 <see cref="IsInGridBounds(int, GridCoord, int, int)"/> 守卫。</summary>
        public static void ForEach2D(int startIndex, GridCoord dimensions, int columns, Action<int, GridCoord> action)
        {
            if (action == null) return;
            if (columns <= 0) return;

            GridCoord origin = GetPositionFromIndex(startIndex, columns);
            if (!origin.IsValid) return;

            for (int j = 0; j < dimensions.Y; j++)
            {
                for (int i = 0; i < dimensions.X; i++)
                {
                    GridCoord coord = origin + new GridCoord(i, j);
                    action(GetIndexFromPosition(coord, columns), coord);
                }
            }
        }
    }
}
