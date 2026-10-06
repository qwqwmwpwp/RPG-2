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

    /// <summary>物品类型标签常量（示例最小集）：嵌套静态类的层级与标签层级一一对应；这些标签在代码中零引用、仅由数据资产填写，故只保留数条示例固定命名口径，其余标签直接加到 <c>Data/TagRegistry.asset</c>。</summary>
    public static class ItemTypeTags
    {
        /// <summary>装备类。</summary>
        public static class Equipment
        {
            /// <summary>武器。</summary>
            public static class Weapons
            {
                public static readonly BackpackTag Sword = new BackpackTag("GameItems.Equipment.Weapons.Sword"); // 剑。
            }

            /// <summary>披风。</summary>
            public static class Cloaks
            {
                public static readonly BackpackTag RedCloak = new BackpackTag("GameItems.Equipment.Cloaks.RedCloak"); // 红色披风。
            }
        }

        /// <summary>消耗品类。</summary>
        public static class Consumables
        {
            /// <summary>药水。</summary>
            public static class Potions
            {
                /// <summary>红药。</summary>
                public static class Red
                {
                    public static readonly BackpackTag Small = new BackpackTag("GameItems.Consumables.Potions.Red.Small"); // 小型红药。
                }
            }
        }

        /// <summary>可制作材料。</summary>
        public static class Craftables
        {
            public static readonly BackpackTag FireFernFruit = new BackpackTag("GameItems.Craftables.FireFernFruit"); // 火蕨果实。
        }
    }
}
