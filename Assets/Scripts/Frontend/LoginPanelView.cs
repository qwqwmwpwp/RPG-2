using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.EventSystems;
namespace RPG4.Frontend
{
    public sealed class LoginPanelView : FrontendWindow
    {
        public TMP_InputField username, password;
        public TMP_Text serverLabel, validationLabel, passwordVisibilityLabel;
        public Button submitButton, closeButton, passwordVisibilityButton;
        public event Action Submitted;
        private bool passwordVisible;
        protected override void Awake()
        {
            base.Awake();
            submitButton.onClick.AddListener(Submit);
            passwordVisibilityButton.onClick.AddListener(TogglePassword);
            ConcealPassword();
        }
        private void Submit() { if (IsOpen && submitButton.IsInteractable()) Submitted?.Invoke(); }
        private void TogglePassword()
        {
            passwordVisible = !passwordVisible;
            password.inputType = passwordVisible ? TMP_InputField.InputType.Standard : TMP_InputField.InputType.Password;
            password.ForceLabelUpdate();
            if (passwordVisibilityLabel != null) passwordVisibilityLabel.text = passwordVisible ? "隐藏密码" : "显示密码";
        }
        private void ConcealPassword()
        {
            passwordVisible = true; TogglePassword();
        }
        public void ClearPassword() { password.SetTextWithoutNotify(string.Empty); ConcealPassword(); }
        protected override void OnClosed() { ClearPassword(); base.OnClosed(); }
        protected override void Update()
        {
            base.Update();
            if (!CanProcessWindowInput || Keyboard.current == null) return;
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            // Enter on reveal/back retains that button's own meaning; only the form/submit triggers login.
            if ((selected == username.gameObject || selected == password.gameObject || selected == submitButton.gameObject)
                && (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame)) Submit();
        }
    }
}
