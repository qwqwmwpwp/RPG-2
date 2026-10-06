using UnityEngine;

namespace RPG4.Backpack.UI
{
    /// <summary>悬停反馈态：由交互控制层每帧的空间查询结果派生，视图只负责翻译成颜色；成员数值是调试比对口径，不可改动。</summary>
    public enum BackpackHoverState
    {
        None = 0, // 无反馈：鼠标不在网格内，或本条不参与悬停着色。

        Available = 1, // 可放置：落点区域全空。

        Blocked = 2, // 不可放置：越界，或落点被多个不同物品挡路。

        Swap = 3, // 可交换 / 可堆叠：落点被单个物品占用，且该物品是合法对手。
    }

    /// <summary>界面配色单一入口：所有交互反馈颜色集中于此，避免散落在各视图各写一份。</summary>
    public static class BackpackFeedbackPalette
    {
        public static readonly Color SlotEmpty = new Color(0.059f, 0.133f, 0.176f, 0.90f); // 空闲格底色。

        public static readonly Color SlotFilled = new Color(0.082f, 0.184f, 0.227f, 0.96f); // 被占用格底色。

        public static readonly Color SlotAvailable = new Color(0.357f, 0.890f, 0.549f, 0.55f); // 可放置高亮（#5BE38C）。

        public static readonly Color SlotBlocked = new Color(0.922f, 0.341f, 0.341f, 0.42f); // 不可放置高亮（#EB5757）。

        public static readonly Color SlotSwap = new Color(0.949f, 0.788f, 0.298f, 0.50f); // 可交换 / 可堆叠高亮（#F2C94C）。

        public static readonly Color ItemFrame = new Color(0.173f, 0.549f, 0.620f, 0.97f); // 物品框常态（#2C8C9E）。

        public static readonly Color ItemFrameHovered = new Color(0.427f, 0.906f, 0.843f, 1f); // 物品框悬停态（#6DE7D7）。

        public static readonly Color ItemInner = new Color(0.024f, 0.086f, 0.129f, 0.95f); // 物品内容底色。

        public static readonly Color TextPrimary = new Color(0.910f, 0.961f, 0.961f); // 主文字色（#E8F5F5）。

        public static readonly Color TextMuted = new Color(0.435f, 0.588f, 0.620f); // 次要文字色（#6F969E），描述面板未展开的数值用它。

        public static readonly Color Warning = new Color(1f, 0.690f, 0.420f, 1f); // 溢出与配置提示色（RPG_4 追加）。
    }
}
