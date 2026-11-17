using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
namespace RPG4.Frontend
{
    /// <summary>Application-owned canvas; it never registers with a scene UIManager.</summary>
    public sealed class SessionStatusView : MonoBehaviour
    {
        [SerializeField] private CanvasGroup overlayGroup;
        [SerializeField] private TMP_Text phaseLabel, failureLabel;
        [SerializeField] private Button cancelButton, leaveButton, quitButton;
        [SerializeField] private GameObject playingControls;
        private ClientSessionController owner;
        private bool visible;
        private double abandonConfirmationUntil;
        private string leaveLabel;
        private void Awake()
        {
            cancelButton.onClick.AddListener(() => owner?.Cancel());
            leaveButton.onClick.AddListener(ConfirmAbandon);
            quitButton.onClick.AddListener(() => owner?.RequestQuit());
            leaveLabel = leaveButton.GetComponentInChildren<TMP_Text>()?.text;
        }
        public void Bind(ClientSessionController value) { owner = value; Show(value.Status); }
        public void Show(ClientSessionStatus status)
        {
            bool show = status.IsBusy;
            overlayGroup.alpha = show ? 1 : 0;
            overlayGroup.interactable = overlayGroup.blocksRaycasts = show;
            phaseLabel.text = status.Message; failureLabel.text = status.Failure ?? string.Empty;
            cancelButton.interactable = status.State != ClientSessionState.Disconnecting && status.State != ClientSessionState.Bootstrapping
                && status.State != ClientSessionState.Settling && status.State != ClientSessionState.ReturningLobby;
            bool playing = status.State == ClientSessionState.Playing;
            bool bootstrapFailure = status.State == ClientSessionState.Bootstrapping && !string.IsNullOrEmpty(status.Failure);
            playingControls.SetActive(playing || bootstrapFailure);
            leaveButton.gameObject.SetActive(playing);
            if (show) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (show && !visible && EventSystem.current != null)
            { EventSystem.current.SetSelectedGameObject(null); if (cancelButton.interactable) cancelButton.Select(); }
            visible = show;
        }
        private void ConfirmAbandon()
        {
            if (owner == null) return;
            TMP_Text label = leaveButton.GetComponentInChildren<TMP_Text>();
            if (Time.realtimeSinceStartupAsDouble > abandonConfirmationUntil)
            {
                abandonConfirmationUntil = Time.realtimeSinceStartupAsDouble + 5d;
                if (label != null) label.text = "携行物品将掉落，再点确认放弃";
                return;
            }
            abandonConfirmationUntil = 0d;
            if (label != null) label.text = leaveLabel;
            owner.TryReturnToLobby(out _);
        }
        private void Update()
        {
            if (abandonConfirmationUntil > 0 && Time.realtimeSinceStartupAsDouble > abandonConfirmationUntil)
            { abandonConfirmationUntil = 0d; TMP_Text label = leaveButton.GetComponentInChildren<TMP_Text>(); if (label != null) label.text = leaveLabel; }
            if (visible && cancelButton.interactable && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) owner?.Cancel();
            if (visible && Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
            { if (cancelButton.interactable) cancelButton.Select(); else if (playingControls.activeSelf) quitButton.Select(); }
        }
    }
}
