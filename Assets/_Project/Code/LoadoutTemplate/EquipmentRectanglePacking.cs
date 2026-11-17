using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>最多 256 件物品、最多 5 列的精确正交装箱。按面积升序穷举矩形和 90 度朝向，无近似结果。
    /// 正规布局的坐标是其他矩形边长的子集和，避免按每一个空格扫描。
    /// 搜索预算耗尽明确失败，不把当前上界伪装成最小面积。</summary>
    internal static class EquipmentRectanglePacking
    {
        private const int MaxNodes = 2000000;
        private sealed class Box { internal int width, height; internal long Area => (long)width * height; }
        internal static bool TryPack(List<ItemDisplayData> input, out Vector2Int size, out List<ItemDisplayData> result, out string error)
        {
            size = Vector2Int.zero; result = new List<ItemDisplayData>(); error = null;
            if (input.Count == 0) return true;
            if (input.Count > 256) { error = "临时网格最多支持 256 件物品。"; return false; }
            int area = 0, stackWidth = 0, stackHeight = 0;
            foreach (ItemDisplayData item in input)
            {
                Vector2Int s = item.OriginalSize;
                if (s.x < 1 || s.y < 1 || s.x > 64 || s.y > 64 || (long)s.x * s.y + area > 1024)
                { error = "装备尺寸超出装箱支持范围（单边 64 格、总面积 1024 格）。"; return false; }
                int narrowSide = Math.Min(s.x, s.y);
                if (narrowSide > BackpackContainerData.MaxColumns)
                { error = "物品旋转后仍超过临时网格最多 5 列的宽度，未生成布局。"; return false; }
                area += s.x * s.y;
                stackWidth = Math.Max(stackWidth, narrowSide);
                stackHeight += Math.Max(s.x, s.y);
            }
            var ordered = new List<ItemDisplayData>(input);
            ordered.Sort((a, b) => { int c = ((long)b.Size.x * b.Size.y).CompareTo((long)a.Size.x * a.Size.y); return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id); });
            int nodes = 0;
            var sums = new HashSet<int> { 0 };
            foreach (ItemDisplayData item in ordered)
            {
                var before = new List<int>(sums);
                foreach (int value in before)
                {
                    if (++nodes >= MaxNodes) { error = "精确装箱坐标生成达到预算，未生成布局。"; return false; }
                    if (value + item.OriginalSize.x <= 1024) sums.Add(value + item.OriginalSize.x);
                    if (value + item.OriginalSize.y <= 1024) sums.Add(value + item.OriginalSize.y);
                }
            }
            var coordinates = new List<int>(sums); coordinates.Sort();
            // 每件物品以短边作宽竖直堆叠，得到满足列限制的可行上界。
            // 协议最多 1024 格；宽为 w 的候选高度至多 upper / w。
            // 宽高不可互换：5x2 这样的横矩形同样必须枚举。
            var boxes = new List<Box>(); long upper = Math.Min(1024L, (long)stackWidth * stackHeight);
            for (int w = 1; w <= BackpackContainerData.MaxColumns; w++)
            foreach (int h in coordinates)
            {
                if (++nodes >= MaxNodes) { error = "精确装箱候选枚举达到预算，未生成布局。"; return false; }
                if (h > upper / w) break;
                if (h > 0 && (long)w * h >= area)
                    boxes.Add(new Box { width = w, height = h });
            }
            boxes.Sort((a, b) =>
            {
                int c = a.Area.CompareTo(b.Area);
                if (c != 0) return c;
                c = Math.Abs(a.height - a.width).CompareTo(Math.Abs(b.height - b.width));
                return c != 0 ? c : a.width.CompareTo(b.width);
            });
            foreach (Box box in boxes)
            {
                var placed = new List<ItemDisplayData>();
                if (Search(ordered, coordinates, box, placed, ref nodes))
                { size = new Vector2Int(box.width, box.height); result = placed; return true; }
                if (nodes >= MaxNodes) { error = "精确装箱达到搜索预算；未提交转移或生成会话，不返回近似布局。"; return false; }
            }
            error = "装备无法在最多 5 列、1024 格范围内生成合法临时矩形。"; return false;
        }
        private static bool Search(List<ItemDisplayData> items, List<int> coordinates, Box box, List<ItemDisplayData> placed, ref int nodes)
        {
            if (placed.Count == items.Count) return true;
            ItemDisplayData item = items[placed.Count];
            for (int rotation = 0; rotation < (item.OriginalSize.x == item.OriginalSize.y ? 1 : 2); rotation++)
            {
                ItemDisplayData rotated = item.WithRotation(rotation != 0); Vector2Int size = rotated.Size;
                if (size.x > box.width || size.y > box.height) continue;
                foreach (int y in coordinates)
                {
                    if (y + size.y > box.height) break;
                    foreach (int x in coordinates)
                    {
                        if (x + size.x > box.width) break;
                        if (++nodes >= MaxNodes) return false;
                        bool free = true;
                        foreach (ItemDisplayData other in placed)
                            if (x < other.Origin.x + other.Size.x && other.Origin.x < x + size.x
                                && y < other.Origin.y + other.Size.y && other.Origin.y < y + size.y) { free = false; break; }
                        if (!free) continue;
                        placed.Add(rotated.At(new Vector2Int(x, y)));
                        if (Search(items, coordinates, box, placed, ref nodes)) return true;
                        placed.RemoveAt(placed.Count - 1);
                        if (nodes >= MaxNodes) return false;
                    }
                }
            }
            return false;
        }
    }
}
