using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>物品父类：所有具体物品的公共只读数据面，暴露类型标签、堆叠数量与占格尺寸；数据由具体物品自持，本类不存储任何状态。</summary>
    public abstract class BackpackItem
    {
        public abstract BackpackTag ItemType { get; } // 物品类型标签。

        public abstract int StackCount { get; } // 堆叠数量。

        public abstract GridCoord Size { get; } // 占格尺寸（X 为宽、Y 为高）。
    }
}
