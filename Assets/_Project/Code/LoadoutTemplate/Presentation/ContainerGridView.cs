using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>管理显示格子池和物品视图，按已通过校验的投影刷新布局与放置反馈。</summary>
    internal sealed class ContainerGridView
    {
        private readonly List<Vector2Int> authoredCoordinates = new List<Vector2Int>();
        private readonly List<CellUI> cellPool = new List<CellUI>();
        private readonly Dictionary<Vector2Int, CellUI> cellViews = new Dictionary<Vector2Int, CellUI>();
        private readonly Dictionary<string, ItemViewUI> itemViews = new Dictionary<string, ItemViewUI>(StringComparer.Ordinal);
        private readonly Vector3[] extentCorners = new Vector3[4];

        internal IReadOnlyList<Vector2Int> AuthoredCoordinates => authoredCoordinates;
        internal int CellCount => cellPool.Count;
        internal bool HasPlacementPreview { get; private set; }

        /// <summary>首次配置时从可复用的预置格子读取原始坐标，重复或失效的引用跳过。</summary>
        internal void CollectAuthoredCells(ContainerUI owner, RectTransform root, CellUI[] cells)
        {
            var references = new HashSet<CellUI>();
            foreach (CellUI cell in cells ?? Array.Empty<CellUI>())
            {
                if (cell == null || cell.RectTransform == null || cell.transform.parent != root
                    || (cell.Owner != null && cell.Owner != owner) || !references.Add(cell)) continue;
                authoredCoordinates.Add(cell.Coordinate);
                cellPool.Add(cell);
            }
        }

        /// <summary>取消各物品视图正在进行的交互。</summary>
        internal void CancelInteractions()
        {
            foreach (ItemViewUI view in itemViews.Values)
                if (view != null) view.CancelInteraction();
        }

        /// <summary>隐藏格子和物品，保留池和物品视图供下次绑定复用。</summary>
        internal void Hide(RectTransform gridRoot, CellUI[] cells)
        {
            foreach (ItemViewUI view in itemViews.Values)
                if (view != null) view.gameObject.SetActive(false);
            foreach (CellUI cell in cellPool)
                if (cell != null) cell.gameObject.SetActive(false);
            foreach (CellUI cell in cells ?? Array.Empty<CellUI>())
                if (cell != null && cell.transform.parent == gridRoot) cell.gameObject.SetActive(false);
            cellViews.Clear();
        }

        /// <summary>先绑定有效格坐标与尺寸，再将物品视图放到对应占格。</summary>
        internal void Render(ContainerUI owner, ContainerGridLayout layout, ContainerDisplayProjection projection,
            CellUI cellPrefab, ItemViewUI itemPrefab)
        {
            ApplyShape(owner, layout, projection.Coordinates, cellPrefab);
            RefreshItems(owner, layout, projection.Items, itemPrefab);
        }

        /// <summary>按快照顺序重新绑定格子池，并更新网格与主体尺寸。</summary>
        private void ApplyShape(ContainerUI owner, ContainerGridLayout layout, IReadOnlyList<Vector2Int> shape, CellUI cellPrefab)
        {
            while (cellPool.Count < shape.Count)
                cellPool.Add(UnityEngine.Object.Instantiate(cellPrefab, layout.GridRoot));
            cellViews.Clear();
            int width = 0, height = 0;
            for (int i = 0; i < cellPool.Count; i++)
            {
                CellUI cell = cellPool[i];
                bool active = i < shape.Count;
                if (active)
                {
                    Vector2Int coordinate = shape[i];
                    cell.Bind(owner, coordinate);
                    ContainerGridLayout.SetRect(cell.RectTransform, layout.ToPixel(coordinate), layout.CellSize);
                    cell.SetFeedback(CellFeedback.Normal);
                    cellViews.Add(coordinate, cell);
                    width = Math.Max(width, coordinate.x + 1);
                    height = Math.Max(height, coordinate.y + 1);
                }
                if (cell.gameObject.activeSelf != active) cell.gameObject.SetActive(active);
            }
            Vector2 extent = layout.GetItemPixelSize(new Vector2Int(width, height));
            ContainerGridLayout.SetRect(layout.ItemLayer, Vector2.zero, extent);
            layout.ApplyExtent(owner, extent, extentCorners);
            layout.ItemLayer.SetAsLastSibling();
        }

        /// <summary>复用当前快照仍包含的物品视图，并移除已消失的编号。</summary>
        private void RefreshItems(ContainerUI owner, ContainerGridLayout layout,
            IReadOnlyDictionary<string, ItemDisplaySnapshot> items, ItemViewUI itemPrefab)
        {
            var removed = new List<string>();
            foreach (KeyValuePair<string, ItemViewUI> entry in itemViews)
                if (!items.ContainsKey(entry.Key)) removed.Add(entry.Key);
            foreach (string id in removed)
            {
                if (itemViews[id] != null)
                {
                    itemViews[id].gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(itemViews[id].gameObject);
                }
                itemViews.Remove(id);
            }
            foreach (ItemDisplaySnapshot item in items.Values)
            {
                if (!itemViews.TryGetValue(item.Id, out ItemViewUI view) || view == null)
                {
                    view = UnityEngine.Object.Instantiate(itemPrefab, layout.ItemLayer);
                    itemViews[item.Id] = view;
                }
                PlaceItem(owner, layout, view, item);
                view.SetDimmed(false);
                if (!view.gameObject.activeSelf) view.gameObject.SetActive(true);
            }
        }

        /// <summary>查找指定编号已有的物品视图。</summary>
        internal bool TryGetItemView(string id, out ItemViewUI view)
        {
            view = null;
            return id != null && itemViews.TryGetValue(id, out view) && view != null;
        }

        /// <summary>用当前投影恢复拖拽物品的父节点、位置、朝向和交互状态。</summary>
        internal void RestoreItem(ContainerUI owner, ContainerGridLayout layout, ItemDisplaySnapshot item)
        {
            if (!TryGetItemView(item.Id, out ItemViewUI view)) return;
            PlaceItem(owner, layout, view, item);
            view.SetDragState(false);
            view.SetDimmed(false);
        }

        /// <summary>共用快照的尺寸和朝向，将物品定位到物品层。</summary>
        private static void PlaceItem(ContainerUI owner, ContainerGridLayout layout, ItemViewUI view, ItemDisplaySnapshot item)
        {
            if (view.transform.parent != layout.ItemLayer) view.transform.SetParent(layout.ItemLayer, false);
            Vector2 size = layout.GetItemPixelSize(item.Size);
            ContainerGridLayout.SetRect(view.RectTransform, layout.ToPixel(item.Origin), size);
            view.Bind(owner, item);
            view.Apply(item, size);
        }

        /// <summary>在已实例化的有效格上显示放置结果。</summary>
        internal void ShowPlacement(PlacementPreview preview)
        {
            ClearFeedback();
            HasPlacementPreview = true;
            // 只遍历已存在的有效格，空洞既不生成格子，也不成为射线目标。
            foreach (KeyValuePair<Vector2Int, CellUI> entry in cellViews)
            {
                long x = (long)entry.Key.x - preview.Origin.x;
                long y = (long)entry.Key.y - preview.Origin.y;
                if (x >= 0 && y >= 0 && x < preview.Size.x && y < preview.Size.y)
                    entry.Value.SetFeedback(preview.Allowed ? CellFeedback.Allowed : CellFeedback.Blocked);
            }
        }

        /// <summary>清除上一次放置预览的格子反馈。</summary>
        internal void ClearFeedback()
        {
            HasPlacementPreview = false;
            foreach (CellUI cell in cellViews.Values) if (cell != null) cell.SetFeedback(CellFeedback.Normal);
        }
    }
}
