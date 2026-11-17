using System;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    public readonly struct WeaponCombatParameters
    {
        public WeaponCombatParameters(AmmoDefinitionFragment ammo, float damage, float spread, float recoil, float interval)
        { Ammo = ammo; Damage = damage; Spread = spread; Recoil = recoil; Interval = interval; }
        public AmmoDefinitionFragment Ammo { get; }
        public float Damage { get; }
        public float Spread { get; }
        public float Recoil { get; }
        public float Interval { get; }
    }

    /// <summary>战斗与装配预览共用的乘算规则；预览可显式替换枪口，不修改物品。</summary>
    public static class WeaponParameterCalculator
    {
        public static bool TryCalculate(RuntimeItem item, ItemManifestCatalog catalog, BackpackTag ammoItemType,
            out WeaponCombatParameters parameters, RuntimeItem muzzleOverride = null, bool overrideMuzzle = false)
        {
            return TryCalculateAssembly(item, catalog, ammoItemType, out parameters,
                item?.ItemId ?? ItemId.None, overrideMuzzle ? ItemAssemblySlots.Muzzle : null, muzzleOverride);
        }

        public static bool TryCalculateAssembly(RuntimeItem root, ItemManifestCatalog catalog, BackpackTag ammoItemType,
            out WeaponCombatParameters parameters, ItemId parentItemId = default, string slotId = null, RuntimeItem replacement = null)
        {
            parameters = default;
            WeaponDefinitionFragment definition = root?.WeaponState?.Definition;
            if (definition == null || catalog == null || !catalog.TryGet(ammoItemType, out ItemManifest manifest)) return false;
            AmmoDefinitionFragment ammo = manifest.GetFragment<AmmoDefinitionFragment>();
            if (ammo == null) return false;
            float damage = definition.BaseDamage, spread = definition.SpreadAngle, recoil = 1f, interval = definition.ShotInterval;
            try
            {
                foreach (RuntimeItem item in ItemAssemblyTraversal.EnumerateReplacement(root, parentItemId, slotId, replacement))
                    Apply(item, ref damage, ref spread, ref recoil, ref interval);
            }
            catch (InvalidOperationException) { return false; }
            damage *= ammo.DamageMultiplier; spread *= ammo.SpreadMultiplier;
            if (!Finite(damage) || damage < 0f || !Finite(spread) || spread < 0f || spread > 90f
                || !Finite(recoil) || recoil < 0f || !Finite(interval) || interval <= 0f
                || !Finite(definition.RecoilPitch * recoil) || !Finite(definition.RecoilYaw * recoil)) return false;
            parameters = new WeaponCombatParameters(ammo, damage, spread, recoil, interval);
            return true;
        }
        private static void Apply(RuntimeItem item, ref float damage, ref float spread, ref float recoil, ref float interval)
        {
            AttachmentDefinitionFragment modifier = item?.GetFragment<AttachmentDefinitionFragment>();
            if (modifier == null) return;
            damage *= modifier.DamageMultiplier; spread *= modifier.SpreadMultiplier;
            recoil *= modifier.RecoilMultiplier; interval *= modifier.ShotIntervalMultiplier;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
