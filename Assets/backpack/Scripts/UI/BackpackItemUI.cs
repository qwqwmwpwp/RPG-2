using RPG4.Backpack.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>物品框视图：由主控实例化物品预制体而来，只负责把主控算好的像素尺寸、图标、显示数量与悬停态画到预制体上；不做尺寸推导、不改动库存，物品框外观完全由预制体决定。</summary>
    [RequireComponent(typeof(RectTransform), typeof(CanvasGroup))]
    public class BackpackItemUI : MonoBehaviour
    {
        #region 序列化字段

        [SerializeField] private Image backgroundImage0; // 物品框底图后面（悬停换色用，接线到预制体根节点；留空则不做悬停换色）。
        [SerializeField] private Image backgroundImage1; // 物品框底图前面（悬停换色用，接线到预制体根节点；留空则不做悬停换色）。

        [SerializeField] private Image iconImage; // 物品图标（无图标时整块隐藏）。
        [SerializeField] private TMP_Text stackText; // 显示数量文本（不大于 1 时不显示）。
        [SerializeField] private float iconPadding; // 图标可用区域相对物品框的内缩量（像素）。
        [SerializeField] private CanvasGroup group; // 预制体上的整体透明度组件（携带预览用）。

        #endregion

        #region 运行时状态

        private RectTransform rectTransform; // 自身 RectTransform（像素尺寸与内嵌换算的目标）。
        private Color backgroundColor; // 底图常态色：唤醒时取自预制体，取消悬停时原样还原。
        private Vector2 pixelSize; // 最近一次写入的物品像素尺寸。
        private Sprite icon; // 当前图标。
        private bool warnedIcon; // 图标未接线的警告是否已输出（每实例一条，避免逐帧刷屏）。
        private bool warnedStack; // 数量文本未接线的警告是否已输出。

        #endregion

        #region 生命周期

        /// <summary>缓存自身 RectTransform 与底图常态色。</summary>
        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
            if (group == null) group = GetComponent<CanvasGroup>();
            if (backgroundImage0 != null) backgroundColor = backgroundImage0.color;
            else BackpackLog.Warn(LogModule.UI, "物品框未接线物品背景，悬停将不换色。");
        }

        #endregion

        #region 对外契约

        /// <summary>写入物品像素尺寸（由主控按网格度量算好，含多格物品的间距累加），并据此重算图标内嵌。</summary>
        public void SetSize(Vector2 size)
        {
            pixelSize = size;
            if (rectTransform != null)
            {
                rectTransform.sizeDelta = size;
                backgroundImage1.rectTransform.sizeDelta = size - Vector2.one * 10;
            }
            ApplyIconLayout();
        }

        /// <summary>写入图标与显示数量：图标为空则留空（不回落文字），数量不大于 1 则清空文本。</summary>
        public void SetItem(Sprite itemIcon, int displayStack)
        {
            icon = itemIcon;
            ApplyIconLayout();

            if (stackText == null)
            {
                if (!warnedStack)
                {
                    warnedStack = true;
                    BackpackLog.Warn(LogModule.UI, "物品框未接线物品数量，堆叠数不会显示。");
                }

                return;
            }

            stackText.text = displayStack > 1 ? "x" + displayStack : string.Empty;
        }

        /// <summary>切换悬停底色：悬停用反馈高亮色，取消悬停还原预制体的常态色。</summary>
        public void SetHover(bool hovered)
        {
            if (backgroundImage0 == null) return;
            backgroundImage0.color = hovered ? BackpackFeedbackPalette.ItemFrameHovered : backgroundColor;
        }

        /// <summary>写入预制体上 CanvasGroup 的整体透明度（携带预览用）。</summary>
        public void SetAlpha(float alpha)
        {
            if (group != null) group.alpha = alpha;
        }

        #endregion

        #region 内部绘制

        /// <summary>按当前像素尺寸与图标宽高比做内嵌缩放：短边贴齐、长边留空；无图标时隐藏图标节点。</summary>
        private void ApplyIconLayout()
        {
            if (iconImage == null)
            {
                if (!warnedIcon)
                {
                    warnedIcon = true;
                    BackpackLog.Warn(LogModule.UI, "物品框未接线物品图标，物品只显示底图。");
                }

                return;
            }

            bool hasIcon = icon != null && icon.rect.size.x > 0f && icon.rect.size.y > 0f;
            iconImage.gameObject.SetActive(hasIcon);
            if (!hasIcon) return;

            iconImage.sprite = icon;

            // 可用区域 = 物品框尺寸内缩 iconPadding；按宽高比取短边贴齐，避免图标被拉伸。
            Vector2 area = new Vector2(
                Mathf.Max(1f, pixelSize.x - iconPadding * 2f),
                Mathf.Max(1f, pixelSize.y - iconPadding * 2f));
            float iconAspect = icon.rect.size.x / icon.rect.size.y;
            iconImage.rectTransform.sizeDelta = area.x / area.y < iconAspect
                ? new Vector2(area.x, area.x / iconAspect)-Vector2.one * 10
                : new Vector2(area.y * iconAspect, area.y)- Vector2.one * 10;
        }

        #endregion
    }
}
