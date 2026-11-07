using RPG4.Backpack.Items;
using RPG4.Combat;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>只读装备模型投影；大厅和局内共用创建、配件绑定、刷新及释放。</summary>
    internal sealed class CharacterEquipmentPresentation
    {
        private readonly PlayerWeaponRig rig;
        private RuntimeItem item;
        private bool attempted, rigAvailable, visible = true;
        private int revision = -1;
        internal WeaponView View { get; private set; }
        internal CharacterEquipmentPresentation(PlayerWeaponRig rig)
        {
            this.rig = rig;
            rig?.PrepareEquipment();
        }
        internal void SetVisible(bool value)
        {
            if (visible == value) return;
            visible = value;
            if (value && View == null) attempted = false;
            rig?.SetVisible(value);
        }
        internal void Refresh(RuntimeItem confirmedItem)
        {
            if (!ReferenceEquals(item, confirmedItem))
            {
                Release();
                item = confirmedItem;
            }
            if (!visible || item == null) return;
            bool available = rig != null && rig.isActiveAndEnabled;
            if (available != rigAvailable)
            {
                rigAvailable = available;
                if (available) attempted = false;
            }
            if (!available) return;
            if (View != null)
            {
                if (revision != item.Revision) View.ApplyItemState(item);
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
                if (!rig.TryPrepareEquipment(prefab, out model, out _)) return;
                rig.BindEquipment(model);
                View = model.GetComponentInChildren<WeaponView>(true);
                View.Bind(item);
                rig.SetVisible(visible);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
                if (model != null) Object.Destroy(model);
                View?.Unbind();
                View = null;
                rig?.UnbindEquipment();
            }
        }
        internal void Release()
        {
            View?.Unbind();
            View = null;
            rig?.UnbindEquipment();
            item = null;
            attempted = false;
            revision = -1;
        }
    }
}
