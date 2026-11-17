using RPG4.Backpack.Items;
using RPG4.Combat;
using UnityEngine;
using UnityEngine.Animations;

namespace RPG4.PlayerMovement
{
    /// <summary>只读装备模型投影；大厅和局内共用创建、配件绑定、刷新及释放。</summary>
    internal sealed class CharacterEquipmentPresentation
    {
        private readonly Transform carrier;
        private GameObject equipmentModel;
        private Transform grip;
        private bool prepared;
        private RuntimeItem item;
        private bool attempted, rigAvailable, visible = true;
        private int revision = -1;
        private static uint nextVersion;
        internal WeaponView View { get; private set; }
        internal ulong Identity => item != null ? item.ItemId.Value : 0;
        internal uint Version { get; private set; }
        internal bool Matches(RuntimeItem value) => value == null ? item == null
            : item != null && item.ItemId == value.ItemId && item.ItemType.Equals(value.ItemType) && revision == value.Revision;
        internal CharacterEquipmentPresentation(Transform carrier)
        {
            this.carrier = carrier;
            PrepareCarrier();
        }
        private void PrepareCarrier()
        {
            if (prepared || carrier == null) return;
            prepared = true;
            foreach (Renderer renderer in carrier.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
        }
        internal bool TryGetGripPose(out Vector3 position, out Quaternion rotation)
        {
            position = grip != null && carrier != null ? carrier.InverseTransformPoint(grip.position) : Vector3.zero;
            rotation = grip != null && carrier != null ? Quaternion.Inverse(carrier.rotation) * grip.rotation : Quaternion.identity;
            // A staged model is hidden while its immutable geometry is used to plan next frame's IK.
            return grip != null && equipmentModel != null;
        }
        private bool TryPrepare(GameObject prefab, out GameObject model)
        {
            model = null;
            if (carrier == null || prefab == null) return false;
            WeaponView source = prefab.GetComponentInChildren<WeaponView>(true);
            if (source == null || source.Muzzle == null) return false;
            // Equipment can render without a support grip; only its left-hand IK becomes invalid.
            model = new GameObject(prefab.name + " (Equipment)");
            model.SetActive(false); model.transform.SetParent(carrier, false);
            GameObject instance = Object.Instantiate(prefab, model.transform, false);
            instance.transform.localPosition = Vector3.zero; instance.transform.localRotation = Quaternion.identity;
            instance.SetActive(true);
            foreach (ParentConstraint constraint in model.GetComponentsInChildren<ParentConstraint>(true)) constraint.enabled = false;
            return true;
        }
        private void UnbindModel()
        {
            grip = null;
            Version = ++nextVersion;
            if (equipmentModel == null) return;
            equipmentModel.SetActive(false); Object.Destroy(equipmentModel); equipmentModel = null;
        }
        internal void SetVisible(bool value)
        {
            if (visible == value) return;
            visible = value;
            if (value && View == null) attempted = false;
            if (equipmentModel != null) equipmentModel.SetActive(value);
        }
        internal void Refresh(RuntimeItem confirmedItem)
        {
            bool rebound = !ReferenceEquals(item, confirmedItem);
            if (rebound)
            {
                // 库存事务发布新的不可变候选实例；同一武器继续使用原模型和动作表现。
                bool sameIdentity = item != null && confirmedItem != null && item.ItemId == confirmedItem.ItemId
                    && item.ItemType.Equals(confirmedItem.ItemType);
                if (!sameIdentity) Release();
                item = confirmedItem;
            }
            if (!visible || item == null) return;
            bool available = carrier != null && carrier.gameObject.activeInHierarchy;
            if (available != rigAvailable)
            {
                rigAvailable = available;
                if (available) attempted = false;
            }
            if (!available) return;
            if (View != null)
            {
                if (rebound || revision != item.Revision)
                {
                    View.ApplyItemState(item);
                    Version = ++nextVersion;
                }
                revision = item.Revision;
                return;
            }
            if (attempted && revision == item.Revision) return;
            attempted = true;
            revision = item.Revision;
            GameObject model = null;
            try
            {
                GameObject prefab = item.GetFragment<EquipmentFragment>()?.EquipPrefab;
                if (!TryPrepare(prefab, out model)) return;
                UnbindModel(); equipmentModel = model; model.SetActive(true);
                View = model.GetComponentInChildren<WeaponView>(true);
                View.Bind(item);
                grip = View.SupportHandGrip;
                if (grip == null)
                    foreach (Transform candidate in model.GetComponentsInChildren<Transform>(true))
                        if (candidate.name == "L_Hand_Position") { grip = candidate; break; }
                model.SetActive(visible);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
                if (model != null) Object.Destroy(model);
                View?.Unbind();
                View = null;
                UnbindModel();
            }
        }
        internal void Release()
        {
            View?.Unbind();
            View = null;
            UnbindModel();
            item = null;
            attempted = false;
            revision = -1;
        }
    }

    /// <summary>Two equipment slots keep the executing model alive while next frame's geometry is prepared.</summary>
    internal sealed class CharacterEquipmentFrames
    {
        private readonly CharacterEquipmentPresentation first, second;
        private CharacterEquipmentPresentation active, planned;
        private RuntimeItem requested;
        internal CharacterEquipmentPresentation Active => active;
        internal CharacterEquipmentFrames(Transform carrier)
        {
            first = new CharacterEquipmentPresentation(carrier);
            second = new CharacterEquipmentPresentation(carrier);
        }
        internal void Request(RuntimeItem item) => requested = item;
        internal CharacterEquipmentPresentation BeginFrame(ulong identity, uint version, bool hasEquipment,
            ulong? requiredIdentity = null)
        {
            active = hasEquipment && first.Identity == identity && first.Version == version ? first
                : hasEquipment && second.Identity == identity && second.Version == version ? second : null;
            first.SetVisible(active == first); second.SetVisible(active == second);
            RuntimeItem next = requested;
            if (requiredIdentity.HasValue && (next != null ? next.ItemId.Value : 0) != requiredIdentity.Value)
            {
                // Public equipment and the business snapshot can arrive separately. Preserve a
                // retained matching projection until both producers agree on the weapon identity.
                if (active != null && active.Identity == requiredIdentity.Value) return planned = active;
                if (first.Identity == requiredIdentity.Value && first.View != null) return planned = first;
                if (second.Identity == requiredIdentity.Value && second.View != null) return planned = second;
                next = null;
            }
            if (active != null && active.Matches(next)) return planned = active;
            planned = active == first ? second : first;
            // Building the hidden slot never replaces the model referenced by the current animation/IK plan.
            planned.SetVisible(true);
            planned.Refresh(next);
            planned.SetVisible(false);
            return planned;
        }
        internal void SetVisible(bool visible)
        {
            first.SetVisible(visible && active == first);
            second.SetVisible(visible && active == second);
        }
        internal void Release()
        {
            first.Release(); second.Release(); active = planned = null; requested = null;
        }
    }
}
