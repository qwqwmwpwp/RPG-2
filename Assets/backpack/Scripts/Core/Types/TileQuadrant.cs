namespace RPG4.Backpack.Core
{
    /// <summary>悬停格内的象限：以格中心为界，决定多格物品的「上左索引」由悬停索引如何偏移推导；成员数值与顺序属契约，不可改动。</summary>
    public enum TileQuadrant
    {
        TopLeft = 0, // 格内左上象限。
        TopRight = 1, // 格内右上象限。
        BottomLeft = 2, // 格内左下象限。
        BottomRight = 3, // 格内右下象限。
        None = 4, // 未命中任何格（悬停参数未更新时的默认值）。
    }
}
