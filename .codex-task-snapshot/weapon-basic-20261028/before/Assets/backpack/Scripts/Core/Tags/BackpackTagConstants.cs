namespace RPG4.Backpack.Core
{
    /// <summary>Fragment 标签常量：只保留当前已有 Fragment 实现可以承载的标签。</summary>
    public static class FragmentTags
    {
        public static readonly BackpackTag GridFragment = new BackpackTag("FragmentTags.Default.GridFragment"); // 网格片段：物品在库存网格中占用的尺寸。

        public static readonly BackpackTag IconFragment = new BackpackTag("FragmentTags.Default.IconFragment"); // 图标片段：物品图标与显示尺寸。

        public static readonly BackpackTag StackableFragment = new BackpackTag("FragmentTags.Default.StackableFragment"); // 堆叠配置：只定义最大堆叠容量。

        public static readonly BackpackTag ConsumableFragment = new BackpackTag("FragmentTags.Consumable.ConsumableFragment"); // 消耗品容器。

        public static readonly BackpackTag EquipmentFragment = new BackpackTag("FragmentTags.Equip.EquipmentFragment"); // 装备容器。
    }
}
