using System;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.Backpack.Items
{
    public enum WeaponAttachmentSlot { Muzzle = 0 }

    internal static class WeaponDefinitionValidation
    {
        internal static void Number(float value, float minimum, string path)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < minimum)
                throw new InvalidOperationException(path + " 数值非法。");
        }

        internal static void Tag(BackpackTag tag, bool optional, string path)
        {
            if (optional && tag.IsEmpty) return;
            if (!tag.IsValid || tag.Name.Length > RuntimeItemSnapshot.MaxItemTypeLength)
                throw new InvalidOperationException(path + " 标签非法。");
        }
    }

    [Serializable]
    public sealed class AmmoDefinitionFragment : InventoryItemFragment
    {
        [SerializeField] private BackpackTag ammoType;
        [SerializeField] private float damageMultiplier = 1f;
        [SerializeField] private int pelletCount = 1;
        [SerializeField] private float spreadMultiplier = 1f;
        public BackpackTag AmmoType => ammoType;
        public float DamageMultiplier => damageMultiplier;
        public int PelletCount => pelletCount;
        public float SpreadMultiplier => spreadMultiplier;
        public AmmoDefinitionFragment() : base(FragmentTags.AmmoDefinition) { }
        public AmmoDefinitionFragment(BackpackTag ammoType, float damageMultiplier = 1f, int pelletCount = 1, float spreadMultiplier = 1f)
            : this() { this.ammoType = ammoType; this.damageMultiplier = damageMultiplier; this.pelletCount = pelletCount; this.spreadMultiplier = spreadMultiplier; ValidateConfiguration(nameof(AmmoDefinitionFragment)); }
        public override ItemFragment DeepCopy() => new AmmoDefinitionFragment(ammoType, damageMultiplier, pelletCount, spreadMultiplier);
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.AmmoDefinition, path);
            WeaponDefinitionValidation.Tag(ammoType, false, path);
            WeaponDefinitionValidation.Number(damageMultiplier, 0f, path);
            WeaponDefinitionValidation.Number(spreadMultiplier, 0f, path);
            if (pelletCount < 1 || pelletCount > 32) throw new InvalidOperationException(path + " 弹丸数须在1..32。");
        }
    }

    [Serializable]
    public sealed class MagazineDefinitionFragment : InventoryItemFragment
    {
        [SerializeField] private BackpackTag magazineType;
        [SerializeField] private BackpackTag ammoType;
        [SerializeField] private int capacity = 30;
        [SerializeField] private BackpackTag initialAmmoType;
        [SerializeField] private int initialRoundCount;
        public BackpackTag MagazineType => magazineType;
        public BackpackTag AmmoType => ammoType;
        public int Capacity => capacity;
        public BackpackTag InitialAmmoType => initialAmmoType;
        public int InitialRoundCount => initialRoundCount;
        public MagazineDefinitionFragment() : base(FragmentTags.MagazineDefinition) { }
        public MagazineDefinitionFragment(BackpackTag magazineType, BackpackTag ammoType, int capacity,
            BackpackTag initialAmmoType = default, int initialRoundCount = 0) : this()
        { this.magazineType = magazineType; this.ammoType = ammoType; this.capacity = capacity; this.initialAmmoType = initialAmmoType; this.initialRoundCount = initialRoundCount; ValidateConfiguration(nameof(MagazineDefinitionFragment)); }
        public override ItemFragment DeepCopy() => new MagazineDefinitionFragment(magazineType, ammoType, capacity, initialAmmoType, initialRoundCount);
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.MagazineDefinition, path);
            WeaponDefinitionValidation.Tag(magazineType, false, path);
            WeaponDefinitionValidation.Tag(ammoType, false, path);
            WeaponDefinitionValidation.Tag(initialAmmoType, initialRoundCount == 0, path);
            if (capacity < 1 || capacity > 256 || initialRoundCount < 0 || initialRoundCount > capacity)
                throw new InvalidOperationException(path + " 容量/初始弹数非法（容量1..256）。");
        }
    }

    [Serializable]
    public sealed class AttachmentDefinitionFragment : InventoryItemFragment
    {
        [SerializeField] private WeaponAttachmentSlot slot;
        [SerializeField] private BackpackTag weaponType;
        [SerializeField] private float damageMultiplier = 1f;
        [SerializeField] private float spreadMultiplier = 1f;
        [SerializeField] private float recoilMultiplier = 1f;
        [SerializeField] private float shotIntervalMultiplier = 1f;
        [SerializeField] private GameObject model;
        public WeaponAttachmentSlot Slot => slot;
        public BackpackTag WeaponType => weaponType;
        public float DamageMultiplier => damageMultiplier;
        public float SpreadMultiplier => spreadMultiplier;
        public float RecoilMultiplier => recoilMultiplier;
        public float ShotIntervalMultiplier => shotIntervalMultiplier;
        public GameObject Model => model;
        public AttachmentDefinitionFragment() : base(FragmentTags.AttachmentDefinition) { }
        public AttachmentDefinitionFragment(WeaponAttachmentSlot slot, BackpackTag weaponType = default,
            float damageMultiplier = 1f, float spreadMultiplier = 1f, float recoilMultiplier = 1f,
            float shotIntervalMultiplier = 1f, GameObject model = null) : this()
        { this.slot = slot; this.weaponType = weaponType; this.damageMultiplier = damageMultiplier; this.spreadMultiplier = spreadMultiplier; this.recoilMultiplier = recoilMultiplier; this.shotIntervalMultiplier = shotIntervalMultiplier; this.model = model; ValidateConfiguration(nameof(AttachmentDefinitionFragment)); }
        public override ItemFragment DeepCopy() => new AttachmentDefinitionFragment(slot, weaponType, damageMultiplier, spreadMultiplier, recoilMultiplier, shotIntervalMultiplier, model);
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.AttachmentDefinition, path);
            WeaponDefinitionValidation.Tag(weaponType, true, path);
            if (slot != WeaponAttachmentSlot.Muzzle) throw new InvalidOperationException(path + " 配件槽无效。");
            WeaponDefinitionValidation.Number(damageMultiplier, 0f, path);
            WeaponDefinitionValidation.Number(spreadMultiplier, 0f, path);
            WeaponDefinitionValidation.Number(recoilMultiplier, 0f, path);
            WeaponDefinitionValidation.Number(shotIntervalMultiplier, 0.01f, path);
        }
    }
}
