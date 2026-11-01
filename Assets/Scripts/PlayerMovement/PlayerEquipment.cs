using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Equipment;
using RPG4.Backpack.Items;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Runtime;
using RPG4.Combat;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>武器槽与游戏执行适配；武器表现统一由 PlayerPresenter 管理。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(BackpackInventory), typeof(EquipmentInventory))]
    public sealed partial class PlayerEquipment : MonoBehaviour
    {
        [SerializeField] private BackpackInventory inventory;
        [SerializeField] private EquipmentInventory equipmentInventory;
        [SerializeField] private PlayerWeaponRig weaponRig;
        private readonly Dictionary<ItemId, Weapon> weapons = new Dictionary<ItemId, Weapon>();
        private readonly List<RuntimeItem> items = new List<RuntimeItem>();
        private EventSubscription equipmentChanged;
        private object eventSource;
        private Player player;
        private Weapon current;
        private Weapon preparedWeapon;
        private RuntimeItem preparedItem;
        private bool confirming;
        private bool synchronizing;
        private readonly WeaponShotExecutor shotExecutor = new WeaponShotExecutor();
        private readonly Queue<AcceptedShot> pendingShots = new Queue<AcceptedShot>(32);
        private readonly struct AcceptedShot
        {
            internal AcceptedShot(WeaponShotRequest request, Vector3 position) { Request = request; Position = position; }
            internal WeaponShotRequest Request { get; }
            internal Vector3 Position { get; }
        }
        private bool canExecuteAuthority = true;
        private bool deferShots;
        private RuntimeItem replicatedEquipment;
        public bool CanExecuteAuthority => canExecuteAuthority;
        /// <summary>远端与预测角色只能读状态。切换权限不会重放已经消费的射击。</summary>
        public void ConfigureAuthority(bool canExecute, bool deferShots = false)
        {
            canExecuteAuthority = canExecute;
            this.deferShots = deferShots;
            if (!canExecute) { current?.CancelAction(); pendingShots.Clear(); }
        }
        /// <summary>网络公开装备的只读投影，仅供模型/配件表现；不创建可消费弹药的武器。</summary>
        public void ApplyReplicatedEquipment(RuntimeItem item)
        {
            if (canExecuteAuthority) return;
            replicatedEquipment = item;
            RefreshReplicatedEquipmentView();
        }
        /// <summary>权威结算完成后的表现通知；订阅者不能决定扣弹或伤害是否成功。</summary>
        public event System.Action<WeaponShotRequest, IReadOnlyList<WeaponHitResult>> ShotResolved;
        public IReadOnlyList<WeaponHitResult> LastShotHits { get; private set; } = System.Array.Empty<WeaponHitResult>();

        public ItemId CurrentItemId => CurrentWeapon != null ? current.ItemId : ItemId.None;
        public Weapon CurrentWeapon => canExecuteAuthority && RuntimeAvailable && current != null
            && ReferenceEquals(current.Item, equipmentInventory.EquippedItem) ? current : null;
        private bool InventoryAvailable => inventory != null && inventory.gameObject == gameObject
            && inventory.isActiveAndEnabled && inventory.IsInitialized;
        private bool EquipmentAvailable => equipmentInventory != null && equipmentInventory.gameObject == gameObject
            && equipmentInventory.isActiveAndEnabled && equipmentInventory.IsReady;
        internal PlayerWeaponRig WeaponRig => weaponRig;
        private bool RuntimeAvailable => isActiveAndEnabled && EquipmentAvailable
            && (player == null || player.isActiveAndEnabled);

        private void Awake()
        {
            if (inventory == null) inventory = GetComponent<BackpackInventory>();
            if (equipmentInventory == null) equipmentInventory = GetComponent<EquipmentInventory>();
            if (weaponRig == null) weaponRig = GetComponent<PlayerWeaponRig>();
            player = GetComponent<Player>();
            if (equipmentInventory != null && equipmentInventory.gameObject == gameObject)
            {
                equipmentInventory.AdditionalAdmission = CanUseItem;
                equipmentInventory.CommitConfirm = ConfirmEquipment;
            }
        }

        private void OnEnable() => SynchronizeEquipment();

        private void Start()
        {
            SynchronizeEquipment();
            if (!canExecuteAuthority || !InventoryAvailable || !EquipmentAvailable || equipmentInventory.EquippedItem != null) return;
            inventory.CopyItems(items); // CopyItems 的契约为 ItemId 升序。
            foreach (RuntimeItem item in items)
                if (TryEquip(item.ItemId, out _)) break;
            items.Clear();
        }

        private void Update() => SynchronizeEquipment();
        private void LateUpdate()
        {
            SynchronizeEquipment();
            FlushWeaponUi();
        }

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
            BindWeaponUiEvents();
            if (synchronizing || confirming) return;
            synchronizing = true;
            try
            {
                BindEquipmentEvents(); // OnEnable 可能早于装备容器 Awake。
                if (!canExecuteAuthority)
                {
                    ClearPreparedEquipment();
                    if (current != null) ReleaseWeapon();
                    RefreshReplicatedEquipmentView();
                    return;
                }
                RuntimeItem item = EquipmentAvailable ? equipmentInventory.EquippedItem : null;
                if (!RuntimeAvailable || item == null)
                {
                    ClearPreparedEquipment();
                    ReleaseWeapon();
                    return;
                }
                if (current != null && ReferenceEquals(current.Item, item))
                {
                    ClearPreparedEquipment();
                    RefreshItemView();
                    return;
                }
                if (!ReferenceEquals(preparedItem, item) || preparedWeapon == null)
                {
                    // 恢复组件只重建槽内运行实例，不重新向背包索取物品。
                    if (!ConfirmEquipment(item.ItemId, out _))
                    {
                        ReleaseWeapon();
                        return;
                    }
                }
                Weapon next = preparedWeapon;
                next.Rebind(item);
                preparedWeapon = null;
                preparedItem = null;
                ReleaseWeapon();
                weapons[item.ItemId] = next;
                current = next;
                BindWeaponUiAction();
                RefreshItemView();
            }
            finally { synchronizing = false; }
        }

        // 事务只验证业务条件与运行定义；模型和挂点不是装备成功条件。
        private bool CanUseItem(RuntimeItem item, out string error)
        {
            error = null;
            if (!RuntimeAvailable)
                error = "角色或武器槽未就绪。";
            else if (!InventoryAvailable)
                error = "角色背包尚未就绪。";
            else if (item == null || item.GetFragment<WeaponDefinitionFragment>() == null)
                error = "物品缺少武器定义。";
            else
            {
                try { item.GetFragment<WeaponDefinitionFragment>().Validate(); }
                catch (System.ArgumentException exception) { error = exception.Message; return false; }
                catch (System.InvalidOperationException exception) { error = exception.Message; return false; }
                return true;
            }
            return false;
        }

        // 事务已静默提交；仅暂存候选，Changed 之前保留旧武器、动作和模型。
        private bool ConfirmEquipment(ItemId requiredItem, out string error)
        {
            error = null;
            if (!canExecuteAuthority) { ClearPreparedEquipment(); return true; }
            if (confirming) { error = "武器运行实例正在准备中。"; return false; }
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
                if (!CanUseItem(item, out error)) return false;
                if (current != null && ReferenceEquals(current.Item, item)) return true;
                Weapon candidate;
                try
                {
                    Weapon validated = new Weapon(item, inventory.Catalog, SelectReloadMagazine, CommitReloadMagazine);
                    candidate = weapons.TryGetValue(item.ItemId, out Weapon cached) ? cached : validated;
                }
                catch (System.ArgumentException exception) { error = exception.Message; return false; }
                catch (System.InvalidOperationException exception) { error = exception.Message; return false; }
                if (!RuntimeAvailable || !ReferenceEquals(equipmentInventory.EquippedItem, item))
                {
                    error = "准备武器时角色或装备槽状态发生变化。";
                    return false;
                }
                preparedWeapon = candidate;
                preparedItem = item;
                return true;
            }
            finally { confirming = false; }
        }

        public bool TryEquip(ItemId itemId, out string error)
        {
            error = null;
            if (!canExecuteAuthority) error = "装备操作需要权威执行权限。";
            else if (confirming || synchronizing) error = "装备正在更新中。";
            else if (!isActiveAndEnabled || !EquipmentAvailable || !InventoryAvailable)
                error = "需要本角色已启用且初始化的背包与武器槽。";
            else if (equipmentInventory.EquippedItem?.ItemId == itemId) return true;
            else return equipmentInventory.TryEquip(inventory, itemId, out error);
            return false;
        }

        public bool TryUnequip(out string error)
        {
            error = null;
            if (!canExecuteAuthority) error = "装备操作需要权威执行权限。";
            else if (confirming || synchronizing) error = "装备正在更新中。";
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
            if (current == null || player == null || player.Presenter == null) return;
            try
            {
                player.Presenter.RefreshEquipment(current.Item);
            }
            catch (System.Exception exception) { Debug.LogException(exception, this); }
        }

        private void RefreshReplicatedEquipmentView()
        {
            if (player == null || player.Presenter == null) return;
            try { player.Presenter.RefreshEquipment(replicatedEquipment); }
            catch (System.Exception exception) { Debug.LogException(exception, this); }
        }

        private bool SelectReloadMagazine(out ItemId magazineId)
        {
            magazineId = ItemId.None;
            if (!canExecuteAuthority || !InventoryAvailable || !EquipmentAvailable || current == null) return false;
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
            return canExecuteAuthority && InventoryAvailable && EquipmentAvailable && current != null
                && inventory.TryExchangeWeaponMagazine(equipmentInventory.Container,
                    current.ItemId, magazineId, out error);
        }

        public bool TryLoadMagazine(ItemId magazineId, ItemId ammoId, int roundCount, out string error)
        {
            error = "角色背包尚未就绪。";
            return canExecuteAuthority && InventoryAvailable && inventory.TryLoadMagazine(magazineId, ammoId, roundCount, out error);
        }

        public bool TryInstallAttachment(WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
        {
            error = "当前武器尚未就绪，或正在换弹。";
            return canExecuteAuthority && InventoryAvailable && EquipmentAvailable && CurrentWeapon != null && !current.IsReloading
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

        /// <summary>同武器换匣、丢弃等权威事务前调用；已提交弹匣不回滚，已接受射击不撤销。</summary>
        public void CancelWeaponActionForTransaction() => current?.CancelAction();

        /// <summary>仅关联当前武器、已装弹匣或换弹候选的操作能中断当前动作。</summary>
        public void PrepareWeaponOperation(WeaponOperation operation)
        {
            Weapon weapon = CurrentWeapon;
            if (!canExecuteAuthority || weapon == null) return;
            ItemId attached = weapon.Item.WeaponState.Magazine?.ItemId ?? ItemId.None;
            ItemId candidate = weapon.ReloadMagazineId;
            bool Related(ItemId id) => id.IsValid && (id == weapon.ItemId || id == attached || id == candidate);
            if (Related(operation.TargetItemId) || Related(operation.CandidateItemId)) weapon.CancelAction();
        }

        internal bool TryFire(bool pressed, bool held)
        {
            Weapon weapon = CurrentWeapon;
            if (!canExecuteAuthority || pendingShots.Count >= 32 || player == null || !player.IsAlive || player.ReadState == null
                || !player.ReadState.Weapon.CanUseWeapon || weapon == null) return false;
            Ray aim = player.GetAimRay();
            player.GetShotOrigins(aim.direction, out Vector3 bodyOrigin, out Vector3 muzzleOrigin);
            if (!WeaponShotExecutor.IsValidContext(aim.origin, aim.direction, bodyOrigin, muzzleOrigin)) return false;
            if (!weapon.TryFire(pressed, held, out WeaponShot shot)) return false;
            var request = new WeaponShotRequest(shot, gameObject, aim.origin, aim.direction, bodyOrigin, muzzleOrigin);
            pendingShots.Enqueue(new AcceptedShot(request, transform.position));
            player.ApplyShotRecoil(shot.RecoilPitch, shot.RecoilYaw);
            if (!deferShots) FlushPendingShots();
            return true;
        }

        /// <summary>全部权威移动与装备事务完成后调用；rewindTime 由服务器已接受的输入 tick 推导。</summary>
        public void FlushPendingShots(WeaponShotExecutor executor = null, ServerCombatHistory history = null,
            double rewindTime = double.NaN)
        {
            if (!canExecuteAuthority) return;
            executor = executor ?? shotExecutor;
            while (pendingShots.Count != 0)
            {
                AcceptedShot accepted = pendingShots.Dequeue();
                Vector3 displacement = transform.position - accepted.Position;
                WeaponShotRequest original = accepted.Request;
                // 上身接受射击早于本 tick motor；只平移逻辑起点，保留扣弹时方向，不能使用后坐力后的 aim。
                var request = new WeaponShotRequest(original.Shot, original.Shooter,
                    original.AimOrigin + displacement, original.AimDirection,
                    original.BodyOrigin + displacement, original.MuzzleOrigin + displacement);
                // 先出队。结算/表现中的同步回调不能再次提交同一发。
                LastShotHits = executor.Execute(request, history, rewindTime);
                if (LastShotHits.Count == 0) continue;
                RecordResolvedShot(request);
                if (ShotResolved == null) continue;
                foreach (System.Action<WeaponShotRequest, IReadOnlyList<WeaponHitResult>> listener in ShotResolved.GetInvocationList())
                {
                    try { listener(request, LastShotHits); }
                    catch (System.Exception exception) { Debug.LogException(exception, this); }
                }
            }
        }

        private void RecordResolvedShot(WeaponShotRequest request)
        {
            bool damageApplied = false, obstructed = false;
            foreach (WeaponHitResult hit in LastShotHits)
            {
                damageApplied |= hit.DamageApplied;
                obstructed |= hit.MuzzleObstructed;
            }
            player?.RecordShot(request.Shot.WeaponId.Value, request.Shot.Sequence, damageApplied, obstructed);
        }

        private void ClearPreparedEquipment()
        {
            preparedItem = null;
            preparedWeapon = null;
        }

        private void ReleaseWeapon()
        {
            bool hadWeapon = current != null;
            current?.CancelAction();
            try { player?.Presenter?.ReleaseEquipment(); }
            catch (System.Exception exception) { Debug.LogException(exception, this); }
            current = null;
            BindWeaponUiAction();
            if (hadWeapon) player?.CancelWeaponAction();
        }

        private void OnDisable()
        {
            pendingShots.Clear();
            UnbindWeaponUiEvents();
            equipmentChanged.Dispose();
            eventSource = null;
            ClearPreparedEquipment();
            ReleaseWeapon();
        }

        private void OnDestroy()
        {
            shotExecutor.ForgetShooter(gameObject);
            if (equipmentInventory == null) return;
            if (equipmentInventory.AdditionalAdmission == CanUseItem) equipmentInventory.AdditionalAdmission = null;
            if (equipmentInventory.CommitConfirm == ConfirmEquipment) equipmentInventory.CommitConfirm = null;
        }
    }
}
