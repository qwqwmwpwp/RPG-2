using UnityEngine;
using UnityEngine.UI;

namespace RPG4.Interaction
{
    /// <summary>只保存准心展示引用与颜色；输入和目标检测由玩家交互组件处理。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class CrosshairUI : MonoBehaviour
    {
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private Image crosshair;
        [SerializeField] private Color idleColor = Color.white;
        [SerializeField] private Color interactableColor = new Color(0.3f, 1f, 0.5f, 1f);

        public void SetState(bool visible, bool canInteract)
        {
            if (canvasGroup != null) canvasGroup.alpha = visible ? 1f : 0f;
            if (crosshair != null) crosshair.color = canInteract ? interactableColor : idleColor;
        }
    }
}
