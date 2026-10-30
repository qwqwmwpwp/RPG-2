using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    /// <summary>本地权威物品和网络只读投影使用同一展示规则；操作准入由宿主提供。</summary>
    public static class WeaponUiSnapshots
    {
        public static WeaponUiItem Describe(RuntimeItem item, ItemManifestCatalog catalog = null)
        {
            if (item == null) return null;
            string name = item.ItemType.Name;
            if (catalog != null && catalog.TryGet(item.ItemType, out ItemManifest manifest) && manifest != null) name = manifest.name;
            return new WeaponUiItem(item.ItemId, name, item.GetFragment<ImageFragment>()?.Icon, item.StackCount);
        }

        public static WeaponInspectionSnapshot Inspection(RuntimeItem item, IList<RuntimeItem> roots,
            ItemManifestCatalog catalog, ItemId equippedId, WeaponSnapshotRequest request,
            Func<WeaponOperation, string> rejectionReason, string blockReason = null)
        {
            var candidates = new List<WeaponUiCandidate>();
            WeaponItemState weapon = item?.WeaponState;
            MagazineItemState magazine = weapon?.Magazine?.MagazineState ?? item?.MagazineState;
            bool removeMagazine = false, removeMuzzle = false;
            RuntimeItem muzzle = null;
            string removalReason = null;
            if (weapon != null)
            {
                weapon.Attachments.TryGetValue(WeaponAttachmentSlot.Muzzle, out muzzle);
                if (weapon.Magazine != null)
                {
                    removalReason = rejectionReason(new WeaponOperation(0, WeaponOperationKind.ExchangeMagazine, item.ItemId));
                    removeMagazine = string.IsNullOrEmpty(removalReason);
                }
                if (muzzle != null)
                {
                    string reason = rejectionReason(new WeaponOperation(0, WeaponOperationKind.ExchangeAttachment, item.ItemId));
                    removeMuzzle = string.IsNullOrEmpty(reason);
                    if (string.IsNullOrEmpty(removalReason)) removalReason = reason;
                }
            }
            if (item != null && (weapon != null || item.MagazineState != null))
                foreach (RuntimeItem candidate in roots)
                {
                    WeaponOperationKind kind;
                    int maxCount = 0;
                    string preview = null;
                    if (weapon != null && weapon.IsCompatibleMagazine(candidate, catalog))
                    {
                        kind = WeaponOperationKind.ExchangeMagazine;
                        preview = $"弹匣 {candidate.MagazineState.RoundCount} / {candidate.MagazineState.Capacity} 发";
                    }
                    else if (weapon != null && weapon.IsCompatibleAttachment(WeaponAttachmentSlot.Muzzle, candidate))
                    {
                        kind = WeaponOperationKind.ExchangeAttachment;
                        preview = CombatSummary(item, catalog, candidate, true);
                    }
                    else if (weapon == null && item.MagazineState.CanAcceptAmmo(candidate.ItemType, catalog)
                        && candidate.WeaponState == null && candidate.MagazineState == null
                        && candidate.GetFragment<AttachmentDefinitionFragment>() == null)
                    {
                        kind = WeaponOperationKind.LoadMagazine;
                        maxCount = Math.Min(candidate.StackCount, magazine.Capacity - magazine.RoundCount);
                    }
                    else continue;
                    string reason = rejectionReason(new WeaponOperation(0, kind, item.ItemId, candidate.ItemId, maxCount));
                    candidates.Add(new WeaponUiCandidate(Describe(candidate, catalog), kind, string.IsNullOrEmpty(reason), reason, maxCount, preview));
                }
            return new WeaponInspectionSnapshot(Describe(item, catalog), weapon != null, item?.MagazineState != null,
                item != null && item.ItemId == equippedId, weapon == null || weapon.ChamberAmmo.IsEmpty ? 0 : 1,
                magazine?.RoundCount ?? 0, magazine?.Capacity ?? 0, weapon?.Durability ?? 0f,
                weapon?.Definition.MaxDurability ?? 0f, Describe(weapon?.Magazine, catalog), Describe(muzzle, catalog),
                removeMagazine, removeMuzzle, blockReason ?? removalReason, candidates,
                request.SessionId, request.RequestId, request.InspectedItemId, CombatSummary(item, catalog),
                weapon != null && weapon.Definition.AllowsSlot(WeaponAttachmentSlot.Muzzle));
        }

        public static string CombatSummary(RuntimeItem item, ItemManifestCatalog catalog,
            RuntimeItem muzzleOverride = null, bool overrideMuzzle = false)
        {
            WeaponItemState state = item?.WeaponState;
            if (state == null) return null;
            BackpackTag ammo = state.ChamberAmmo;
            if (ammo.IsEmpty && state.Magazine?.MagazineState.RoundCount > 0) ammo = state.Magazine.MagazineState.Rounds[0];
            if (WeaponParameterCalculator.TryCalculate(item, catalog, ammo, out WeaponCombatParameters value, muzzleOverride, overrideMuzzle))
                return $"伤害 {value.Damage:0.#} · 射速 {60f / value.Interval:0} 发/分 · 散布 {value.Spread:0.##}° · 后坐力 {value.Recoil:0.##}×";
            WeaponDefinitionFragment definition = state.Definition;
            return $"基础伤害 {definition.BaseDamage:0.#} · 基础射速 {60f / definition.ShotInterval:0} 发/分 · 基础散布 {definition.SpreadAngle:0.##}°（无可预览弹药）"
                + AttachmentSummary(overrideMuzzle ? muzzleOverride : GetMuzzle(state));
        }
        private static RuntimeItem GetMuzzle(WeaponItemState state)
        { state.Attachments.TryGetValue(WeaponAttachmentSlot.Muzzle, out RuntimeItem item); return item; }
        private static string AttachmentSummary(RuntimeItem item)
        {
            AttachmentDefinitionFragment value = item?.GetFragment<AttachmentDefinitionFragment>();
            return value == null ? "" : $" · 枪口倍率：伤害 {value.DamageMultiplier:0.##}× / 散布 {value.SpreadMultiplier:0.##}× / 后坐力 {value.RecoilMultiplier:0.##}× / 射击间隔 {value.ShotIntervalMultiplier:0.##}×";
        }
    }
}
