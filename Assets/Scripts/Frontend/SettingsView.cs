using TMPro;
using RPG4.UI;
using UnityEngine.UI;
using UnityEngine;
namespace RPG4.Frontend
{
    public sealed class SettingsView : FrontendWindow
    {
        public Button textScaleButton, hitFeedbackButton, lowHealthPulseButton, closeButton;
        public TMP_Text textScaleLabel, hitFeedbackLabel, lowHealthPulseLabel;
        private CursorLockMode previousLock;
        private bool previousVisible;
        protected override void Awake()
        {
            base.Awake(); textScaleButton.onClick.AddListener(PlayerUiPreferences.CycleTextScale);
            hitFeedbackButton.onClick.AddListener(PlayerUiPreferences.ToggleHitFeedback);
            lowHealthPulseButton.onClick.AddListener(PlayerUiPreferences.ToggleLowHealthPulse);
            closeButton.onClick.AddListener(Close);
            CloseRequested += Close;
        }
        protected override void OnOpened()
        {
            previousLock = Cursor.lockState; previousVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            base.OnOpened();
        }
        protected override void OnClosed()
        {
            bool playing = ClientSessionController.Instance != null && ClientSessionController.Instance.State == ClientSessionState.Playing;
            Cursor.lockState = playing ? previousLock : CursorLockMode.None;
            Cursor.visible = !playing || previousVisible;
            base.OnClosed();
        }
        protected override void OnEnable() { base.OnEnable(); PlayerUiPreferences.Changed += Render; Render(); }
        protected override void OnDisable() { PlayerUiPreferences.Changed -= Render; base.OnDisable(); }
        private void Render()
        {
            textScaleLabel.text = "界面文字  " + (PlayerUiPreferences.TextScale * 100).ToString("0") + "%";
            hitFeedbackLabel.text = "命中反馈  " + (PlayerUiPreferences.HitFeedback ? "开启" : "关闭");
            lowHealthPulseLabel.text = "低生命提示  " + (PlayerUiPreferences.LowHealthPulse ? "开启" : "关闭");
        }
    }
}
