using System;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.Backpack.Items
{
    /// <summary>消耗动作的表现键与使用时长；效果数值仍由 ConsumableFragment 提供。</summary>
    [Serializable]
    public sealed class ConsumableUseDefinitionFragment : InventoryItemFragment
    {
        private static readonly BackpackTag DefinitionTag = new BackpackTag("FragmentTags.Consumable.UseDefinition");
        [SerializeField] private string actionKey = "Heal";
        [SerializeField, Min(0.01f)] private float useDuration = 2f;

        public ConsumableUseDefinitionFragment() : base(DefinitionTag) { }
        public ConsumableUseDefinitionFragment(string actionKey, float useDuration = 2f) : base(DefinitionTag)
        { this.actionKey = actionKey; this.useDuration = useDuration; ValidateConfiguration(nameof(ConsumableUseDefinitionFragment)); }
        public string ActionKey => actionKey;
        public float UseDuration => useDuration;
        public override ItemFragment DeepCopy() => new ConsumableUseDefinitionFragment(actionKey, useDuration);
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(DefinitionTag, path);
            if (string.IsNullOrWhiteSpace(actionKey) || useDuration <= 0f || float.IsNaN(useDuration) || float.IsInfinity(useDuration))
                throw new InvalidOperationException(path + " 必须配置动作键和正有限使用时长。");
        }
    }
}
