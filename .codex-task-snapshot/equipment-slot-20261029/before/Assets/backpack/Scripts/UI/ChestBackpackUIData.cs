using TMPro;
using UnityEngine;

namespace RPG4.Backpack.UI
{
    /// <summary>箱子窗口的预制体引用；固定 UI 节点在编辑器中创建并接线。</summary>
    [DisallowMultipleComponent]
    public sealed class ChestBackpackUIData : MonoBehaviour
    {
        public BackpackContainerUI playerView;
        public BackpackContainerUI chestView;
        public BackpackInteractionController interaction;
        public TMP_Text playerTitle;
        public TMP_Text chestTitle;
    }
}
