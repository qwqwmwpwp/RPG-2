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
        private void Awake()
        {
            cancelButton.onClick.AddListener(() => owner?.Cancel());
            leaveButton.onClick.AddListener(() => owner?.Cancel());
            quitButton.onClick.AddListener(() => owner?.RequestQuit());
        }
        public void Bind(ClientSessionController value) { owner = value; Show(value.Status); }
        public void Show(ClientSessionStatus status)
        {
            bool show = status.IsBusy;
            overlayGroup.alpha = show ? 1 : 0;
            overlayGroup.interactable = overlayGroup.blocksRaycasts = show;
            phaseLabel.text = status.Message; failureLabel.text = status.Failure ?? string.Empty;
            cancelButton.interactable = status.State != ClientSessionState.Disconnecting && status.State != ClientSessionState.Bootstrapping;
            bool playing = status.State == ClientSessionState.Playing;
            bool bootstrapFailure = status.State == ClientSessionState.Bootstrapping && !string.IsNullOrEmpty(status.Failure);
            playingControls.SetActive(playing || bootstrapFailure);
            leaveButton.gameObject.SetActive(playing);
            if (show) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (show && !visible && EventSystem.current != null)
            { EventSystem.current.SetSelectedGameObject(null); if (cancelButton.interactable) cancelButton.Select(); }
            visible = show;
        }
        private void Update()
        {
            if (visible && cancelButton.interactable && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) owner?.Cancel();
            if (visible && Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
            { if (cancelButton.interactable) cancelButton.Select(); else if (playingControls.activeSelf) quitButton.Select(); }
        }
    }
}
