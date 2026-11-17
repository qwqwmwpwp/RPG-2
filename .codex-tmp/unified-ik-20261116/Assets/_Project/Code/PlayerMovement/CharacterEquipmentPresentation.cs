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
        internal WeaponView View { get; private set; }
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
            position = grip != null ? grip.position : Vector3.zero;
            rotation = grip != null ? grip.rotation : Quaternion.identity;
            return grip != null && equipmentModel != null && equipmentModel.activeInHierarchy;
        }
        private bool TryPrepare(GameObject prefab, out GameObject model)
        {
            model = null;
            if (carrier == null || prefab == null) return false;
            WeaponView source = prefab.GetComponentInChildren<WeaponView>(true);
            if (source == null || source.Muzzle == null) return false;
            bool hasGrip = source.SupportHandGrip != null;
            if (!hasGrip)
                foreach (Transform candidate in prefab.GetComponentsInChildren<Transform>(true))
                    if (candidate.name == "L_Hand_Position") { hasGrip = true; break; }
            if (!hasGrip) return false;
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
                if (rebound || revision != item.Revision) View.ApplyItemState(item);
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
}
