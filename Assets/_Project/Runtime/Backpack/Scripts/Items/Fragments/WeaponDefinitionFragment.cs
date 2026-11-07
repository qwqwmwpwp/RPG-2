using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.Backpack.Items
{
    public enum WeaponFireMode
    {
        Automatic = 0,
        SemiAutomatic = 1
    }

    /// <summary>武器的射击与换弹定义；运行状态由装备层持有。</summary>
    [Serializable]
    public sealed class WeaponDefinitionFragment : InventoryItemFragment
    {
        [SerializeField] private WeaponFireMode fireMode = WeaponFireMode.Automatic;
        [SerializeField, Min(0.01f)] private float shotInterval = 0.16f;
        [SerializeField, Min(0.01f)] private float shotDuration = 0.24f;
        [SerializeField, Min(0f)] private float fadeOutDuration = 0.12f;
        [SerializeField, Min(0.01f)] private float reloadDuration = 1.6f;
        [SerializeField] private BackpackTag ammoType;
        [SerializeField] private BackpackTag magazineType;
        [SerializeField, Min(0.01f)] private float maxDurability = 100f;
        [SerializeField, Min(0f)] private float durabilityPerShot = 1f;
        [SerializeField, Min(0f)] private float baseDamage = 20f;
        [SerializeField, Min(0.01f)] private float range = 100f;
        [SerializeField, Min(0f)] private float spreadAngle;
        [SerializeField, Min(0f)] private float recoilPitch = 1f;
        [SerializeField, Min(0f)] private float recoilYaw;
        [SerializeField, Range(0f, 1f)] private float reloadCommitFraction = 0.6f;
        [SerializeField, Range(0f, 1f)] private float chamberFraction = 0.9f;
        [SerializeField] private List<WeaponAttachmentSlot> allowedAttachmentSlots = new List<WeaponAttachmentSlot> { WeaponAttachmentSlot.Muzzle };

        public WeaponDefinitionFragment() : base(FragmentTags.WeaponDefinition) { }

        public WeaponDefinitionFragment(WeaponFireMode fireMode, float shotInterval, float shotDuration,
            float fadeOutDuration, float reloadDuration) : base(FragmentTags.WeaponDefinition)
        {
            this.fireMode = fireMode;
            this.shotInterval = shotInterval;
            this.shotDuration = shotDuration;
            this.fadeOutDuration = fadeOutDuration;
            this.reloadDuration = reloadDuration;
            Validate();
        }

        public WeaponFireMode FireMode => fireMode;
        public float ShotInterval => shotInterval;
        public float ShotDuration => shotDuration;
        public float FadeOutDuration => fadeOutDuration;
        public float ReloadDuration => reloadDuration;
        public float EffectiveShotInterval => shotInterval;
        public BackpackTag AmmoType => ammoType;
        public BackpackTag MagazineType => magazineType;
        public float MaxDurability => maxDurability;
        public float DurabilityPerShot => durabilityPerShot;
        public float BaseDamage => baseDamage;
        public float Range => range;
        public float SpreadAngle => spreadAngle;
        public float RecoilPitch => recoilPitch;
        public float RecoilYaw => recoilYaw;
        public float ReloadCommitFraction => reloadCommitFraction;
        public float ChamberFraction => chamberFraction;
        public IReadOnlyList<WeaponAttachmentSlot> AllowedAttachmentSlots => allowedAttachmentSlots.AsReadOnly();

        public bool AllowsSlot(WeaponAttachmentSlot slot) => allowedAttachmentSlots != null && allowedAttachmentSlots.Contains(slot);

        public void ConfigureCombat(BackpackTag ammoType, BackpackTag magazineType, float baseDamage = 20f,
            float range = 100f, float spreadAngle = 0f, float recoilPitch = 1f, float recoilYaw = 0f,
            float maxDurability = 100f, float durabilityPerShot = 1f,
            float reloadCommitFraction = 0.6f, float chamberFraction = 0.9f)
        {
            this.ammoType = ammoType; this.magazineType = magazineType;
            this.baseDamage = baseDamage; this.range = range; this.spreadAngle = spreadAngle;
            this.recoilPitch = recoilPitch; this.recoilYaw = recoilYaw;
            this.maxDurability = maxDurability; this.durabilityPerShot = durabilityPerShot;
            this.reloadCommitFraction = reloadCommitFraction; this.chamberFraction = chamberFraction;
            Validate();
        }

        public override ItemFragment DeepCopy()
        {
            var copy = new WeaponDefinitionFragment(fireMode, shotInterval, shotDuration, fadeOutDuration, reloadDuration);
            copy.ConfigureCombat(ammoType, magazineType, baseDamage, range, spreadAngle, recoilPitch, recoilYaw,
                maxDurability, durabilityPerShot, reloadCommitFraction, chamberFraction);
            copy.allowedAttachmentSlots = new List<WeaponAttachmentSlot>(allowedAttachmentSlots);
            return copy;
        }

        /// <summary>供装备程序集在绑定运行时物品时验证定义。</summary>
        public void Validate() => ValidateConfiguration(nameof(WeaponDefinitionFragment));

        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.WeaponDefinition, path);
            if (fireMode != WeaponFireMode.Automatic && fireMode != WeaponFireMode.SemiAutomatic)
                throw new InvalidOperationException($"{path}.FireMode 无效。");
            ValidateDuration(shotInterval, 0.01f, path + ".ShotInterval");
            ValidateDuration(shotDuration, 0.01f, path + ".ShotDuration");
            ValidateDuration(fadeOutDuration, 0f, path + ".FadeOutDuration");
            ValidateDuration(reloadDuration, 0.01f, path + ".ReloadDuration");
            WeaponDefinitionValidation.Tag(ammoType, true, path + ".AmmoType");
            WeaponDefinitionValidation.Tag(magazineType, true, path + ".MagazineType");
            ValidateDuration(maxDurability, 0.01f, path + ".MaxDurability");
            ValidateDuration(durabilityPerShot, 0f, path + ".DurabilityPerShot");
            ValidateDuration(baseDamage, 0f, path + ".BaseDamage");
            ValidateDuration(range, 0.01f, path + ".Range");
            ValidateDuration(spreadAngle, 0f, path + ".SpreadAngle");
            ValidateDuration(recoilPitch, 0f, path + ".RecoilPitch");
            ValidateDuration(recoilYaw, 0f, path + ".RecoilYaw");
            ValidateDuration(reloadCommitFraction, 0f, path + ".ReloadCommitFraction");
            ValidateDuration(chamberFraction, 0f, path + ".ChamberFraction");
            if (reloadCommitFraction > chamberFraction || chamberFraction > 1f || spreadAngle > 90f)
                throw new InvalidOperationException(path + " 的换弹时点顺序或散布角无效。");
            if (allowedAttachmentSlots == null || allowedAttachmentSlots.Count > 1)
                throw new InvalidOperationException(path + " 的配件槽无效。");
            foreach (WeaponAttachmentSlot slot in allowedAttachmentSlots)
                if (slot != WeaponAttachmentSlot.Muzzle) throw new InvalidOperationException(path + " 的配件槽无效。");
        }

        private static void ValidateDuration(float value, float minimum, string path)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < minimum)
                throw new InvalidOperationException($"{path} 必须是大于等于 {minimum} 的有限数。");
        }
    }
}
