using System;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.Backpack.Items
{
    /// <summary>此物品安装后对根网格尺寸的四向扩展；普通扩展各向取最大，强制扩展另行累加。</summary>
    [Serializable]
    public sealed class AssemblySizeDefinitionFragment : InventoryItemFragment
    {
        [SerializeField, Min(0)] private int left;
        [SerializeField, Min(0)] private int right;
        [SerializeField, Min(0)] private int top;
        [SerializeField, Min(0)] private int bottom;
        [SerializeField] private bool forceAdditive;
        public int Left => left;
        public int Right => right;
        public int Top => top;
        public int Bottom => bottom;
        public bool ForceAdditive => forceAdditive;
        public AssemblySizeDefinitionFragment() : base(FragmentTags.AssemblySizeDefinition) { }
        public AssemblySizeDefinitionFragment(int left, int right, int top, int bottom, bool forceAdditive = false) : this()
        {
            this.left = left; this.right = right; this.top = top; this.bottom = bottom; this.forceAdditive = forceAdditive;
            ValidateConfiguration(nameof(AssemblySizeDefinitionFragment));
        }
        public override ItemFragment DeepCopy() => new AssemblySizeDefinitionFragment(left, right, top, bottom, forceAdditive);
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.AssemblySizeDefinition, path);
            if (left < 0 || right < 0 || top < 0 || bottom < 0)
                throw new InvalidOperationException(path + " 四向占格扩展必须非负。");
        }
    }
}
