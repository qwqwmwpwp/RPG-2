using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Equipment;
using RPG4.Backpack.Items;
using RPG4.Backpack.Runtime;
using RPG4.Combat;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>独立武器槽的角色表现；武器运行时按物品实例身份保留冷却与历史。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(BackpackInventory), typeof(EquipmentInventory))]
    public sealed class PlayerEquipment : MonoBehaviour
    {
        [SerializeField] private BackpackInventory inventory;
        [SerializeField] private EquipmentInventory equipmentInventory;
        [SerializeField] private PlayerWeaponRig weaponRig;
        [SerializeField] private Camera shootingCamera;
        private readonly Dictionary<ItemId, Weapon> weapons = new Dictionary<ItemId, Weapon>();
        private readonly List<RuntimeItem> items = new List<RuntimeItem>();
        private EventSubscription equipmentChanged;
        private object eventSource;
        private Player player;
        private Weapon current;
        private Weapon preparedWeapon;
        private RuntimeItem preparedItem;
        private GameObject preparedModel;
        private bool confirming;
        private bool synchronizing;
        private WeaponView weaponView;
        private int appliedItemRevision = -1;
        private readonly WeaponShotExecutor shotExecutor = new WeaponShotExecutor();
        public IReadOnlyList<WeaponHitResult> LastShotHits { get; private set; } = System.Array.Empty<WeaponHitResult>();

        public ItemId CurrentItemId => CurrentWeapon != null ? current.ItemId : ItemId.None;
        public Weapon CurrentWeapon => PresentationAvailable && current != null && weaponRig.HasEquipment
            && ReferenceEquals(current.Item, equipmentInventory.EquippedItem) ? current : null;
        private bool InventoryAvailable => inventory != null && inventory.gameObject == gameObject
            && inventory.isActiveAndEnabled && inventory.IsInitialized;
        private bool EquipmentAvailable => equipmentInventory != null && equipmentInventory.gameObject == gameObject
            && equipmentInventory.isActiveAndEnabled && equipmentInventory.IsReady;
        private bool PresentationAvailable => isActiveAndEnabled && EquipmentAvailable
            && (player == null || player.isActiveAndEnabled) && weaponRig != null && weaponRig.CanBindEquipment;

        private void Awake()
        {
            if (inventory == null) inventory = GetComponent<BackpackInventory>();
            if (equipmentInventory == null) equipmentInventory = GetComponent<EquipmentInventory>();
            if (weaponRig == null) weaponRig = GetComponent<PlayerWeaponRig>();
            player = GetComponent<Player>();
            if (equipmentInventory != null && equipmentInventory.gameObject == gameObject)
            {
                equipmentInventory.AdditionalAdmission = CanPresentItem;
                equipmentInventory.CommitConfirm = ConfirmEquipment;
            }
            weaponRig?.PrepareEquipment();
        }

        private void OnEnable() => SynchronizeEquipment();

        private void Start()
        {
            SynchronizeEquipment();
            if (!InventoryAvailable || !EquipmentAvailable || equipmentInventory.EquippedItem != null) return;
            inventory.CopyItems(items); // CopyItems 的契约为 ItemId 升序。
            foreach (RuntimeItem item in items)
                if (TryEquip(item.ItemId, out _)) break;
            items.Clear();
        }

        private void Update() => SynchronizeEquipment();
        private void LateUpdate() => SynchronizeEquipment();

        private void BindEquipmentEvents()
        {
            object source = EquipmentAvailable ? equipmentInventory.EventSource : null;
            if (ReferenceEquals(eventSource, source) && (source == null || equipmentChanged.IsAlive)) return;
            equipmentChanged.Dispose();
            eventSource = source;
            if (source != null)
                equipmentChanged = EventBus.Global.Subscribe(InvChannels.Changed, source, this, SynchronizeEquipment);
        }

        private void SynchronizeEquipment()
        {
            if (synchronizing || confirming) return;
            synchronizing = true;
            try
            {
                BindEquipmentEvents(); // OnEnable 可能早于装备容器 Awake。
                RuntimeItem item = EquipmentAvailable ? equipmentInventory.EquippedItem : null;
                if (!PresentationAvailable || item == null)
                {
                    ClearPreparedEquipment();
                    ReleasePresentation();
                    return;
                }
                if (current != null && ReferenceEquals(current.Item, item) && weaponRig.HasEquipment)
                {
                    ClearPreparedEquipment();
                    RefreshItemView();
                    return;
                }
                if (!ReferenceEquals(preparedItem, item) || preparedModel == null)
                {
                    // 恢复组件只重建槽内实例的表现，不重新向背包索取物品。
                    if (!ConfirmEquipment(item.ItemId, out _))
                    {
                        ReleasePresentation();
                        return;
                    }
                }
                Weapon next = preparedWeapon;
                GameObject model = preparedModel;
                next.Rebind(item);
                preparedWeapon = null;
                preparedItem = null;
                preparedModel = null;
                ReleasePresentation();
                weapons[item.ItemId] = next;
                current = next;
                weaponRig.BindEquipment(model);
                weaponView = model.GetComponentInChildren<WeaponView>(true);
                if (weaponView != null) weaponView.Bind(item);
                appliedItemRevision = item.Revision;
            }
            finally { synchronizing = false; }
        }

        // 准入可以被拖拽预览反复调用；只检查定义和 prefab，不创建武器或模型。
        private bool CanPresentItem(RuntimeItem item, out string error)
        {
            error = null;
            if (!PresentationAvailable)
                error = "角色或武器槽未就绪，或未配置稳定武器挂点与左手目标。";
            else if (item == null || item.GetFragment<WeaponDefinitionFragment>() == null)
                error = "物品缺少武器定义。";
            else
            {
                try { item.GetFragment<WeaponDefinitionFragment>().Validate(); }
                catch (System.ArgumentException exception) { error = exception.Message; return false; }
                catch (System.InvalidOperationException exception) { error = exception.Message; return false; }
                EquipmentFragment equipment = item.GetFragment<EquipmentFragment>();
                return weaponRig.CanPrepareEquipment(equipment?.EquipPrefab, out error);
            }
            return false;
        }

        // 事务已静默提交；仅暂存候选，Changed 之前保留旧武器、动作和模型。
        private bool ConfirmEquipment(ItemId requiredItem, out string error)
        {
            error = null;
            if (confirming) { error = "武器表现正在准备中。"; return false; }
            confirming = true;
            try
            {
                ClearPreparedEquipment();
                // None 同时用于卸装与回滚补偿；都不能提前撤下旧表现。
                if (!requiredItem.IsValid) return true;
                RuntimeItem item = equipmentInventory != null ? equipmentInventory.EquippedItem : null;
                if (item == null || item.ItemId != requiredItem)
                {
                    error = "武器槽中没有待确认的物品。";
                    return false;
                }
                if (!CanPresentItem(item, out error)) return false;
                if (current != null && ReferenceEquals(current.Item, item) && weaponRig.HasEquipment) return true;
                Weapon candidate;
                try
                {
                    Weapon validated = new Weapon(item, inventory.Catalog, SelectReloadMagazine, CommitReloadMagazine);
                    candidate = weapons.TryGetValue(item.ItemId, out Weapon cached) ? cached : validated;
                }
                catch (System.ArgumentException exception) { error = exception.Message; return false; }
                catch (System.InvalidOperationException exception) { error = exception.Message; return false; }
                if (!weaponRig.TryPrepareEquipment(item.GetFragment<EquipmentFragment>().EquipPrefab,
                    out GameObject model, out error)) return false;
                if (!PresentationAvailable || !ReferenceEquals(equipmentInventory.EquippedItem, item))
                {
                    Destroy(model);
                    error = "准备武器时角色或装备槽状态发生变化。";
                    return false;
                }
                preparedWeapon = candidate;
                preparedItem = item;
                preparedModel = model;
                return true;
            }
            finally { confirming = false; }
        }

        public bool TryEquip(ItemId itemId, out string error)
        {
            error = null;
            if (confirming || synchronizing) error = "装备正在更新中。";
            else if (!isActiveAndEnabled || !EquipmentAvailable || !InventoryAvailable)
                error = "需要本角色已启用且初始化的背包与武器槽。";
            else if (equipmentInventory.EquippedItem?.ItemId == itemId) return true;
            else return equipmentInventory.TryEquip(inventory, itemId, out error);
            return false;
        }

        public bool TryUnequip(out string error)
        {
            error = null;
            if (confirming || synchronizing) error = "装备正在更新中。";
            else if (!isActiveAndEnabled || !EquipmentAvailable || !InventoryAvailable)
                error = "需要本角色已启用且初始化的背包与武器槽。";
            else return equipmentInventory.TryUnequip(inventory, out error);
            return false;
        }

        public void Unequip()
        {
            if (!TryUnequip(out string error)) Debug.LogWarning($"卸装失败：{error}", this);
        }

        private void RefreshItemView()
        {
            if (current == null || weaponView == null || appliedItemRevision == current.Item.Revision) return;
            try
            {
                weaponView.ApplyItemState(current.Item);
                appliedItemRevision = current.Item.Revision;
            }
            catch (System.Exception exception) { Debug.LogException(exception, this); }
        }

        private bool SelectReloadMagazine(out ItemId magazineId)
        {
            magazineId = ItemId.None;
            if (!InventoryAvailable || !EquipmentAvailable || current == null) return false;
            inventory.CopyItems(items);
            int bestCount = -1;
            foreach (RuntimeItem candidate in items)
            {
                if (candidate.MagazineState == null || candidate.MagazineState.RoundCount <= bestCount
                    || candidate.MagazineState.RoundCount == 0
                    || !current.Item.WeaponState.IsCompatibleMagazine(candidate, inventory.Catalog)
                    || !inventory.CanExchangeWeaponMagazine(equipmentInventory.Container, current.ItemId,
                        candidate.ItemId, out _)) continue;
                bestCount = candidate.MagazineState.RoundCount;
                magazineId = candidate.ItemId;
            }
            return magazineId.IsValid;
        }

        private bool CommitReloadMagazine(ItemId magazineId, out string error)
        {
            error = "角色背包或当前武器尚未就绪。";
            return InventoryAvailable && EquipmentAvailable && current != null
                && inventory.TryExchangeWeaponMagazine(equipmentInventory.Container,
                    current.ItemId, magazineId, out error);
        }

        public bool TryLoadMagazine(ItemId magazineId, ItemId ammoId, int roundCount, out string error)
        {
            error = "角色背包尚未就绪。";
            return InventoryAvailable && inventory.TryLoadMagazine(magazineId, ammoId, roundCount, out error);
        }

        public bool TryInstallAttachment(WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
        {
            error = "当前武器尚未就绪，或正在换弹。";
            return InventoryAvailable && EquipmentAvailable && CurrentWeapon != null && !current.IsReloading
                && inventory.TryExchangeWeaponAttachment(equipmentInventory.Container,
                    current.ItemId, slot, attachmentId, out error);
        }

        public bool TryRemoveAttachment(WeaponAttachmentSlot slot, out string error)
            => TryInstallAttachment(slot, ItemId.None, out error);

        public bool TryExchangeMagazine(ItemId magazineId, out string error)
        {
            error = "当前武器尚未就绪，或正在换弹。";
            return CurrentWeapon != null && !current.IsReloading && CommitReloadMagazine(magazineId, out error);
        }

        internal bool TryFire(bool pressed, bool held)
        {
            Weapon weapon = CurrentWeapon;
            if (player == null || !player.IsAlive || player.Blackboard == null || !player.Blackboard.CanUseWeapon
                || weapon == null || weaponView == null || weaponView.Muzzle == null) return false;
            WeaponView shotView = weaponView;
            Camera camera = shootingCamera != null ? shootingCamera : Camera.main;
            if (camera == null || !camera.isActiveAndEnabled) return false;
            Ray aim = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f));
            Vector3 bodyOrigin = shotView.transform.position;
            Vector3 muzzleOrigin = shotView.Muzzle.position;
            if (!WeaponShotExecutor.IsValidContext(aim.origin, aim.direction, bodyOrigin, muzzleOrigin)) return false;
            if (!weapon.TryFire(pressed, held, out WeaponShot shot)) return false;
            var request = new WeaponShotRequest(shot, gameObject, aim.origin, aim.direction, bodyOrigin, muzzleOrigin);
            // 命中与扣弹完成之后才触发表现；可选特效失败不能阻止伤害。
            LastShotHits = shotExecutor.Execute(request);
            player.ApplyShotRecoil(shot.RecoilPitch, shot.RecoilYaw);
            try
            {
                if (shotView != null) shotView.PlayShot(shot);
                RefreshItemView();
            }
            catch (System.Exception exception) { Debug.LogException(exception, this); }
            return true;
        }

        private void ClearPreparedEquipment()
        {
            if (preparedModel != null) Destroy(preparedModel);
            preparedModel = null;
            preparedItem = null;
            preparedWeapon = null;
        }

        private void ReleasePresentation()
        {
            bool hadWeapon = current != null;
            current?.CancelAction();
            if (weaponView != null) weaponView.Unbind();
            weaponView = null;
            appliedItemRevision = -1;
            current = null;
            if (hadWeapon) player?.CancelWeaponAction();
            weaponRig?.UnbindEquipment();
        }

        private void OnDisable()
        {
            equipmentChanged.Dispose();
            eventSource = null;
            ClearPreparedEquipment();
            ReleasePresentation();
        }

        private void OnDestroy()
        {
            if (equipmentInventory == null) return;
            if (equipmentInventory.AdditionalAdmission == CanPresentItem) equipmentInventory.AdditionalAdmission = null;
            if (equipmentInventory.CommitConfirm == ConfirmEquipment) equipmentInventory.CommitConfirm = null;
        }
    }
}
