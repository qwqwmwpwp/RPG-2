using RPG4.Backpack.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RPG4.LoadoutTemplate
{
    /// <summary>十类装备槽共用的显示与输入组件，接收规则来自数据端点快照。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform), typeof(Image), typeof(CanvasGroup))]
    public class LoadoutSlotUI : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IBeginDragHandler,
        IDragHandler, IEndDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Image background;
        [SerializeField] private Image icon;
        [SerializeField] private Image highlight;
        [SerializeField] private CanvasGroup group;
        [SerializeField] private Color hoveredColor = new Color(.45f, .7f, 1f, .3f);
        [SerializeField] private Color allowedColor = new Color(.2f, .85f, .45f, .45f);
        [SerializeField] private Color blockedColor = new Color(1f, .25f, .25f, .45f);
        private RectTransform rect;
        private Color normalColor;
        private bool cachedColor, dragging, savedBlocksRaycasts, savedInteractable, refreshPending, hasPreview;
        private bool presentationVisible = true, visibleBlocksRaycasts, visibleInteractable;
        private float visibleAlpha;
        private object endpoint;
        private RectTransform dragLayer;
        private EventSubscription changedSubscription, snapshotSubscription, visualSubscription;
        private LoadoutDragVisual interaction;
        private int requestId;
        private int receivedRequestId = -1;

        public RectTransform RectTransform => rect != null ? rect : (rect = (RectTransform)transform);
        public object Session { get; private set; }
        public ContainerDisplaySnapshot Snapshot { get; private set; }
        public ItemDisplaySnapshot Data => Snapshot != null && Snapshot.Items.Count > 0 ? Snapshot.Items[0] : null;
        public object Endpoint => endpoint;

        protected virtual void Awake()
        {
            CacheComponents();
            if (background != null) background.raycastTarget = true;
            if (icon != null) icon.raycastTarget = false;
            if (highlight != null) highlight.raycastTarget = false;
            if (Snapshot == null) Apply(null);
            SetFeedback(CellFeedback.Normal);
        }

        protected virtual void OnEnable()
        {
            Subscribe();
            refreshPending = true;
        }

        protected virtual void LateUpdate()
        {
            interaction?.CheckAvailability();
            if (Session is LoadoutWindowSession window && !window.IsActive) { Disconnect(); return; }
            if (!refreshPending || endpoint == null) return;
            refreshPending = false;
            int request = ++requestId;
            EventBus.Global.Publish(LoadoutChannels.SnapshotRequested, endpoint,
                new LoadoutSnapshotRequest(this, request));
            if (receivedRequestId != request)
            {
                interaction?.Cancel();
                Snapshot = null;
                Apply(null);
            }
        }

        protected virtual void OnDisable()
        {
            interaction?.Dispose();
            Unsubscribe();
            Snapshot = null;
            hasPreview = false;
            SetDragState(false);
            SetDimmed(false);
            SetFeedback(CellFeedback.Normal);
        }

        protected virtual void OnDestroy() => interaction?.Dispose();

        public void CancelInteraction()
        {
            interaction?.Cancel();
            SetDragState(false);
            SetFeedback(CellFeedback.Normal);
        }

        public void Bind(object endpointSource, object session, RectTransform movementLayer = null)
            => BindEndpoint(endpointSource, session, movementLayer);

        public void Disconnect()
        {
            interaction?.Cancel();
            Unsubscribe();
            endpoint = null;
            Session = null;
            Snapshot = null;
            refreshPending = false;
            hasPreview = false;
            Apply(null);
            SetDragState(false);
            SetFeedback(CellFeedback.Normal);
            BindInteraction();
        }

        public void BindEndpoint(object endpointSource, object session, RectTransform movementLayer = null)
        {
            if (ReferenceEquals(endpoint, endpointSource) && ReferenceEquals(Session, session)
                && dragLayer == movementLayer) return;
            interaction?.Cancel();
            Unsubscribe();
            endpoint = endpointSource;
            Session = session is LoadoutWindowSession window && window.IsActive ? session : null;
            dragLayer = movementLayer;
            Snapshot = null;
            Apply(null);
            Subscribe();
            refreshPending = endpoint != null && Session != null;
        }

        private void Subscribe()
        {
            if (!isActiveAndEnabled) return;
            Unsubscribe();
            if (endpoint != null && Session != null)
            {
                changedSubscription = EventBus.Global.Subscribe(LoadoutChannels.Changed, endpoint, this, OnChanged);
                snapshotSubscription = EventBus.Global.Subscribe<LoadoutSnapshotReply>(
                    LoadoutChannels.SnapshotReceived, this, this, OnSnapshot);
            }
            if (Session != null)
                visualSubscription = EventBus.Global.Subscribe<LoadoutVisualCommand>(
                    LoadoutChannels.Visual, Session, this, OnVisual);
            BindInteraction();
        }

        private void Unsubscribe()
        {
            changedSubscription.Dispose();
            snapshotSubscription.Dispose();
            visualSubscription.Dispose();
            requestId++;
        }

        private void OnChanged()
        {
            interaction?.Cancel();
            Snapshot = null;
            Apply(null);
            refreshPending = true;
        }

        private void OnSnapshot(LoadoutSnapshotReply reply)
        {
            if (reply.RequestId != requestId || !isActiveAndEnabled
                || !(Session is LoadoutWindowSession window) || !window.IsActive) return;
            receivedRequestId = reply.RequestId;
            interaction?.Cancel();
            Snapshot = reply.Snapshot != null && reply.Snapshot.IsReady ? reply.Snapshot : null;
            Apply(Data?.Icon);
            SetFeedback(CellFeedback.Normal);
        }

        private void OnVisual(LoadoutVisualCommand command)
        {
            if (!ReferenceEquals(command.Session, Session)) return;
            if (command.Kind == LoadoutVisualKind.Finish)
            {
                hasPreview = false;
                SetFeedback(CellFeedback.Normal);
            }
            else if (command.Kind == LoadoutVisualKind.Preview)
            {
                hasPreview = command.TargetViewId == GetInstanceID();
                SetFeedback(hasPreview
                    ? command.Preview.Allowed ? CellFeedback.Allowed : CellFeedback.Blocked
                    : CellFeedback.Normal);
            }
        }

        private void BindInteraction()
        {
            if (interaction == null)
                interaction = new LoadoutDragVisual(this, RectTransform, () => Snapshot, () => Data, SetDragState);
            interaction.Bind(Session, dragLayer);
        }

        public void Apply(Sprite value)
        {
            if (icon == null) return;
            icon.sprite = value;
            icon.preserveAspect = true;
            icon.gameObject.SetActive(value != null);
        }

        public void SetFeedback(CellFeedback feedback)
        {
            CacheComponents();
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

        public void SetDimmed(bool value)
        {
            CacheComponents();
            if (group == null) return;
            float alpha = value ? .35f : 1f;
            if (presentationVisible) group.alpha = alpha;
            else { visibleAlpha = alpha; group.alpha = 0f; }
        }

        /// <summary>仅隐藏显示与输入，保留物体及全部布局占位。</summary>
        protected void SetPresentationVisible(bool value)
        {
            if (presentationVisible == value) return;
            CancelInteraction();
            CacheComponents();
            if (group != null)
            {
                if (!value)
                {
                    visibleAlpha = group.alpha;
                    visibleBlocksRaycasts = group.blocksRaycasts;
                    visibleInteractable = group.interactable;
                    group.alpha = 0f;
                    group.blocksRaycasts = false;
                    group.interactable = false;
                }
                else
                {
                    group.alpha = visibleAlpha;
                    group.blocksRaycasts = visibleBlocksRaycasts;
                    group.interactable = visibleInteractable;
                }
            }
            presentationVisible = value;
        }

        public void SetDragState(bool value)
        {
            CacheComponents();
            if (dragging == value) return;
            if (group != null)
            {
                if (value)
                {
                    savedBlocksRaycasts = group.blocksRaycasts;
                    savedInteractable = group.interactable;
                    group.blocksRaycasts = false;
                    group.interactable = false;
                }
                else
                {
                    group.blocksRaycasts = presentationVisible && savedBlocksRaycasts;
                    group.interactable = presentationVisible && savedInteractable;
                }
            }
            dragging = value;
        }

        private void CacheComponents()
        {
            if (background == null) background = GetComponent<Image>();
            if (group == null) group = GetComponent<CanvasGroup>();
            if (cachedColor || background == null) return;
            normalColor = background.color;
            cachedColor = true;
        }

        public void OnPointerDown(PointerEventData e) { }
        public void OnPointerUp(PointerEventData e) { }
        public void OnBeginDrag(PointerEventData e)
        {
            if (!presentationVisible) return;
            BindInteraction();
            interaction.Begin(e);
        }
        public void OnDrag(PointerEventData e) => interaction?.Move(e);
        public void OnEndDrag(PointerEventData e) => interaction?.End(e);
        public void OnPointerEnter(PointerEventData e) { if (presentationVisible && !dragging && !hasPreview) SetFeedback(CellFeedback.Hovered); }
        public void OnPointerExit(PointerEventData e) { if (presentationVisible && !dragging && !hasPreview) SetFeedback(CellFeedback.Normal); }
    }
}
