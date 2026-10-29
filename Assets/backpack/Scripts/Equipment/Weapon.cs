using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Equipment
{
    /// <summary>一次成功射击的记录；Sustained 表示本次按住中的后续发。</summary>
    public readonly struct WeaponShot
    {
        public WeaponShot(ulong sequence, double time, bool sustained)
        {
            Sequence = sequence;
            Time = time;
            Sustained = sustained;
        }

        public ulong Sequence { get; }
        public double Time { get; }
        public bool Sustained { get; }
    }

    /// <summary>绑定一个物品实例的无限弹药武器。调用方每帧注入时钟和输入，不依赖角色或 Unity 时钟。</summary>
    public sealed class Weapon
    {
        private bool hasFrame;
        private int frameId;
        private double now;
        private double nextShotTime;
        private double reloadStartTime;
        private bool hasShotInHold;
        private bool commandConsumed;

        public Weapon(RuntimeItem item)
        {
            Bind(item);
        }

        /// <summary>同一物品实例重建后更新引用，不重置冷却或射击历史。</summary>
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
            if (definition == null)
                throw new ArgumentException("武器物品缺少 WeaponDefinitionFragment。", nameof(item));
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
        public bool ReloadCompletedThisFrame { get; private set; }
        public bool ShotStartedThisFrame { get; private set; }
        public bool CanReload => hasFrame && !commandConsumed && !IsReloading;

        /// <summary>每帧只接收第一次调用；新帧的时钟必须有限、非负且不后退。</summary>
        public void BeginFrame(double now, int frameId, bool triggerHeld)
        {
            if (hasFrame && this.frameId == frameId) return;
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0d || (hasFrame && now < this.now))
                throw new ArgumentOutOfRangeException(nameof(now), "武器时钟必须有限、非负且不后退。");

            hasFrame = true;
            this.frameId = frameId;
            this.now = now;
            commandConsumed = false;
            ShotStartedThisFrame = false;
            ReloadCompletedThisFrame = false;
            if (!triggerHeld) hasShotInHold = false;
            if (!IsReloading) return;

            double elapsed = now - reloadStartTime;
            ReloadPhase = (float)Math.Min(1d, elapsed / Definition.ReloadDuration);
            if (elapsed < Definition.ReloadDuration) return;
            IsReloading = false;
            ReloadCompletedThisFrame = true;
        }

        /// <summary>纯查询；按下事件的连续射击重置只在成功射击时消费。</summary>
        public bool CanFire(bool pressed, bool held)
        {
            return hasFrame && !commandConsumed && !IsReloading && now >= nextShotTime
                && (Definition.FireMode == WeaponFireMode.SemiAutomatic ? pressed : pressed || held);
        }

        public bool TryFire(bool pressed, bool held, out WeaponShot shot)
        {
            shot = default;
            if (!CanFire(pressed, held)) return false;
            if (LastShot.Sequence == ulong.MaxValue)
                throw new OverflowException("武器射击序号已耗尽。");

            shot = new WeaponShot(LastShot.Sequence + 1, now, held && !pressed && hasShotInHold);
            LastShot = shot;
            hasShotInHold = held;
            nextShotTime = now + Definition.EffectiveShotInterval;
            ShotStartedThisFrame = true;
            commandConsumed = true;
            return true;
        }

        public bool TryBeginReload()
        {
            if (!CanReload) return false;
            IsReloading = true;
            ReloadPhase = 0f;
            reloadStartTime = now;
            hasShotInHold = false;
            commandConsumed = true;
            return true;
        }

        /// <summary>保留冷却和射击历史；取消后本帧不再消费命令。</summary>
        public void CancelAction()
        {
            IsReloading = false;
            ReloadPhase = 0f;
            hasShotInHold = false;
            ShotStartedThisFrame = false;
            ReloadCompletedThisFrame = false;
            commandConsumed = true;
        }
    }
}
