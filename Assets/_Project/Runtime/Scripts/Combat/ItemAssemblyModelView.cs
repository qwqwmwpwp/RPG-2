using System;
using System.Collections.Generic;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.Combat
{
    /// <summary>Read-only recursive model projection for an item's installed assembly.</summary>
    [DisallowMultipleComponent]
    public sealed class ItemAssemblyModelView : MonoBehaviour
    {
        [Serializable]
        private sealed class SlotMount
        {
            [SerializeField] internal string slotId;
            [SerializeField] internal Transform mount;
            [SerializeField] internal GameObject fallbackModel;
        }

        [SerializeField] private List<SlotMount> slotMounts = new List<SlotMount>();

        private readonly Dictionary<string, SlotMount> mounts = new Dictionary<string, SlotMount>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameObject> instances = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private readonly Dictionary<ItemId, GameObject> modelsByItem = new Dictionary<ItemId, GameObject>();
        private readonly List<Renderer> highlightedRenderers = new List<Renderer>();
        private readonly List<MaterialPropertyBlock> originalPropertyBlocks = new List<MaterialPropertyBlock>();
        private RuntimeItem boundItem;
        private ItemAssemblyModelView focusOwner;
        private int structureRevision = -1;

        public ItemId FocusedParentItemId { get; private set; }
        public string FocusedSlotId { get; private set; }
        public ItemId FocusedItemId { get; private set; }

        public void Bind(RuntimeItem item)
        {
            if (item == null) { Clear(); return; }
            if (!ReferenceEquals(boundItem, item))
            {
                Clear();
                boundItem = item;
                boundItem.Changed += OnItemChanged;
            }
            if (structureRevision == item.StructureRevision) return;
            Rebuild(item);
        }

        public void FocusSlot(ItemId parentItemId, string slotId)
        {
            ClearHighlight();
            FocusedParentItemId = parentItemId;
            FocusedSlotId = slotId;
            FocusedItemId = ItemId.None;
            ItemAssemblyModelView parentView = FindView(parentItemId);
            if (parentView == null || string.IsNullOrWhiteSpace(slotId) || !parentView.mounts.ContainsKey(slotId)) return;
            RuntimeItem child = parentView.boundItem.Assembly.GetChild(slotId);
            GameObject target = child != null ? FindModel(child.ItemId) : null;
            ItemAssemblyModelView owner = child != null ? FindView(child.ItemId) ?? FindViewForModel(child.ItemId) : null;
            if (target == null) { target = parentView.gameObject; owner = parentView; }
            focusOwner = owner;
            focusOwner?.ApplyHighlight(target, owner);
        }

        public void FocusItem(ItemId itemId)
        {
            ClearHighlight();
            GameObject target = FindModel(itemId);
            ItemAssemblyModelView owner = FindView(itemId) ?? FindViewForModel(itemId);
            FocusedItemId = target != null ? itemId : ItemId.None;
            FocusedParentItemId = ItemId.None;
            FocusedSlotId = null;
            if (target != null)
            {
                focusOwner = owner;
                focusOwner?.ApplyHighlight(target, owner);
            }
        }

        public void ClearFocus()
        {
            ClearHighlight();
            FocusedParentItemId = ItemId.None;
            FocusedSlotId = null;
            FocusedItemId = ItemId.None;
        }

        public void Clear()
        {
            ClearFocus();
            if (boundItem != null) boundItem.Changed -= OnItemChanged;
            boundItem = null;
            structureRevision = -1;
            foreach (GameObject instance in instances.Values) DestroyModel(instance);
            instances.Clear();
            modelsByItem.Clear();
            mounts.Clear();
        }

        public GameObject GetModel(ItemId itemId) => FindModel(itemId);

        public bool TryGetSlotTransform(ItemId parentItemId, string slotId, out Transform socket)
        {
            ItemAssemblyModelView parentView = FindView(parentItemId);
            if (parentView != null && !string.IsNullOrWhiteSpace(slotId)
                && parentView.mounts.TryGetValue(slotId, out SlotMount slot))
            {
                socket = slot.mount;
                return socket != null;
            }
            socket = null;
            return false;
        }

        private void OnItemChanged(RuntimeItem item)
        {
            if (ReferenceEquals(item, boundItem) && structureRevision != item.StructureRevision)
                Rebuild(item);
        }

        private void Rebuild(RuntimeItem item)
        {
            ItemId focusedParent = FocusedParentItemId;
            string focusedSlot = FocusedSlotId;
            ItemId focusedItem = FocusedItemId;
            ClearHighlight();
            foreach (GameObject instance in instances.Values) DestroyModel(instance);
            instances.Clear();
            modelsByItem.Clear();
            mounts.Clear();
            foreach (SlotMount entry in slotMounts)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.slotId) || entry.mount == null) continue;
                mounts[entry.slotId] = entry;
            }
            if (item.ItemId.IsValid) modelsByItem[item.ItemId] = gameObject;
            InstantiateChildren(item);
            structureRevision = item.StructureRevision;
            if (focusedParent.IsValid) FocusSlot(focusedParent, focusedSlot);
            else if (focusedItem.IsValid) FocusItem(focusedItem);
        }

        private void InstantiateChildren(RuntimeItem parent)
        {
            foreach (KeyValuePair<string, RuntimeItem> pair in parent.Assembly.InstalledChildren)
            {
                if (!mounts.TryGetValue(pair.Key, out SlotMount slot) || pair.Value == null) continue;
                RuntimeItem child = pair.Value;
                GameObject prefab = ResolveModel(child) ?? slot.fallbackModel;
                if (prefab == null) continue;
                GameObject instance = InstantiateAtSocket(prefab, slot.mount);
                SetLayerRecursively(instance.transform, slot.mount.gameObject.layer);
                instances[MakeKey(parent.ItemId, pair.Key)] = instance;
                if (child.ItemId.IsValid) modelsByItem[child.ItemId] = instance;
                ItemAssemblyModelView nestedView = instance.GetComponentInChildren<ItemAssemblyModelView>(true);
                if (nestedView != null) nestedView.Bind(child);
            }
        }

        private GameObject FindModel(ItemId itemId)
        {
            if (modelsByItem.TryGetValue(itemId, out GameObject model)) return model;
            foreach (GameObject instance in instances.Values)
            {
                ItemAssemblyModelView nested = instance != null ? instance.GetComponentInChildren<ItemAssemblyModelView>(true) : null;
                if (nested == null) continue;
                model = nested.FindModel(itemId);
                if (model != null) return model;
            }
            return null;
        }

        private ItemAssemblyModelView FindView(ItemId itemId)
        {
            if (boundItem != null && boundItem.ItemId == itemId) return this;
            foreach (GameObject instance in instances.Values)
            {
                ItemAssemblyModelView nested = instance != null ? instance.GetComponentInChildren<ItemAssemblyModelView>(true) : null;
                ItemAssemblyModelView result = nested?.FindView(itemId);
                if (result != null) return result;
            }
            return null;
        }

        private ItemAssemblyModelView FindViewForModel(ItemId itemId)
        {
            if (modelsByItem.ContainsKey(itemId)) return this;
            foreach (GameObject instance in instances.Values)
            {
                ItemAssemblyModelView nested = instance != null ? instance.GetComponentInChildren<ItemAssemblyModelView>(true) : null;
                ItemAssemblyModelView result = nested?.FindViewForModel(itemId);
                if (result != null) return result;
            }
            return null;
        }

        private void ApplyHighlight(GameObject target, ItemAssemblyModelView owner)
        {
            RestoreHighlight();
            if (target == null) return;
            int baseColorId = Shader.PropertyToID("_BaseColor");
            int colorId = Shader.PropertyToID("_Color");
            int emissionId = Shader.PropertyToID("_EmissionColor");
            bool highlightingOwnerRoot = owner != null && target == owner.gameObject;
            foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
            {
                if (highlightingOwnerRoot && IsMountedChild(renderer.transform, owner)) continue;
                var original = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(original);
                highlightedRenderers.Add(renderer);
                originalPropertyBlocks.Add(original);
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    if (material.HasProperty(baseColorId)) block.SetColor(baseColorId, material.GetColor(baseColorId) * 1.25f);
                    if (material.HasProperty(colorId)) block.SetColor(colorId, material.GetColor(colorId) * 1.25f);
                    if (material.HasProperty(emissionId)) block.SetColor(emissionId, new Color(0.35f, 0.25f, 0.05f, 1f));
                }
                renderer.SetPropertyBlock(block);
            }
        }

        private static bool IsMountedChild(Transform candidate, ItemAssemblyModelView owner)
        {
            foreach (SlotMount slot in owner.mounts.Values)
                if (slot.mount != null && (candidate == slot.mount || candidate.IsChildOf(slot.mount))) return true;
            return false;
        }

        private void ClearHighlight()
        {
            if (focusOwner != null && !ReferenceEquals(focusOwner, this)) focusOwner.RestoreHighlight();
            else RestoreHighlight();
            focusOwner = null;
        }

        private void RestoreHighlight()
        {
            for (int i = 0; i < highlightedRenderers.Count; i++)
                if (highlightedRenderers[i] != null) highlightedRenderers[i].SetPropertyBlock(originalPropertyBlocks[i]);
            highlightedRenderers.Clear();
            originalPropertyBlocks.Clear();
        }

        private static GameObject ResolveModel(RuntimeItem item)
        {
            GameObject prefab = item.GetFragment<EquipmentFragment>()?.EquipPrefab;
            return prefab != null ? prefab : item.GetFragment<AttachmentDefinitionFragment>()?.Model;
        }

        private static string MakeKey(ItemId parentId, string slotId)
            => parentId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + slotId;

        private static GameObject InstantiateAtSocket(GameObject prefab, Transform socket)
        {
            GameObject instance = Instantiate(prefab, socket, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            return instance;
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++) SetLayerRecursively(root.GetChild(i), layer);
        }

        private static void DestroyModel(GameObject model)
        {
            if (model == null) return;
            model.SetActive(false);
            if (Application.isPlaying) Destroy(model);
            else DestroyImmediate(model);
        }

        private void OnDestroy() => Clear();
    }
}
