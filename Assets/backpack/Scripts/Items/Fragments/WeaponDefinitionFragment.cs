using System;
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
        [SerializeField, Min(0f)] private float shotInterval = 0.16f;
        [SerializeField, Min(0.01f)] private float shotDuration = 0.24f;
        [SerializeField, Min(0f)] private float fadeOutDuration = 0.12f;
        [SerializeField, Min(0.01f)] private float reloadDuration = 1.6f;

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
        public float EffectiveShotInterval => Math.Max(shotInterval, shotDuration);

        public override ItemFragment DeepCopy() => new WeaponDefinitionFragment(
            fireMode, shotInterval, shotDuration, fadeOutDuration, reloadDuration);

        /// <summary>供装备程序集在绑定运行时物品时验证定义。</summary>
        public void Validate() => ValidateConfiguration(nameof(WeaponDefinitionFragment));

        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.WeaponDefinition, path);
            if (fireMode != WeaponFireMode.Automatic && fireMode != WeaponFireMode.SemiAutomatic)
                throw new InvalidOperationException($"{path}.FireMode 无效。");
            ValidateDuration(shotInterval, 0f, path + ".ShotInterval");
            ValidateDuration(shotDuration, 0.01f, path + ".ShotDuration");
            ValidateDuration(fadeOutDuration, 0f, path + ".FadeOutDuration");
            ValidateDuration(reloadDuration, 0.01f, path + ".ReloadDuration");
        }

        private static void ValidateDuration(float value, float minimum, string path)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < minimum)
                throw new InvalidOperationException($"{path} 必须是大于等于 {minimum} 的有限数。");
        }
    }
}
