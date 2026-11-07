using UnityEngine;
using UnityEngine.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>格子预制体的视图引用，供容器实例化后直接使用。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform), typeof(Image))]
    public sealed class BackpackSlotUI : MonoBehaviour
    {
        public Image background;
    }
}
