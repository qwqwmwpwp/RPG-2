using System;
using UnityEngine;
using UnityEngine.UI;
using RPG4.Backpack.Inventory;

namespace RPG4.Backpack.UI
{
    [DisallowMultipleComponent]
    public sealed class AssemblySlotMarker : MonoBehaviour
    {
        public Button selectButton;
        private Action<AssemblySlotSnapshot> selected;
        private AssemblySlotSnapshot slot;
        private void Awake() { if (selectButton != null) selectButton.onClick.AddListener(Select); }
        private void OnDestroy() { if (selectButton != null) selectButton.onClick.RemoveListener(Select); }
        public void Bind(AssemblySlotSnapshot value, Action<AssemblySlotSnapshot> onSelected, bool focused)
        {
            slot = value; selected = onSelected;
            if (selectButton != null)
            {
                selectButton.interactable = true;
                Graphic graphic = selectButton.targetGraphic;
                if (graphic != null) graphic.color = focused ? new Color(0.32f, 0.86f, 0.98f, 0.98f) : new Color(0.9f, 0.72f, 0.32f, 0.92f);
            }
        }
        private void Select() { if (slot != null) selected?.Invoke(slot); }
    }
}
