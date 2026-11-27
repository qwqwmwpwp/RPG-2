using UnityEngine;
using UnityEngine.UI;

namespace RPG4.LoadoutTemplate
{
    /// <summary>按容器当前接线和格子尺寸完成坐标换算、矩形定位和面板尺寸计算。</summary>
    internal readonly struct ContainerGridLayout
    {
        internal readonly RectTransform GridRoot;
        internal readonly RectTransform ItemLayer;
        internal readonly Vector2 CellSize;
        private readonly RectTransform bodyRoot;
        private readonly float minimumBodyHeight;
        private readonly float headerHeight;
        private readonly Vector2 spacing;

        /// <summary>读取本次布局操作使用的 Inspector 引用和尺寸。</summary>
        internal ContainerGridLayout(RectTransform gridRoot, RectTransform itemLayer, RectTransform bodyRoot,
            float minimumBodyHeight, float headerHeight, Vector2 cellSize, Vector2 spacing)
        {
            GridRoot = gridRoot;
            ItemLayer = itemLayer;
            this.bodyRoot = bodyRoot;
            this.minimumBodyHeight = minimumBodyHeight;
            this.headerHeight = headerHeight;
            CellSize = cellSize;
            this.spacing = spacing;
        }

        /// <summary>验证网格接线、手动布局模式和尺寸，拒绝无法按坐标定位的布局。</summary>
        internal bool Validate(out string error)
        {
            error = null;
            if (GridRoot == null || ItemLayer == null || ItemLayer == GridRoot)
                error = "请接线格子根节点和独立物品层。";
            else if (ItemLayer.parent != GridRoot)
                error = "物品层必须位于格子根节点下，共用左上原点。";
            else if (GridRoot.TryGetComponent(out LayoutGroup layout) && layout.enabled)
                error = "格子根节点不能启用自动布局；有效格须由显式坐标定位。";
            else if (!FinitePositive(CellSize.x) || !FinitePositive(CellSize.y)
                || !FinitePositive(CellSize.x + spacing.x) || !FinitePositive(CellSize.y + spacing.y))
                error = "格子尺寸和包含间距的步距必须为有限正数。";
            else if (bodyRoot != null && (!FiniteNonNegative(minimumBodyHeight) || !FiniteNonNegative(headerHeight)))
                error = "面板最小高度和标题高度必须为有限非负数。";
            return error == null;
        }

        /// <summary>将屏幕点映射到左上原点的格坐标，格子间隙不参与命中。</summary>
        internal bool TryResolveCell(Vector2 screen, Camera camera, out Vector2Int coordinate)
        {
            coordinate = default;
            if (GridRoot == null
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(GridRoot, screen, camera, out Vector2 local)) return false;
            float x = local.x - GridRoot.rect.xMin, y = GridRoot.rect.yMax - local.y;
            if (!FiniteNonNegative(x) || !FiniteNonNegative(y)) return false;
            float pitchX = CellSize.x + spacing.x, pitchY = CellSize.y + spacing.y;
            if (x / pitchX >= int.MaxValue || y / pitchY >= int.MaxValue) return false;
            int column = Mathf.FloorToInt(x / pitchX), row = Mathf.FloorToInt(y / pitchY);
            if (x - column * pitchX >= CellSize.x || y - row * pitchY >= CellSize.y) return false;
            coordinate = new Vector2Int(column, row);
            return true;
        }

        /// <summary>计算覆盖指定列数和行数的像素尺寸，包含内部间距。</summary>
        internal Vector2 GetItemPixelSize(Vector2Int size) => new Vector2(
            size.x < 1 ? 0f : CellSize.x * size.x + spacing.x * (size.x - 1),
            size.y < 1 ? 0f : CellSize.y * size.y + spacing.y * (size.y - 1));

        /// <summary>将网格坐标转换为左上锚点下的像素偏移。</summary>
        internal Vector2 ToPixel(Vector2Int coordinate) => new Vector2(
            coordinate.x * (CellSize.x + spacing.x), -coordinate.y * (CellSize.y + spacing.y));

        /// <summary>将矩形统一定位到左上原点，并恢复标准缩放和朝向。</summary>
        internal static void SetRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
        }

        /// <summary>根据网格和容器槽的底边更新网格、主体和面板高度。</summary>
        internal void ApplyExtent(ContainerUI owner, Vector2 extent, Vector3[] extentCorners)
        {
            GridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, extent.x);
            GridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, extent.y);
            if (bodyRoot == null || bodyRoot.parent != owner.transform || !(owner.transform is RectTransform panel)
                || !GridRoot.IsChildOf(bodyRoot)) return;

            // 面板高度跟随内容；面板与 Body 的宽度仍由外部布局维护。
            float bodyHeight = Mathf.Max(minimumBodyHeight, BottomExtent(GridRoot, extentCorners));
            RectTransform slotRect = owner.Slot != null ? owner.Slot.RectTransform : null;
            if (slotRect != null && slotRect.IsChildOf(bodyRoot)) bodyHeight = Mathf.Max(bodyHeight, BottomExtent(slotRect, extentCorners));
            bodyRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, bodyHeight);
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, headerHeight + bodyHeight);
        }

        /// <summary>计算矩形最低角相对于主体顶部的距离。</summary>
        private float BottomExtent(RectTransform rect, Vector3[] extentCorners)
        {
            rect.GetWorldCorners(extentCorners);
            float extent = 0f;
            foreach (Vector3 corner in extentCorners)
                extent = Mathf.Max(extent, bodyRoot.rect.yMax - bodyRoot.InverseTransformPoint(corner).y);
            return extent;
        }

        /// <summary>判断数值是否为可用的非负距离。</summary>
        private static bool FiniteNonNegative(float value) => value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        /// <summary>判断数值是否为有限正长度。</summary>
        private static bool FinitePositive(float value) => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
