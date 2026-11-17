using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RPG4.LoadoutTemplate
{
    /// <summary>物品显示与输入入口；绑定物品上下文后，将输入交给独立交换器解释。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform), typeof(CanvasGroup), typeof(Image))]
    public sealed class ItemViewUI : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Image background;
        [SerializeField] private Image icon;
        [SerializeField] private TMP_Text countLabel;
        [SerializeField] private TMP_Text nameLabel;
        [SerializeField] private CanvasGroup group;
        [SerializeField] private bool acceptsInput = true;
        [SerializeField] private Color selectedColor = new Color(0.3f, 0.55f, 0.8f, 1f);
        private RectTransform rect;
        private Color normalColor;
        private bool cachedColor, cachedIconMetrics;
        private Vector2 iconPadding;
        private bool dragging;
        private bool selected;
        private LoadoutDragVisual interaction;

        public ContainerUI Owner { get; private set; }
        public ItemDisplaySnapshot Data { get; private set; }
        public RectTransform RectTransform => rect != null ? rect : (rect = (RectTransform)transform);
        private void Awake()
        {
            CacheComponents();
            ConfigureRaycasts();
            RefreshSelection();
        }

        private void OnEnable() => BindInteraction();
        private void Update() => interaction?.CheckAvailability();

        public void CancelInteraction()
        {
            interaction?.Cancel();
            SetDragState(false);
            SetSelected(false);
        }

        private void OnDisable()
        {
            interaction?.Dispose();
            SetDragState(false);
            SetDimmed(false);
            SetSelected(false);
        }

        private void OnDestroy() => interaction?.Dispose();

        public void Bind(ContainerUI owner, ItemDisplaySnapshot value)
        {
            bool changed = Owner != owner || !MatchesCurrentItem(value);
            if (changed)
            {
                NotifyUnavailable();
                SetDragState(false);
                SetSelected(false);
            }
            Owner = owner;
            Data = value;
            BindInteraction();
            ConfigureRaycasts();
        }

        public void Apply(ItemDisplaySnapshot value, Vector2 pixelSize)
        {
            if (!MatchesCurrentItem(value)) NotifyUnavailable();
            Data = value;
            ApplyDragAppearance(value, pixelSize);
        }

        private bool MatchesCurrentItem(ItemDisplaySnapshot value) =>
            ReferenceEquals(Data, value) || Data != null && Data.HasSameSource(value);

        internal void ApplyDragAppearance(ItemDisplaySnapshot value, Vector2 pixelSize)
        {
            ConfigureRaycasts();
            if (!cachedIconMetrics && icon != null && icon.rectTransform != RectTransform)
            {
                iconPadding = RectTransform.rect.size - icon.rectTransform.rect.size;
                cachedIconMetrics = true;
            }
            RectTransform.sizeDelta = pixelSize;
            if (icon != null)
            {
                icon.sprite = value?.Icon;
                icon.preserveAspect = true;
                icon.enabled = value?.Icon != null;
                if (icon.rectTransform != RectTransform)
                {
                    Vector2 available = Vector2.Max(Vector2.zero, pixelSize - iconPadding);
                    icon.rectTransform.localRotation = Quaternion.Euler(0f, 0f, value != null && value.Rotated ? -90f : 0f);
                    icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = icon.rectTransform.pivot = new Vector2(.5f, .5f);
                    icon.rectTransform.anchoredPosition = Vector2.zero;
                    icon.rectTransform.sizeDelta = value != null && value.Rotated ? new Vector2(available.y, available.x) : available;
                }
            }
            if (countLabel != null) countLabel.text = value != null && value.Count > 1 ? value.Count.ToString() : string.Empty;
            if (nameLabel != null) nameLabel.text = value?.Name ?? string.Empty;
        }

        /// <summary>源物品在拖动时穿透射线；展示用影子通过 acceptsInput 永久关闭输入。</summary>
        public void SetDragState(bool value)
        {
            dragging = value;
            ConfigureRaycasts();
        }

        public void SetDimmed(bool value)
        {
            CacheComponents();
            if (group != null) group.alpha = value ? 0.35f : 1f;
        }

        public void SetSelected(bool value)
        {
            selected = value;
            CacheComponents();
            RefreshSelection();
        }

        private void CacheComponents()
        {
            if (background == null) background = GetComponent<Image>();
            if (group == null) group = GetComponent<CanvasGroup>();
            if (!cachedColor && background != null)
            {
                normalColor = background.color;
                cachedColor = true;
            }
        }

        private void ConfigureRaycasts()
        {
            CacheComponents();
            bool receiveInput = acceptsInput && !dragging;
            if (group != null) { group.blocksRaycasts = receiveInput; group.interactable = receiveInput; }
            if (background != null) background.raycastTarget = receiveInput;
            if (icon != null) icon.raycastTarget = false;
            if (countLabel != null) countLabel.raycastTarget = false;
            if (nameLabel != null) nameLabel.raycastTarget = false;
        }

        private void RefreshSelection()
        {
            if (background != null) background.color = selected ? selectedColor : normalColor;
        }

        private void BindInteraction()
        {
            if (interaction == null)
                interaction = new LoadoutDragVisual(this, RectTransform,
                    () => Owner != null && Owner.IsReady ? Owner.Snapshot : null,
                    () => Data, SetDragState,
                    () => { if (Owner != null && Data != null) Owner.RestoreItemVisual(Data.Id); });
            interaction.Bind(Owner?.Session, Owner?.DragLayer);
        }

        private void NotifyUnavailable() => interaction?.Cancel();

        public void OnPointerDown(PointerEventData e)
        {
            if (acceptsInput && e.button == PointerEventData.InputButton.Left) SetSelected(true);
        }
        public void OnPointerUp(PointerEventData e) { if (!dragging) SetSelected(false); }
        public void OnBeginDrag(PointerEventData e)
        {
            if (!acceptsInput) return;
            BindInteraction();
            interaction.Begin(e);
        }
        public void OnDrag(PointerEventData e) => interaction?.Move(e);
        public void OnEndDrag(PointerEventData e) => interaction?.End(e);
        public void OnPointerEnter(PointerEventData e) { if (acceptsInput) SetSelected(true); }
        public void OnPointerExit(PointerEventData e) { if (!dragging) SetSelected(false); }
    }
}
