using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Equipment
{
    /// <summary>成功扣弹时固定的本发参数；不从后续可变配件或弹匣反查。</summary>
    public readonly struct WeaponShot
    {
        public WeaponShot(ItemId weaponId, ulong sequence, double time, bool sustained,
            BackpackTag ammoType, BackpackTag ammoItemType, float damage, float range,
            float spreadDegrees, int pelletCount, float recoilPitch, float recoilYaw)
        {
            WeaponId = weaponId; Sequence = sequence; Time = time; Sustained = sustained;
            AmmoType = ammoType; AmmoItemType = ammoItemType; Damage = damage; Range = range;
            SpreadDegrees = spreadDegrees; PelletCount = pelletCount;
            RecoilPitch = recoilPitch; RecoilYaw = recoilYaw;
        }
        public ItemId WeaponId { get; }
        public ulong Sequence { get; }
        public double Time { get; }
        public bool Sustained { get; }
        public BackpackTag AmmoType { get; }
        public BackpackTag AmmoItemType { get; }
        public float Damage { get; }
        public float Range { get; }
        public float SpreadDegrees { get; }
        public int PelletCount { get; }
        public float RecoilPitch { get; }
        public float RecoilYaw { get; }
    }

    public enum WeaponReloadStage { None, Started, MagazineCommitted, Chambered, Completed }
    public delegate bool SelectWeaponMagazine(out ItemId magazineId);
    public delegate bool ExchangeWeaponMagazine(ItemId magazineId, out string error);

    /// <summary>纯 C# 武器时序；弹药、耐久与序号属于物品，库存交换通过事务委托提交。</summary>
    public sealed class Weapon
    {
        private bool hasFrame;
        private long frameId;
        private double now;
        private double nextShotTime;
        private double reloadStartTime;
        private bool hasShotInHold;
        private bool commandConsumed;
        private readonly ItemManifestCatalog catalog;
        private readonly SelectWeaponMagazine selectMagazine;
        private readonly ExchangeWeaponMagazine exchangeMagazine;
        private ItemId selectedMagazine;
        private bool reloadNeedsExchange;
        private ulong actionRevision;

        public Weapon(RuntimeItem item, ItemManifestCatalog catalog = null,
            SelectWeaponMagazine selectMagazine = null, ExchangeWeaponMagazine exchangeMagazine = null)
        {
            this.catalog = catalog;
            this.selectMagazine = selectMagazine;
            this.exchangeMagazine = exchangeMagazine;
            Bind(item);
        }
        public void Rebind(RuntimeItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (item.ItemId != ItemId)
                throw new ArgumentException("只能重新绑定相同 ItemId 的物品。", nameof(item));
            Bind(item);
        }
        private void Bind(RuntimeItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            EquipmentFragment equipment = item.GetFragment<EquipmentFragment>();
            if (equipment == null || !equipment.FragmentTag.MatchesTagExact(FragmentTags.EquipmentFragment)
                || equipment.EquipmentType.IsEmpty || !equipment.EquipmentType.IsValid)
                throw new ArgumentException("武器物品必须有合法的 EquipmentFragment。", nameof(item));
            WeaponDefinitionFragment definition = item.GetFragment<WeaponDefinitionFragment>();
            if (definition == null || item.WeaponState == null)
                throw new ArgumentException("武器物品缺少武器定义或持久状态。", nameof(item));
            definition.Validate();
            Item = item;
            Definition = definition;
        }
        public RuntimeItem Item { get; private set; }
        public ItemId ItemId => Item.ItemId;
        public WeaponDefinitionFragment Definition { get; private set; }
        public WeaponShot LastShot { get; private set; }
        public bool IsReloading { get; private set; }
        public float ReloadPhase { get; private set; }
        public WeaponReloadStage ReloadStage { get; private set; }
        public string LastReloadError { get; private set; }
        public bool ReloadCompletedThisFrame { get; private set; }
        public bool ShotStartedThisFrame { get; private set; }
        public double ReloadStartTime => reloadStartTime;
        /// <summary>尚在执行的换弹事务候选；用于关联库存操作与动作取消。</summary>
        public ItemId ReloadMagazineId => IsReloading && reloadNeedsExchange ? selectedMagazine : ItemId.None;
        public bool HasUsableRound => Item.WeaponState.CanConsumeShot(catalog)
            && TryGetParameters(out _, out _, out _, out _, out _);
        public bool CanReload => hasFrame && !commandConsumed && !IsReloading
            && (CanSelectMagazine(out _) || CanChamber());
        private bool CanSelectMagazine(out ItemId magazineId)
        {
            magazineId = ItemId.None;
            return selectMagazine != null && exchangeMagazine != null && selectMagazine(out magazineId);
        }
        private bool CanChamber() => Item.WeaponState.ChamberAmmo.IsEmpty
            && Item.WeaponState.Magazine?.MagazineState != null
            && Item.WeaponState.Magazine.MagazineState.Rounds.Count > 0;
        public void BeginFrame(double now, long frameId, bool triggerHeld)
        {
            // 网络重试与旧输入不能重新开放本 tick 的消费额度。
            if (hasFrame && frameId <= this.frameId) return;
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0d || (hasFrame && now < this.now))
                throw new ArgumentOutOfRangeException(nameof(now), "武器时钟必须有限、非负且不后退。");
            hasFrame = true;
            this.frameId = frameId;
            this.now = now;
            commandConsumed = false;
            ShotStartedThisFrame = ReloadCompletedThisFrame = false;
            if (!triggerHeld) hasShotInHold = false;
            if (!IsReloading) return;
            ulong revision = actionRevision;
            double elapsed = now - reloadStartTime;
            ReloadPhase = (float)Math.Min(1d, elapsed / Definition.ReloadDuration);
            if (ReloadStage == WeaponReloadStage.Started && ReloadPhase >= Definition.ReloadCommitFraction)
            {
                if (reloadNeedsExchange && !exchangeMagazine(selectedMagazine, out string error))
                {
                    LastReloadError = error;
                    CancelAction();
                    return;
                }
                // 事务 Changed 回调可能同步卸装或取消；不能把取消的旧动作继续推进到上膛。
                if (!IsReloading || actionRevision != revision) return;
                ReloadStage = WeaponReloadStage.MagazineCommitted;
                EventBus.Global.Publish(InvChannels.WeaponActionChanged, this);
                if (!IsReloading || actionRevision != revision) return;
            }
            if (ReloadStage == WeaponReloadStage.MagazineCommitted && ReloadPhase >= Definition.ChamberFraction)
            {
                if (Item.WeaponState.ChamberAmmo.IsEmpty && !Item.WeaponState.TryChamberRound(catalog, out string error))
                {
                    LastReloadError = error;
                    CancelAction();
                    return;
                }
                if (!IsReloading || actionRevision != revision) return;
                ReloadStage = WeaponReloadStage.Chambered;
                EventBus.Global.Publish(InvChannels.WeaponActionChanged, this);
                if (!IsReloading || actionRevision != revision) return;
            }
            if (elapsed < Definition.ReloadDuration) return;
            IsReloading = false;
            ReloadStage = WeaponReloadStage.Completed;
            ReloadCompletedThisFrame = true;
            EventBus.Global.Publish(InvChannels.WeaponActionChanged, this);
        }
        public bool CanFire(bool pressed, bool held) => hasFrame && !commandConsumed && !IsReloading
            && now >= nextShotTime && HasUsableRound
            && (Definition.FireMode == WeaponFireMode.SemiAutomatic ? pressed : pressed || held);
        private bool TryGetParameters(out AmmoDefinitionFragment ammo, out float damage,
            out float spread, out float recoil, out float interval)
        {
            bool valid = WeaponParameterCalculator.TryCalculate(Item, catalog, Item.WeaponState.ChamberAmmo,
                out WeaponCombatParameters parameters);
            ammo = parameters.Ammo; damage = parameters.Damage; spread = parameters.Spread;
            recoil = parameters.Recoil; interval = parameters.Interval;
            return valid;
        }
        public bool TryFire(bool pressed, bool held, out WeaponShot shot)
        {
            shot = default;
            if (!CanFire(pressed, held)
                || !TryGetParameters(out _, out float damage, out float spread, out float recoil, out float interval))
                return false;
            float range = Definition.Range;
            float recoilPitch = Definition.RecoilPitch * recoil, recoilYaw = Definition.RecoilYaw * recoil;
            // 物品 Changed 会同步通知外部；先占用命令，阻止监听器重入重复扣弹。
            commandConsumed = true;
            if (!Item.WeaponState.TryConsumeShot(catalog, out BackpackTag ammoItemType,
                out AmmoDefinitionFragment ammo, out ulong sequence, out _))
            {
                commandConsumed = false;
                return false;
            }
            shot = new WeaponShot(ItemId, sequence, now, held && !pressed && hasShotInHold,
                ammo.AmmoType, ammoItemType, damage, range,
                spread, ammo.PelletCount,
                recoilPitch, recoilYaw);
            LastShot = shot;
            hasShotInHold = held;
            nextShotTime = now + interval;
            ShotStartedThisFrame = commandConsumed = true;
            return true;
        }
        public bool TryBeginReload()
        {
            if (!CanReload) return false;
            reloadNeedsExchange = CanSelectMagazine(out selectedMagazine);
            actionRevision++;
            IsReloading = true;
            ReloadStage = WeaponReloadStage.Started;
            LastReloadError = null;
            ReloadPhase = 0f;
            reloadStartTime = now;
            hasShotInHold = false;
            commandConsumed = true;
            EventBus.Global.Publish(InvChannels.WeaponActionChanged, this);
            return true;
        }
        /// <summary>提交之前不碰库存；提交之后不回滚真实弹匣及膛内状态。</summary>
        public void CancelAction()
        {
            actionRevision++;
            bool wasReloading = IsReloading;
            IsReloading = false;
            ReloadPhase = 0f;
            ReloadStage = WeaponReloadStage.None;
            selectedMagazine = ItemId.None;
            hasShotInHold = false;
            ShotStartedThisFrame = ReloadCompletedThisFrame = false;
            commandConsumed = true;
            if (wasReloading) EventBus.Global.Publish(InvChannels.WeaponActionChanged, this);
        }
    }
}
