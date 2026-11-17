using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RPG4.LoadoutTemplate
{
    /// <summary>有效格子的坐标与反馈视图，兼容空格滚动输入；物品拿取由物品视图发起。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform), typeof(Image))]
    public sealed class CellUI : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IBeginDragHandler,
        IDragHandler, IEndDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Vector2Int coordinate;
        [SerializeField] private Image background;
        [SerializeField] private Image highlight;
        [SerializeField] private Color hoveredColor = new Color(0.65f, 0.8f, 1f, 0.25f);
        [SerializeField] private Color allowedColor = new Color(0.2f, 0.85f, 0.45f, 0.45f);
        [SerializeField] private Color blockedColor = new Color(1f, 0.25f, 0.25f, 0.45f);
        private RectTransform rect;
        private Color normalColor;
        private bool cachedColor;
        private ScrollRect scroll;
        private int scrollPointerId = int.MinValue;
        private PointerEventData lastScrollPointer;

        public Vector2Int Coordinate => coordinate;
        public ContainerUI Owner { get; private set; }
        public RectTransform RectTransform => rect != null ? rect : (rect = (RectTransform)transform);

        private void Awake()
        {
            CacheColor();
            if (background != null) background.raycastTarget = true;
            if (highlight != null) highlight.raycastTarget = false;
            SetFeedback(CellFeedback.Normal);
        }

        private void OnDisable()
        {
            if (scroll != null && lastScrollPointer != null) scroll.OnEndDrag(lastScrollPointer);
            scroll = null;
            lastScrollPointer = null;
            scrollPointerId = int.MinValue;
        }

        public void Bind(ContainerUI owner, Vector2Int value)
        {
            Owner = owner;
            coordinate = value;
            SetFeedback(CellFeedback.Normal);
        }

        public void SetFeedback(CellFeedback feedback)
        {
            CacheColor();
            Color color = feedback == CellFeedback.Allowed ? allowedColor
                : feedback == CellFeedback.Blocked ? blockedColor : hoveredColor;
            if (highlight != null)
            {
                highlight.enabled = feedback != CellFeedback.Normal;
                highlight.color = color;
            }
            else if (background != null)
                background.color = feedback == CellFeedback.Normal ? normalColor : color;
        }

        private void CacheColor()
        {
            if (cachedColor || background == null) return;
            normalColor = background.color;
            cachedColor = true;
        }

        public void OnPointerDown(PointerEventData e) { }
        public void OnPointerUp(PointerEventData e) { }
        public void OnBeginDrag(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left) return;
            scroll = GetComponentInParent<ScrollRect>();
            if (scroll == null || !scroll.isActiveAndEnabled) { scroll = null; return; }
            scrollPointerId = e.pointerId;
            lastScrollPointer = e;
            scroll.OnBeginDrag(e);
        }
        public void OnDrag(PointerEventData e)
        {
            if (scroll == null || e.pointerId != scrollPointerId) return;
            lastScrollPointer = e;
            scroll.OnDrag(e);
        }
        public void OnEndDrag(PointerEventData e)
        {
            if (scroll == null || e.pointerId != scrollPointerId) return;
            scroll.OnEndDrag(e);
            scroll = null;
            lastScrollPointer = null;
            scrollPointerId = int.MinValue;
        }
        public void OnPointerEnter(PointerEventData e)
        {
            if (Owner == null || !Owner.HasPlacementPreview) SetFeedback(CellFeedback.Hovered);
        }
        public void OnPointerExit(PointerEventData e)
        {
            if (Owner == null || !Owner.HasPlacementPreview) SetFeedback(CellFeedback.Normal);
        }
    }
}
