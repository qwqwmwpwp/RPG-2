using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Equipment;
using RPG4.Backpack.Items;
using RPG4.LoadoutContracts;
using RPG4.LoadoutTemplate;
using RPG4.Combat;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>新角色库存的战斗适配；物品状态由 Role 持有，客户端仅使用只读投影。</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(100)]
    public sealed partial class PlayerEquipment : MonoBehaviour
    {
        [SerializeField] private RoleLoadoutController role;
        private Player player;
        private Weapon current;
        private bool requestedAuthority, authorityConfigured, deferShots, synchronizing;
        private RuntimeItem replicatedEquipment, localPresentationWeapon;
        private readonly List<RuntimeItem> items = new List<RuntimeItem>();
        private readonly WeaponShotExecutor shotExecutor = new WeaponShotExecutor();
        private readonly Queue<AcceptedShot> pendingShots = new Queue<AcceptedShot>(32);
        private static readonly LoadoutSlotId[] weaponSlots = { LoadoutSlotId.PrimaryWeapon1,
            LoadoutSlotId.PrimaryWeapon2, LoadoutSlotId.SecondaryWeapon, LoadoutSlotId.MeleeWeapon };
        private readonly struct AcceptedShot
        {
            internal AcceptedShot(WeaponShotRequest request, Vector3 position) { Request = request; Position = position; }
            internal WeaponShotRequest Request { get; }
            internal Vector3 Position { get; }
        }
        public RoleLoadoutController Role => role;
        public bool CanExecuteAuthority => requestedAuthority && role != null && role.IsAuthority;
        public Weapon CurrentWeapon => CanExecuteAuthority && RuntimeAvailable ? current : null;
        public ItemId CurrentItemId => CurrentWeapon?.ItemId ?? ItemId.None;
        private bool RuntimeAvailable => isActiveAndEnabled && role != null && role.isActiveAndEnabled
            && player != null && player.isActiveAndEnabled;
        public event System.Action<WeaponShotRequest, IReadOnlyList<WeaponHitResult>> ShotResolved;
        public IReadOnlyList<WeaponHitResult> LastShotHits { get; private set; } = System.Array.Empty<WeaponHitResult>();
        public void ConfigureAuthority(bool canExecute, bool deferShots = false)
        {
            authorityConfigured = true; requestedAuthority = canExecute; this.deferShots = deferShots;
            current?.CancelAction(); pendingShots.Clear();
            if (isActiveAndEnabled) SynchronizeEquipment();
        }
        public void ApplyReplicatedEquipment(RuntimeItem item)
        {
            if (CanExecuteAuthority) return;
            replicatedEquipment = item;
            SynchronizeEquipment();
        }
        public void SetLocalPresentationWeapon(RuntimeItem item) { localPresentationWeapon = item; SynchronizeEquipment(); }
        private RuntimeItem PresentationItem
        {
            get
            {
                if (role != null && role.TryGetCurrentWeapon(out RuntimeItem item, out _)) return item;
                return CanExecuteAuthority ? null : localPresentationWeapon ?? replicatedEquipment;
            }
        }
        internal bool TryGetPresentationWeapon(out ulong weaponId, out WeaponDefinitionFragment definition)
        {
            RuntimeItem item = PresentationItem;
            definition = item?.GetFragment<WeaponDefinitionFragment>();
            weaponId = item?.ItemId.Value ?? 0;
            return RuntimeAvailable && weaponId != 0 && definition != null;
        }
        internal bool TryGetPresentationRuntimeItem(out RuntimeItem item, out ItemManifestCatalog catalog)
        {
            item = PresentationItem; catalog = role?.ManifestCatalog;
            return RuntimeAvailable && item != null && item.ItemId.IsValid && item.WeaponState != null && catalog != null;
        }
        internal bool CanAnticipateShot => PresentationItem?.WeaponState != null && role?.ManifestCatalog != null
            && PresentationItem.WeaponState.CanConsumeShot(role.ManifestCatalog);
        internal ulong PresentationShotSequence => PresentationItem?.WeaponState?.ShotSequence ?? 0;
        private void Awake()
        {
            if (role == null) role = GetComponentInChildren<RoleLoadoutController>(true);
            player = GetComponent<Player>();
            if (!authorityConfigured) requestedAuthority = role != null && role.IsAuthority;
        }
        private void OnEnable()
        {
            if (role != null) { role.Changed += OnRoleChanged; role.Committed += OnLoadoutCommitted; }
            SynchronizeEquipment();
        }
        private void Start() => SynchronizeEquipment();
        private void Update() => SynchronizeEquipment();
        private void LateUpdate() { SynchronizeEquipment(); FlushWeaponUi(); }
        private void OnRoleChanged()
        {
            if (role != null && role.IsPublishingCommit) return;
            MarkWeaponUiDirty();
            SynchronizeEquipment();
        }
        private void OnLoadoutCommitted(LoadoutCommitRecord commit)
        {
            MarkWeaponUiDirty();
            if (commit.RuntimeItemsChanged || commit.SelectedWeaponChanged || TouchesWeaponSlot(commit))
                SynchronizeEquipment();
        }
        private bool TouchesWeaponSlot(LoadoutCommitRecord commit)
        {
            foreach (LoadoutSlotId slot in weaponSlots)
            {
                string endpointId = role.GetSlot(slot)?.Snapshot.Id;
                foreach (LoadoutEndpointChange change in commit.Changes)
                    if (change.EndpointId == endpointId) return true;
            }
            return false;
        }
        private void SynchronizeEquipment()
        {
            BindWeaponUiEvents();
            if (synchronizing || player == null) return;
            synchronizing = true;
            try
            {
                RuntimeItem item = PresentationItem;
                if (!CanExecuteAuthority)
                {
                    if (current != null) ReleaseWeapon();
                    RefreshItemView(item);
                    return;
                }
                if (!RuntimeAvailable || item?.WeaponState == null) { ReleaseWeapon(); return; }
                if (current != null && current.ItemId == item.ItemId)
                {
                    // 新库存事务替换候选实例，保持相同物品的动作时钟和射击序号关联。
                    if (!ReferenceEquals(current.Item, item)) current.Rebind(item);
                    RefreshItemView(item); return;
                }
                Weapon next;
                try
                {
                    ItemId id = item.ItemId;
                    next = new Weapon(item, role.ManifestCatalog, SelectReloadMagazine, CommitReloadMagazine,
                        (out BackpackTag ammoType, out AmmoDefinitionFragment ammo, out ulong sequence, out string error)
                            => ConsumeShot(id, out ammoType, out ammo, out sequence, out error),
                        (out string error) => ChamberRound(id, out error));
                }
                catch (System.ArgumentException exception) { Debug.LogWarning(exception.Message, this); ReleaseWeapon(); return; }
                catch (System.InvalidOperationException exception) { Debug.LogWarning(exception.Message, this); ReleaseWeapon(); return; }
                player.CancelConsumableUse();
                ReleaseWeapon(); current = next;
                BindWeaponUiAction(); RefreshItemView(item);
            }
            finally { synchronizing = false; }
        }
        private bool ConsumeShot(ItemId id, out BackpackTag ammoType, out AmmoDefinitionFragment ammo,
            out ulong sequence, out string error)
        {
            bool success = role.TryConsumeShot(id, out ammoType, out ammo, out sequence, out error);
            RebindCommittedWeapon(id); return success;
        }
        private bool ChamberRound(ItemId id, out string error)
        {
            bool success = role.TryChamberRound(id, out error);
            RebindCommittedWeapon(id); return success;
        }
        private void RebindCommittedWeapon(ItemId id)
        {
            if (current != null && current.ItemId == id && role.TryGetOwnedRuntimeItem(id, out RuntimeItem item)) current.Rebind(item);
            MarkWeaponUiDirty();
        }
        public bool TrySelectWeapon(LoadoutSlotId slot, out string error)
        {
            error = "装备操作需要权威执行权限。";
            return CanExecuteAuthority && RuntimeAvailable && role.TrySelectWeapon(slot, out error);
        }
        private void RefreshItemView(RuntimeItem item)
        {
            if (player?.Presenter == null) return;
            try { if (item == null) player.Presenter.ReleaseEquipment(); else player.Presenter.RefreshEquipment(item); }
            catch (System.Exception exception) { Debug.LogException(exception, this); }
        }
        private bool SelectReloadMagazine(out ItemId magazineId)
        {
            magazineId = ItemId.None;
            if (!CanExecuteAuthority || !RuntimeAvailable || current == null) return false;
            role.CopyOwnedRuntimeItems(items);
            int bestCount = -1;
            foreach (RuntimeItem candidate in items)
            {
                if (candidate.ParentItem != null || candidate.MagazineState == null || candidate.MagazineState.RoundCount <= bestCount
                    || candidate.MagazineState.RoundCount == 0 || !current.Item.WeaponState.IsCompatibleMagazine(candidate, role.ManifestCatalog)
                    || !role.CanExchangeWeaponMagazine(current.ItemId, candidate.ItemId, out _)) continue;
                bestCount = candidate.MagazineState.RoundCount; magazineId = candidate.ItemId;
            }
            return magazineId.IsValid;
        }
        private bool CommitReloadMagazine(ItemId magazineId, out string error)
        {
            error = "角色库存或当前武器尚未就绪。";
            if (!CanExecuteAuthority || !RuntimeAvailable || current == null) return false;
            ItemId id = current.ItemId;
            bool success = role.TryExchangeWeaponMagazine(id, magazineId, out error);
            RebindCommittedWeapon(id); return success;
        }
        public bool TryLoadMagazine(ItemId magazineId, ItemId ammoId, int roundCount, out string error)
        {
            error = "角色库存尚未就绪。";
            return CanExecuteAuthority && RuntimeAvailable && role.TryLoadMagazine(magazineId, ammoId, roundCount, out error);
        }
        public bool TryInstallAttachment(WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
        {
            error = "当前武器尚未就绪，或正在换弹。";
            return CurrentWeapon != null && !current.IsReloading
                && role.TryExchangeWeaponAttachment(current.ItemId, slot, attachmentId, out error);
        }
        public bool TryRemoveAttachment(WeaponAttachmentSlot slot, out string error) => TryInstallAttachment(slot, ItemId.None, out error);
        public bool TryExchangeMagazine(ItemId magazineId, out string error)
        {
            error = "当前武器尚未就绪，或正在换弹。";
            return CurrentWeapon != null && !current.IsReloading && CommitReloadMagazine(magazineId, out error);
        }
        public void CancelWeaponActionForTransaction() => current?.CancelAction();
        public void PrepareWeaponOperation(WeaponOperation operation)
        {
            Weapon weapon = CurrentWeapon;
            if (weapon == null) return;
            ItemId attached = weapon.Item.WeaponState.Magazine?.ItemId ?? ItemId.None;
            ItemId candidate = weapon.ReloadMagazineId;
            bool Related(ItemId id) => id.IsValid && (id == weapon.ItemId || id == attached || id == candidate
                || ItemAssemblyTraversal.TryFind(weapon.Item, id, out _));
            if (Related(operation.TargetItemId) || Related(operation.CandidateItemId)) weapon.CancelAction();
        }
        internal bool TryFire(bool pressed, bool held)
        {
            Weapon weapon = CurrentWeapon;
            if (!CanExecuteAuthority || pendingShots.Count >= 32 || player == null || !player.IsAlive || player.ReadState == null
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
            if (!CanExecuteAuthority) return;
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

        private void ReleaseWeapon()
        {
            bool hadWeapon = current != null;
            current?.CancelAction(); current = null;
            RefreshItemView(null); BindWeaponUiAction();
            if (hadWeapon) player?.CancelWeaponAction();
        }
        private void OnDisable()
        {
            if (role != null) { role.Changed -= OnRoleChanged; role.Committed -= OnLoadoutCommitted; }
            pendingShots.Clear(); UnbindWeaponUiEvents(); ReleaseWeapon();
        }
        private void OnDestroy() => shotExecutor.ForgetShooter(gameObject);
    }
}
