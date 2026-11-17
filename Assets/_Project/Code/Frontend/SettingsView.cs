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
        [SerializeField] private Button abandonButton, confirmAbandonButton, cancelAbandonButton;
        [SerializeField] private GameObject abandonConfirmation;
        [SerializeField] private TMP_Text raidStatusLabel;
        private CursorLockMode previousLock;
        private bool previousVisible;
        protected override void Awake()
        {
            base.Awake(); textScaleButton.onClick.AddListener(PlayerUiPreferences.CycleTextScale);
            hitFeedbackButton.onClick.AddListener(PlayerUiPreferences.ToggleHitFeedback);
            lowHealthPulseButton.onClick.AddListener(PlayerUiPreferences.ToggleLowHealthPulse);
            closeButton.onClick.AddListener(Close);
            CloseRequested += Close;
            if (abandonButton != null) abandonButton.onClick.AddListener(ShowAbandonConfirmation);
            if (confirmAbandonButton != null) confirmAbandonButton.onClick.AddListener(AbandonRaid);
            if (cancelAbandonButton != null) cancelAbandonButton.onClick.AddListener(HideAbandonConfirmation);
        }
        protected override void OnOpened()
        {
            previousLock = Cursor.lockState; previousVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            HideAbandonConfirmation();
            if (abandonButton != null) abandonButton.gameObject.SetActive(ClientSessionController.Instance != null
                && ClientSessionController.Instance.State == ClientSessionState.Playing);
            if (raidStatusLabel != null) raidStatusLabel.text = string.Empty;
            base.OnOpened();
        }
        protected override void OnClosed()
        {
            HideAbandonConfirmation();
            bool playing = ClientSessionController.Instance != null && ClientSessionController.Instance.State == ClientSessionState.Playing;
            Cursor.lockState = playing ? previousLock : CursorLockMode.None;
            Cursor.visible = !playing || previousVisible;
            base.OnClosed();
        }
        protected override void OnEnable() { base.OnEnable(); PlayerUiPreferences.Changed += Render; Render(); }
        protected override void OnDisable() { PlayerUiPreferences.Changed -= Render; base.OnDisable(); }
        private void ShowAbandonConfirmation()
        {
            if (ClientSessionController.Instance == null || ClientSessionController.Instance.State != ClientSessionState.Playing) return;
            abandonConfirmation.SetActive(true); confirmAbandonButton.interactable = true; confirmAbandonButton.Select();
        }
        private void HideAbandonConfirmation() { if (abandonConfirmation != null) abandonConfirmation.SetActive(false); }
        private void AbandonRaid()
        {
            var session = ClientSessionController.Instance;
            if (session == null) return;
            confirmAbandonButton.interactable = false;
            if (session.TryReturnToLobby(out string reason)) Close();
            else { confirmAbandonButton.interactable = true; if (raidStatusLabel != null) raidStatusLabel.text = reason; }
        }
        protected override void Update()
        {
            if (abandonConfirmation != null && abandonConfirmation.activeSelf && CanProcessWindowInput)
            {
                if (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
                { HideAbandonConfirmation(); abandonButton.Select(); }
                return;
            }
            base.Update();
        }
        private void Render()
        {
            textScaleLabel.text = "界面文字  " + (PlayerUiPreferences.TextScale * 100).ToString("0") + "%";
            hitFeedbackLabel.text = "命中反馈  " + (PlayerUiPreferences.HitFeedback ? "开启" : "关闭");
            lowHealthPulseLabel.text = "低生命提示  " + (PlayerUiPreferences.LowHealthPulse ? "开启" : "关闭");
        }
    }
}
