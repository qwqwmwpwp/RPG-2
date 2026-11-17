using System;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    public enum LoadoutTagSource { ItemType, EquipmentType }
    public enum LoadoutTagMatch { Exact, SelfOrDescendant }

    [CreateAssetMenu(menuName = "RPG4/Loadout Template/Slot Rule")]
    public sealed class LoadoutSlotRule : ScriptableObject
    {
        [SerializeField] private LoadoutTagSource tagSource = LoadoutTagSource.EquipmentType;
        [SerializeField] private LoadoutTagMatch matchMode = LoadoutTagMatch.SelfOrDescendant;
        [SerializeField] private BackpackTag[] allowedTags = Array.Empty<BackpackTag>();
        [SerializeField] private BackpackTag[] deniedTags = Array.Empty<BackpackTag>();

        internal string ConfigurationSignature => JsonUtility.ToJson(new RuleSignature
        { source = (int)tagSource, match = (int)matchMode, allowed = allowedTags, denied = deniedTags });
        [Serializable] private sealed class RuleSignature
        { public int source, match; public BackpackTag[] allowed, denied; }

        internal LoadoutRuleConstraint CaptureRule() =>
            new LoadoutRuleConstraint(tagSource, matchMode, allowedTags, deniedTags);

        public bool Accepts(ItemDisplayData item, out string reason)
        {
            reason = null;
            return item == null || CaptureRule().Accepts(item.ItemType, item.EquipmentType, out reason);
        }

        internal static LoadoutSlotRule CreateRuntime(BackpackTag allowedTag)
        {
            var rule = CreateInstance<LoadoutSlotRule>();
            rule.hideFlags = HideFlags.DontSave;
            rule.allowedTags = new[] { allowedTag };
            return rule;
        }

    }
}

