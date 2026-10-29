using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace RPG4.Backpack.UI
{
    /// <summary>
    /// 库存网格滚动：空手时可在空格拖动画布，携带或等待操作期间停止拖动。
    /// 普通滚轮沿纵向滚动，Shift + 滚轮沿横向滚动。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BackpackScrollRect : ScrollRect
    {
        [SerializeField] private BackpackInteractionController interaction;

        private bool IsItemInteractionActive => interaction != null
            && (interaction.IsCarrying || interaction.IsWaitingForOperation || interaction.IsChoosingSplitQuantity);

        public override void OnBeginDrag(PointerEventData eventData)
        {
            if (StopDragForItemInteraction(eventData)) return;
            base.OnBeginDrag(eventData);
        }

        public override void OnDrag(PointerEventData eventData)
        {
            if (StopDragForItemInteraction(eventData)) return;
            base.OnDrag(eventData);
        }

        protected override void LateUpdate()
        {
            // 从先前空位拖动留下的惯性也不能在拿取物品后继续移动网格。
            if (IsItemInteractionActive) StopMovement();
            base.LateUpdate();
        }

        public override void OnScroll(PointerEventData eventData)
        {
            bool horizontalWheel = horizontal && Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            if (!horizontalWheel)
            {
                base.OnScroll(eventData);
                return;
            }

            Vector2 originalDelta = eventData.scrollDelta;
            try
            {
                eventData.scrollDelta = new Vector2(originalDelta.x + originalDelta.y, 0f);
                base.OnScroll(eventData);
            }
            finally
            {
                // PointerEventData 可被其他 UI 组件继续使用，不能留下修改后的滚轮轴。
                eventData.scrollDelta = originalDelta;
            }
        }

        private bool StopDragForItemInteraction(PointerEventData eventData)
        {
            if (!IsItemInteractionActive) return false;
            base.OnEndDrag(eventData);
            StopMovement();
            return true;
        }
    }
}
