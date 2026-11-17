using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RPG4.LoadoutTemplate
{
    /// <summary>物品视图的事件适配器，执行表现指令并从实际显示位置上报命中。</summary>
    internal sealed class LoadoutDragVisual : IDisposable
    {
        private readonly MonoBehaviour host;
        private readonly RectTransform rect;
        private readonly Func<ContainerDisplaySnapshot> readSnapshot;
        private readonly Func<ItemDisplaySnapshot> readItem;
        private readonly Action<bool> setDragging;
        private readonly Action restoreLatest;
        private readonly List<RaycastResult> hits = new List<RaycastResult>();
        private EventSubscription subscription;
        private EventSubscription replySubscription, closeSubscription;
        private LoadoutWindowSession session;
        private RectTransform configuredLayer;
        private long interactionId;
        private long acceptedInteractionId;
        private object acceptedProvider;
        private int acceptedConnectRequest;
        private int connectSequence, pendingConnectRequest;
        private bool connecting;
        private float connectionDeadline;
        private ContainerDisplaySnapshot source;
        private ItemDisplaySnapshot item;
        private Transform originalParent;
        private int originalSibling;
        private Vector2 originalAnchorMin, originalAnchorMax, originalPivot, originalSize, grabOffset;
        private Vector3 originalPosition, originalScale;
        private Quaternion originalRotation;
        private RectTransform movementLayer;
        private Camera movementCamera;
        private ScrollRect sourceScroll, targetScroll;
        private bool sourceScrollEnabled, targetScrollEnabled, restoring;
        private int pointerId;
        private Vector2 lastPosition;
        private Camera lastCamera;

        internal LoadoutDragVisual(MonoBehaviour host, RectTransform rect,
            Func<ContainerDisplaySnapshot> readSnapshot, Func<ItemDisplaySnapshot> readItem, Action<bool> setDragging,
            Action restoreLatest = null)
        {
            this.host = host;
            this.rect = rect;
            this.readSnapshot = readSnapshot;
            this.readItem = readItem;
            this.setDragging = setDragging;
            this.restoreLatest = restoreLatest;
        }

        internal void Bind(object value, RectTransform dragLayer)
        {
            if (ReferenceEquals(session, value) && configuredLayer == dragLayer && subscription.IsAlive) return;
            Dispose();
            session = value as LoadoutWindowSession;
            configuredLayer = dragLayer;
            if (session != null && host.isActiveAndEnabled)
            {
                subscription = EventBus.Global.Subscribe<LoadoutVisualCommand>(
                    LoadoutChannels.Visual, session, host, OnVisual);
                replySubscription = EventBus.Global.Subscribe<LoadoutDragConnectReply>(
                    LoadoutChannels.DragConnected, host, host, OnConnected);
                closeSubscription = EventBus.Global.Subscribe(
                    LoadoutChannels.SessionClosed, session, host, Cancel);
            }
        }

        internal void Begin(PointerEventData pointer)
        {
            if (session == null || !session.IsActive || pointer == null
                || pointer.button != PointerEventData.InputButton.Left || connecting
                || acceptedInteractionId != 0 || interactionId != 0 || !host.isActiveAndEnabled) return;
            source = readSnapshot();
            item = readItem();
            if (source == null || !source.IsReady || item == null) return;
            pointerId = pointer.pointerId;
            lastPosition = pointer.position;
            lastCamera = pointer.pressEventCamera;
            Vector3 corner = rect.TransformPoint(new Vector3(rect.rect.xMin, rect.rect.yMax, 0f));
            grabOffset = pointer.position - RectTransformUtility.WorldToScreenPoint(lastCamera, corner);
            // 先建立等待状态与回复订阅，再发布连接请求，兼容同一调用栈内的连接结果。
            connecting = true;
            pendingConnectRequest = ++connectSequence;
            connectionDeadline = Time.unscaledTime + 8f;
            var input = new LoadoutInteractionInput(session, host.GetInstanceID(), 0,
                LoadoutInputKind.Begin, source, item, lastPosition, lastCamera);
            EventBus.Global.Publish(LoadoutChannels.DragConnectRequested, host,
                new LoadoutDragConnectRequest(session, host, pendingConnectRequest, input));
        }

        private void OnConnected(LoadoutDragConnectReply reply)
        {
            if (!connecting || reply.RequestId != pendingConnectRequest
                || !ReferenceEquals(reply.Session, session))
            {
                bool sameRequest = ReferenceEquals(reply.Session, session)
                    && reply.RequestId == acceptedConnectRequest;
                if (reply.Success && sameRequest && acceptedProvider != null
                    && !ReferenceEquals(reply.ProviderSource, acceptedProvider))
                {
                    Cancel();
                    CancelReplyLock(reply);
                    Debug.LogWarning("同一角色标识存在多个交换器，本次拖拽已取消。", host);
                    return;
                }
                // 松开鼠标后到达的成功回复也要释放其锁，不能只忽略显示结果。
                bool isCurrentLock = sameRequest && ReferenceEquals(reply.ProviderSource, acceptedProvider)
                    && (reply.InteractionId == interactionId || reply.InteractionId == acceptedInteractionId);
                if (!isCurrentLock) CancelReplyLock(reply);
                return;
            }
            connecting = false;
            pendingConnectRequest = 0;
            if (!reply.Success || reply.InteractionId == 0 || reply.ProviderSource == null)
            {
                CancelReplyLock(reply);
                source = null;
                item = null;
                return;
            }
            acceptedProvider = reply.ProviderSource;
            acceptedConnectRequest = reply.RequestId;
            acceptedInteractionId = reply.InteractionId;
            if (session == null || !session.IsActive || host == null || !host.isActiveAndEnabled) Cancel();
        }

        private void CancelReplyLock(LoadoutDragConnectReply reply)
        {
            if (!reply.Success || reply.InteractionId == 0 || host == null) return;
            EventBus.Global.Publish(LoadoutChannels.Input, reply.Session,
                new LoadoutInteractionInput(reply.Session, host.GetInstanceID(), reply.InteractionId,
                    LoadoutInputKind.Cancel, null, null, lastPosition, lastCamera));
        }

        internal void Move(PointerEventData pointer)
        {
            if (!Owns(pointer)) return;
            lastPosition = pointer.position;
            if (interactionId != 0) Publish(LoadoutInputKind.Move);
        }

        internal void End(PointerEventData pointer)
        {
            if (!Owns(pointer)) return;
            lastPosition = pointer.position;
            if (interactionId != 0) Publish(LoadoutInputKind.End);
            else Cancel();
        }

        internal void CheckAvailability()
        {
            if (!connecting && acceptedInteractionId == 0 && interactionId == 0) return;
            if (host == null || !host.isActiveAndEnabled || session == null || !session.IsActive
                || !Application.isFocused
                || interactionId != 0 && (originalParent == null || !originalParent.gameObject.activeInHierarchy)
                || (connecting || acceptedInteractionId != 0) && Time.unscaledTime >= connectionDeadline
                || source?.EventSource is UnityEngine.Object endpoint && endpoint == null) Cancel();
        }

        internal void Cancel()
        {
            if (restoring) return;
            long lockedId = interactionId != 0 ? interactionId : acceptedInteractionId;
            connecting = false;
            pendingConnectRequest = 0;
            acceptedInteractionId = 0;
            acceptedProvider = null;
            acceptedConnectRequest = 0;
            if (lockedId != 0 && host != null)
                EventBus.Global.Publish(LoadoutChannels.Input, session,
                    new LoadoutInteractionInput(session, host.GetInstanceID(), lockedId,
                        LoadoutInputKind.Cancel, source, item, lastPosition, lastCamera));
            if (interactionId != 0) Restore();
            else { source = null; item = null; }
        }

        private bool Owns(PointerEventData pointer) => (connecting || acceptedInteractionId != 0 || interactionId != 0) && pointer != null
            && pointer.pointerId == pointerId && pointer.button == PointerEventData.InputButton.Left;

        private void Publish(LoadoutInputKind kind)
        {
            EventBus.Global.Publish(LoadoutChannels.Input, session,
                new LoadoutInteractionInput(session, host.GetInstanceID(), interactionId, kind,
                    source, item, lastPosition, lastCamera));
        }

        private void OnVisual(LoadoutVisualCommand command)
        {
            if (command.ViewId != host.GetInstanceID() || !ReferenceEquals(command.Session, session)) return;
            if (command.Kind == LoadoutVisualKind.Begin)
            {
                if (interactionId != 0 || source == null || acceptedInteractionId != command.InteractionId) return;
                if (session == null || !session.IsActive || host == null || !host.isActiveAndEnabled)
                { Cancel(); return; }
                acceptedInteractionId = 0;
                interactionId = command.InteractionId;
                SaveTransform();
                Canvas canvas = rect.GetComponentInParent<Canvas>();
                movementLayer = command.DragLayer != null ? command.DragLayer : configuredLayer;
                if (movementLayer == null && canvas != null) movementLayer = canvas.rootCanvas.transform as RectTransform;
                if (movementLayer == null || movementLayer == rect || movementLayer.IsChildOf(rect))
                { Cancel(); return; }
                Canvas movementCanvas = movementLayer.GetComponentInParent<Canvas>();
                movementCamera = movementCanvas != null && movementCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? movementCanvas.worldCamera : null;
                Vector2 size = rect.rect.size;
                setDragging(true);
                rect.SetParent(movementLayer, true);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.sizeDelta = size;
                rect.SetAsLastSibling();
                MoveAndReport(lastPosition);
                return;
            }
            if (command.Kind == LoadoutVisualKind.Finish && acceptedInteractionId == command.InteractionId)
            {
                acceptedInteractionId = 0;
                acceptedProvider = null;
                acceptedConnectRequest = 0;
                source = null;
                item = null;
                return;
            }
            if (command.InteractionId != interactionId || interactionId == 0) return;
            if (command.Kind == LoadoutVisualKind.Move)
            {
                if (command.Item != null && host is ItemViewUI itemView)
                {
                    Vector2 pixels = itemView.Owner != null ? itemView.Owner.GetItemPixelSize(command.Item.Size) : originalSize;
                    itemView.ApplyDragAppearance(command.Item, pixels);
                }
                MoveAndReport(command.Position);
            }
            else if (command.Kind == LoadoutVisualKind.Finish) Restore();
        }

        private void SaveTransform()
        {
            originalParent = rect.parent;
            originalSibling = rect.GetSiblingIndex();
            originalAnchorMin = rect.anchorMin;
            originalAnchorMax = rect.anchorMax;
            originalPivot = rect.pivot;
            originalSize = rect.sizeDelta;
            originalPosition = rect.anchoredPosition3D;
            originalScale = rect.localScale;
            originalRotation = rect.localRotation;
            sourceScroll = rect.GetComponentInParent<ScrollRect>();
            if (sourceScroll != null)
            {
                sourceScrollEnabled = sourceScroll.enabled;
                sourceScroll.StopMovement();
                sourceScroll.enabled = false;
            }
        }

        private void MoveAndReport(Vector2 position)
        {
            if (movementLayer == null) { Cancel(); return; }
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(movementLayer,
                position - grabOffset, movementCamera, out Vector2 local))
                rect.position = movementLayer.TransformPoint(local);
            // 格子物品使用左上角内侧作为原点，槽位物品使用图像中心定位。
            float x = source.IsSlot ? rect.rect.center.x : rect.rect.xMin + .01f;
            float y = source.IsSlot ? rect.rect.center.y : rect.rect.yMax - .01f;
            Vector2 probe = RectTransformUtility.WorldToScreenPoint(movementCamera,
                rect.TransformPoint(new Vector3(x, y, 0f)));
            ContainerDisplaySnapshot target = null;
            Vector2Int origin = Vector2Int.zero;
            int viewId = 0;
            string targetItemId = null;
            ScrollRect scroll = null;
            EventSystem system = EventSystem.current;
            if (system != null)
            {
                var pointer = new PointerEventData(system) { position = probe };
                hits.Clear();
                system.RaycastAll(pointer, hits);
                if (hits.Count > 0 && hits[0].gameObject != null)
                {
                    GameObject hit = hits[0].gameObject;
                    LoadoutSlotUI slot = hit.GetComponentInParent<LoadoutSlotUI>();
                    ContainerSlotUI body = hit.GetComponentInParent<ContainerSlotUI>();
                    if (slot != null && slot.isActiveAndEnabled && ReferenceEquals(slot.Session, session))
                    { target = slot.Snapshot; viewId = slot.GetInstanceID(); }
                    else if (body != null && body.isActiveAndEnabled && ReferenceEquals(body.Session, session))
                    { target = body.Snapshot; viewId = body.GetInstanceID(); }
                    else
                    {
                        CellUI cell = hit.GetComponentInParent<CellUI>();
                        ItemViewUI other = hit.GetComponentInParent<ItemViewUI>();
                        ContainerUI owner = cell != null ? cell.Owner : other != null ? other.Owner : null;
                        Camera camera = hits[0].module != null ? hits[0].module.eventCamera : null;
                        if (owner != null && owner.IsReady && ReferenceEquals(owner.Session, session)
                            && owner.TryResolveCell(probe, camera, out origin))
                        { target = owner.Snapshot; viewId = owner.ViewId; }
                    }
                    scroll = hit.GetComponentInParent<ScrollRect>();
                    if (target != null)
                    {
                        foreach (ItemDisplaySnapshot candidate in target.Items)
                        {
                            if (target.IsSlot || (origin.x >= candidate.Origin.x && origin.y >= candidate.Origin.y
                                && (long)origin.x < (long)candidate.Origin.x + candidate.Size.x
                                && (long)origin.y < (long)candidate.Origin.y + candidate.Size.y))
                            { targetItemId = candidate.Id; break; }
                        }
                    }
                }
            }
            SetTargetScroll(target != null ? scroll : null);
            EventBus.Global.Publish(LoadoutChannels.Target, session,
                new LoadoutTargetReport(session, host.GetInstanceID(), interactionId,
                    viewId, target, origin, targetItemId));
        }

        private void SetTargetScroll(ScrollRect value)
        {
            if (value == sourceScroll) value = null;
            if (targetScroll == value) return;
            if (targetScroll != null) targetScroll.enabled = targetScrollEnabled;
            targetScroll = value;
            if (targetScroll == null) return;
            targetScrollEnabled = targetScroll.enabled;
            targetScroll.StopMovement();
            targetScroll.enabled = false;
        }

        private void Restore()
        {
            if (restoring || interactionId == 0) return;
            restoring = true;
            interactionId = 0;
            try
            {
                if (rect != null && originalParent != null)
                {
                    rect.SetParent(originalParent, false);
                    rect.anchorMin = originalAnchorMin;
                    rect.anchorMax = originalAnchorMax;
                    rect.pivot = originalPivot;
                    rect.sizeDelta = originalSize;
                    rect.anchoredPosition3D = originalPosition;
                    rect.localScale = originalScale;
                    rect.localRotation = originalRotation;
                    rect.SetSiblingIndex(originalSibling);
                }
                else if (rect != null) rect.gameObject.SetActive(false);
                setDragging(false);
                if (host is ItemViewUI itemView && item != null) itemView.ApplyDragAppearance(item, originalSize);
                ContainerDisplaySnapshot latest = readSnapshot();
                if (latest != null && source != null && (latest.Revision != source.Revision
                    || !ReferenceEquals(latest.EventSource, source.EventSource)))
                    restoreLatest?.Invoke();
                if (sourceScroll != null) sourceScroll.enabled = sourceScrollEnabled;
                SetTargetScroll(null);
            }
            finally
            {
                source = null;
                item = null;
                movementLayer = null;
                originalParent = null;
                sourceScroll = null;
                acceptedInteractionId = 0;
                acceptedProvider = null;
                acceptedConnectRequest = 0;
                pendingConnectRequest = 0;
                connecting = false;
                restoring = false;
            }
        }

        public void Dispose()
        {
            Cancel();
            subscription.Dispose();
            replySubscription.Dispose();
            closeSubscription.Dispose();
            session = null;
            source = null;
            item = null;
        }
    }
}
