using System;
using RPG4.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using TMPro;
namespace RPG4.Frontend
{
    public abstract class FrontendWindow : UIBase
    {
        [SerializeField] private Selectable[] focusOrder = Array.Empty<Selectable>();
        private int openedFrame = -1;
        // A key that opened this window belongs to its caller, including derived form shortcuts.
        protected bool CanProcessWindowInput => IsOpen && Time.frameCount > openedFrame;
        public event Action CloseRequested;
        protected virtual void Update()
        {
            if (!CanProcessWindowInput || Keyboard.current == null) return;
            TMP_Dropdown expanded = null;
            foreach (Selectable selectable in focusOrder)
                if (selectable is TMP_Dropdown dropdown && dropdown.IsExpanded) { expanded = dropdown; break; }
            if (Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                if (expanded != null) { expanded.Hide(); expanded.Select(); return; }
                CloseRequested?.Invoke();
                return;
            }
            if (Keyboard.current.tabKey.wasPressedThisFrame)
            {
                if (expanded != null) { expanded.Hide(); expanded.Select(); }
                int current = -1;
                for (int i = 0; i < focusOrder.Length; i++)
                    if (focusOrder[i] != null && EventSystem.current != null && EventSystem.current.currentSelectedGameObject == focusOrder[i].gameObject) current = i;
                int step = Keyboard.current.shiftKey.isPressed ? -1 : 1;
                if (current < 0 && step < 0) current = 0;
                for (int i = 0; i < focusOrder.Length; i++)
                {
                    current = (current + step + focusOrder.Length) % focusOrder.Length;
                    if (focusOrder[current] != null && focusOrder[current].isActiveAndEnabled && focusOrder[current].IsInteractable())
                    { focusOrder[current].Select(); break; }
                }
            }
        }
        public void SelectFirst()
        {
            foreach (Selectable selectable in focusOrder)
                if (selectable != null && selectable.isActiveAndEnabled && selectable.IsInteractable()) { selectable.Select(); return; }
        }
        protected override void OnOpened()
        {
            openedFrame = Time.frameCount;
            base.OnOpened();
            SelectFirst();
        }
    }
}
