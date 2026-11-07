using TMPro;
using UnityEngine;
using RPG4.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>固定背包窗口的引用；预制体保存层级和外观，此组件不处理库存或输入。</summary>
    [DisallowMultipleComponent]
    public sealed class BackpackWindowUIData : MonoBehaviour
    {
        public UIBase window;
        public BackpackContainerUI primaryView;
        public BackpackContainerUI secondaryView;
        public BackpackInteractionController interaction;
        public TMP_Text primarySummary;
        public TMP_Text secondarySummary;
        public TMP_Text actionHint;
        public TMP_Text operationStatus;
        public BackpackItemDetailsUI itemDetails;
    }
}
