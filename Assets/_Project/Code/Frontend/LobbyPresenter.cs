using UnityEngine;
namespace RPG4.Frontend
{
    public sealed class LobbyPresenter : MonoBehaviour
    {
        [SerializeField] private LobbyView main;
        [SerializeField] private SettingsView settings;
        [SerializeField] private LobbyCharacterPreview preview;
        private ClientSessionController session;
        private void Start()
        {
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            main.Open(); session = ClientSessionController.Instance;
            if (session == null) { main.statusLabel.text = "请从 ClientBootstrap 启动客户端。"; main.SetInputEnabled(false); return; }
            session.StatusChanged += RenderStatus;
            main.startButton.onClick.AddListener(StartGame);
            main.settingsButton.onClick.AddListener(OpenSettings); main.quitButton.onClick.AddListener(session.RequestQuit);
            settings.CloseRequested += ClosePanels; settings.closeButton.onClick.AddListener(ClosePanels);
            preview?.ShowEmptyLoadout(); RenderStatus(session.Status);
        }
        private void StartGame()
        {
            if (!session.CanStartGame) return;
            ClosePanels();
            if (!session.TryStartGame(out string reason)) main.statusLabel.text = reason;
        }
        private void OpenSettings() { if (session.State != ClientSessionState.Lobby) return; settings.Open(); main.SetInputEnabled(false); }
        private void ClosePanels() { settings.Close(); RestoreMain(); }
        private void RestoreMain()
        { main.SetInputEnabled(session != null && session.State == ClientSessionState.Lobby && !settings.IsOpen); main.SelectFirst(); }
        private void RenderStatus(ClientSessionStatus status)
        {
            main.statusLabel.text = status.Failure ?? status.Message;
            main.resultLabel.text = session.LastRaidResultText;
            bool inLobby = status.State == ClientSessionState.Lobby;
            if (!inLobby) settings.Close();
            if (inLobby && !session.CanStartGame) main.statusLabel.text = "正在准备入场或处理上一局，请稍候。";
            main.startButton.interactable = session.CanStartGame;
            main.SetInputEnabled(inLobby && !settings.IsOpen);
        }
        private void OnDestroy()
        {
            if (!ReferenceEquals(session, null)) session.StatusChanged -= RenderStatus;
            if (settings != null) settings.CloseRequested -= ClosePanels;
        }
    }
}
