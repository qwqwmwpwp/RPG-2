using System.Collections.Generic;
using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Combat;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace RPG4.Backpack.UI
{
    /// <summary>从只读库存投影解析物品并在独立相机中呈现；不写入运行时物品。</summary>
    [DisallowMultipleComponent]
    public sealed class AssemblyPreviewViewport : MonoBehaviour, IPointerDownHandler, IDragHandler, IScrollHandler
    {
        private const int PreviewLayer = 6;
        private static readonly HashSet<int> occupiedStations = new HashSet<int>();
        private static int nextStation;
        public RawImage target;
        public Camera previewCamera;
        public Transform rigRoot;
        public Transform modelHolder;
        public ItemAssemblyModelView modelView;
        public Image fallbackIcon;
        public TMPro.TMP_Text previewStatus;
        public RectTransform markerRoot;
        public AssemblySlotMarker markerPrefab;
        [SerializeField] private int textureWidth = 768;
        [SerializeField] private int textureHeight = 512;
        private RenderTexture texture;
        private IBackpackView inventory;
        private RuntimeItem rootItem;
        private GameObject model;
        private GameObject candidateModel;
        private ItemAssemblyModelView candidateView;
        private ItemId boundRootId;
        private GameObject boundPrefab;
        private int boundStructureRevision = -1;
        private RuntimeItem modelViewBoundItem;
        private float zoom = 5f;
        private int station = -1;
        private readonly List<AssemblySlotMarker> markers = new List<AssemblySlotMarker>();
        private IReadOnlyList<AssemblySlotSnapshot> markerSlots;
        private Action<AssemblySlotSnapshot> markerSelection;
        private ItemId focusedParent;
        private string focusedSlot;
        private string statusMessage = "选择物品以查看 3D 预览";
        public string StatusMessage => statusMessage;

        public void Bind(IBackpackView source, ItemId rootId)
        {
            inventory = source;
            RuntimeItem resolved = Find(source, rootId);
            bool sameItem = rootItem != null && resolved != null && rootItem.ItemId == resolved.ItemId;
            rootItem = resolved;
            if (!sameItem) ClearModel();
            Refresh(false);
        }
        public void FocusSlot(ItemId parentId, string slotId) => modelView?.FocusSlot(parentId, slotId);
        public void FocusItem(ItemId itemId) => modelView?.FocusItem(itemId);
        public void ClearFocus() => modelView?.ClearFocus();
        public void RenderMarkers(IReadOnlyList<AssemblySlotSnapshot> slots, ItemId parentId, string slotId, Action<AssemblySlotSnapshot> onSelected)
        { markerSlots = slots; markerSelection = onSelected; focusedParent = parentId; focusedSlot = slotId; UpdateMarkers(); }
        public void PreviewCandidate(IBackpackView source, ItemId candidateId, ItemId parentId, string slotId)
        {
            ClearCandidatePreview();
            RuntimeItem candidate = Find(source, candidateId);
            GameObject prefab = candidate?.GetFragment<EquipmentFragment>()?.EquipPrefab
                ?? candidate?.GetFragment<AttachmentDefinitionFragment>()?.Model;
            if (candidate == null || prefab == null)
            {
                statusMessage = candidate == null ? "候选物品尚未同步" : "该候选没有 3D 模型，保留当前模型并显示属性";
                if (previewStatus != null) previewStatus.text = statusMessage;
                modelView?.FocusSlot(parentId, slotId);
                return;
            }
            Transform parent = modelHolder != null ? modelHolder : transform;
            if (modelView != null && modelView.TryGetSlotTransform(parentId, slotId, out Transform socket) && socket != null)
                parent = socket;
            candidateModel = Instantiate(prefab, parent);
            candidateModel.transform.localPosition = Vector3.zero;
            candidateModel.transform.localRotation = Quaternion.identity;
            candidateModel.transform.localScale = Vector3.one;
            SetLayerRecursively(candidateModel.transform, PreviewLayer);
            candidateView = candidateModel.GetComponentInChildren<ItemAssemblyModelView>(true);
            candidateView?.Bind(candidate);
            modelView?.FocusSlot(parentId, slotId);
            statusMessage = parent == (modelHolder != null ? modelHolder : transform)
                ? "候选模型单独预览；原模型未修改" : "候选模型已叠加至对应挂点；原模型未修改";
            if (previewStatus != null) previewStatus.text = statusMessage;
        }
        public void ClearCandidatePreview()
        {
            candidateView?.Clear(); candidateView = null;
            if (candidateModel != null) Destroy(candidateModel);
            candidateModel = null;
        }
        public void Clear()
        {
            inventory = null; rootItem = null; boundRootId = ItemId.None; boundPrefab = null; boundStructureRevision = -1;
            ClearCandidatePreview();
            ClearModel();
            markerSlots = null; UpdateMarkers();
            if (fallbackIcon != null) { fallbackIcon.sprite = null; fallbackIcon.enabled = false; }
            if (previewStatus != null) previewStatus.text = "选择物品以查看 3D 预览";
            statusMessage = "选择物品以查看 3D 预览";
        }

        public void SetVisible(bool value)
        {
            if (previewCamera != null) previewCamera.enabled = value;
        }
        public void OnPointerDown(PointerEventData eventData) { }
        public void OnDrag(PointerEventData eventData)
        {
            if (model == null) return;
            model.transform.Rotate(Vector3.up, -eventData.delta.x * 0.35f, Space.World);
            model.transform.Rotate(previewCamera != null ? previewCamera.transform.right : Vector3.right, eventData.delta.y * 0.35f, Space.World);
        }
        public void OnScroll(PointerEventData eventData)
        {
            zoom = Mathf.Clamp(zoom - eventData.scrollDelta.y * 0.35f, 1.5f, 12f);
            if (previewCamera != null) previewCamera.fieldOfView = zoom * 8f;
        }

        private void Awake()
        {
            if (previewCamera != null && target != null)
            {
                texture = new RenderTexture(Mathf.Max(64, textureWidth), Mathf.Max(64, textureHeight), 24, RenderTextureFormat.ARGB32)
                { name = "AssemblyPreviewRuntime", antiAliasing = 2, useMipMap = false, autoGenerateMips = false };
                texture.Create(); previewCamera.targetTexture = texture; target.texture = texture;
                previewCamera.cullingMask = 1 << PreviewLayer;
                previewCamera.enabled = false;
                if (rigRoot != null)
                {
                    station = AcquireStation();
                    int columns = 64;
                    rigRoot.position = new Vector3(10000f + station % columns * 256f, 10000f + station / columns * 256f, 0f);
                }
                previewCamera.fieldOfView = zoom * 8f;
            }
        }
        private void OnDestroy()
        {
            Clear();
            if (previewCamera != null) previewCamera.targetTexture = null;
            if (target != null) target.texture = null;
            if (texture != null) { texture.Release(); Destroy(texture); texture = null; }
            if (station >= 0) { occupiedStations.Remove(station); station = -1; }
        }
        private void Refresh(bool force = false)
        {
            if (rootItem == null)
            {
                ClearModel();
                if (fallbackIcon != null) { fallbackIcon.sprite = null; fallbackIcon.enabled = false; }
                if (previewStatus != null) previewStatus.text = "物品尚未同步或已离开库存";
                statusMessage = "物品尚未同步或已离开库存";
                return;
            }
            GameObject prefab = rootItem.GetFragment<EquipmentFragment>()?.EquipPrefab
                ?? rootItem.GetFragment<AttachmentDefinitionFragment>()?.Model;
            if (prefab == null)
            {
                ClearModel();
                if (fallbackIcon != null) { fallbackIcon.sprite = rootItem.GetFragment<ImageFragment>()?.Icon; fallbackIcon.enabled = fallbackIcon.sprite != null; }
                if (previewStatus != null) previewStatus.text = "该物品没有 3D 模型，显示图标";
                statusMessage = "该物品没有 3D 模型，显示图标";
                return;
            }
            if (model == null || boundRootId != rootItem.ItemId || boundPrefab != prefab || force)
            {
                ClearModel();
                model = Instantiate(prefab, modelHolder != null ? modelHolder : transform);
                model.transform.localPosition = Vector3.zero; model.transform.localRotation = Quaternion.identity; model.transform.localScale = Vector3.one;
                boundRootId = rootItem.ItemId; boundPrefab = prefab;
            }
            SetLayerRecursively(model.transform, PreviewLayer);
            modelView = model.GetComponentInChildren<ItemAssemblyModelView>(true);
            if (modelView != null && (force || boundStructureRevision != rootItem.StructureRevision
                || !ReferenceEquals(modelViewBoundItem, rootItem)))
            {
                modelView.Bind(rootItem);
                modelViewBoundItem = rootItem;
            }
            boundStructureRevision = rootItem.StructureRevision;
            if (fallbackIcon != null) fallbackIcon.enabled = false;
            statusMessage = modelView != null ? "拖动旋转 · 滚轮缩放" : "模型已载入；此物品没有装配挂点预览组件";
            if (previewStatus != null) previewStatus.text = statusMessage;
            UpdateMarkers();
        }
        private void LateUpdate() { if (assemblyModeVisible) UpdateMarkers(); }
        private bool assemblyModeVisible = true;
        public void SetAssemblyPreviewVisible(bool value) { assemblyModeVisible = value; if (!value) HideMarkers(); else UpdateMarkers(); }
        private void UpdateMarkers()
        {
            int used = 0;
            if (markerRoot != null && markerPrefab != null && previewCamera != null && modelView != null && markerSlots != null)
            {
                foreach (AssemblySlotSnapshot slot in markerSlots)
                {
                    if (!modelView.TryGetSlotTransform(slot.ParentItemId, slot.SlotId, out Transform socket) || socket == null) continue;
                    Vector3 point = previewCamera.WorldToViewportPoint(socket.position);
                    if (point.z <= 0f || point.x < 0f || point.x > 1f || point.y < 0f || point.y > 1f) continue;
                    if (used == markers.Count) markers.Add(Instantiate(markerPrefab, markerRoot));
                    AssemblySlotMarker marker = markers[used++]; marker.gameObject.SetActive(true);
                    RectTransform rect = marker.transform as RectTransform;
                    if (rect != null)
                    {
                        Vector2 anchor = new Vector2(point.x, point.y); rect.anchorMin = rect.anchorMax = anchor;
                        rect.anchoredPosition = Vector2.zero;
                    }
                    marker.Bind(slot, markerSelection, slot.ParentItemId == focusedParent && slot.SlotId == focusedSlot);
                }
            }
            for (int i = used; i < markers.Count; i++) markers[i].gameObject.SetActive(false);
        }
        private void HideMarkers() { foreach (AssemblySlotMarker marker in markers) if (marker != null) marker.gameObject.SetActive(false); }
        private static int AcquireStation()
        {
            for (int attempt = 0; attempt < 4096; attempt++)
            {
                int value = (nextStation + attempt) % 4096;
                if (occupiedStations.Add(value)) { nextStation = (value + 1) % 4096; return value; }
            }
            throw new InvalidOperationException("装配预览同时打开数量超过隔离站位池容量。");
        }
        private void ClearModel()
        {
            ClearCandidatePreview();
            modelView?.Clear(); modelView = null;
            if (model != null) Destroy(model);
            model = null; boundRootId = ItemId.None; boundPrefab = null; boundStructureRevision = -1; modelViewBoundItem = null;
        }
        private static RuntimeItem Find(IBackpackView source, ItemId id)
        {
            if (source == null || !source.IsReady || id.IsNone) return null;
            var items = new List<RuntimeItem>(); source.CopyItems(items);
            foreach (RuntimeItem item in items)
                if (ItemAssemblyTraversal.TryFind(item, id, out RuntimeItem found)) return found;
            return null;
        }
        private static void SetLayerRecursively(Transform node, int layer)
        {
            node.gameObject.layer = layer;
            for (int i = 0; i < node.childCount; i++) SetLayerRecursively(node.GetChild(i), layer);
        }
    }
}
