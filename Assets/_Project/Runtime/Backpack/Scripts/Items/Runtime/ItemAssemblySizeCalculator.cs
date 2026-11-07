using System;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    public static class ItemAssemblySizeCalculator
    {
        public static GridCoord GetDimensions(RuntimeItem root, ItemId parentItemId = default, string slotId = null, RuntimeItem replacement = null)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            GridFragment grid = root.GetFragment<GridFragment>();
            if (slotId == null && root.Assembly.InstalledChildren.Count == 0)
                return new GridCoord(grid?.Width ?? 1, grid?.Height ?? 1);
            int left = 0, right = 0, top = 0, bottom = 0;
            long addLeft = 0, addRight = 0, addTop = 0, addBottom = 0;
            foreach (RuntimeItem item in ItemAssemblyTraversal.EnumerateReplacement(root, parentItemId, slotId, replacement))
            {
                if (ReferenceEquals(item, root)) continue;
                AssemblySizeDefinitionFragment size = item.GetFragment<AssemblySizeDefinitionFragment>();
                if (size == null) continue;
                if (size.ForceAdditive)
                { addLeft += size.Left; addRight += size.Right; addTop += size.Top; addBottom += size.Bottom; }
                else
                { left = Math.Max(left, size.Left); right = Math.Max(right, size.Right); top = Math.Max(top, size.Top); bottom = Math.Max(bottom, size.Bottom); }
            }
            long width = (long)(grid?.Width ?? 1) + left + right + addLeft + addRight;
            long height = (long)(grid?.Height ?? 1) + top + bottom + addTop + addBottom;
            if (width > int.MaxValue || height > int.MaxValue) throw new InvalidOperationException("组合占格尺寸超限。");
            return new GridCoord((int)width, (int)height);
        }
    }
}
