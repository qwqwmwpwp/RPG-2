using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RPG4.UI
{
    /// <summary>
    /// Shows one explicitly assigned focus graphic while this object is selected.
    /// Selection and navigation remain owned by the Unity EventSystem and Selectable.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UiFocusVisual : MonoBehaviour, ISelectHandler, IDeselectHandler
    {
        [SerializeField] private Graphic focusGraphic;

        public Graphic FocusGraphic => focusGraphic;
        public bool IsFocused { get; private set; }

        private void Awake() => SetFocused(false);

        private void OnEnable()
        {
            bool selected = EventSystem.current != null
                && EventSystem.current.currentSelectedGameObject == gameObject;
            SetFocused(selected);
        }

        private void OnDisable() => SetFocused(false);

        public void OnSelect(BaseEventData eventData) => SetFocused(true);

        public void OnDeselect(BaseEventData eventData) => SetFocused(false);

        /// <summary>Sets the display state of the assigned focus graphic only.</summary>
        public void SetFocused(bool focused)
        {
            IsFocused = focused;
            if (focusGraphic != null)
                focusGraphic.enabled = focused;
        }
    }
}
