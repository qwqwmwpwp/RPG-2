using UnityEngine;
namespace RPG4.Frontend
{
    public sealed class LobbyPresenter : MonoBehaviour
    {
        [SerializeField] private LobbyView main;
        [SerializeField] private LobbyLoadoutWindow loadout;
        [SerializeField] private SettingsView settings;
        [SerializeField] private LobbyCharacterPreview preview;
        private ClientSessionController session;
        private void Start()
        {
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            main.Open(); session = ClientSessionController.Instance;
            if (session == null) { main.statusLabel.text = "请从 ClientBootstrap 启动客户端。"; main.SetInputEnabled(false); return; }
            session.StatusChanged += RenderStatus; session.LobbyInventoryChanged += RenderInventory;
            main.startButton.onClick.AddListener(StartGame); main.stashButton.onClick.AddListener(OpenLoadout);
            main.settingsButton.onClick.AddListener(OpenSettings); main.quitButton.onClick.AddListener(session.RequestQuit);
            loadout.CloseRequested += ClosePanels; settings.CloseRequested += ClosePanels;
            loadout.Closed += RestoreMain; settings.closeButton.onClick.AddListener(ClosePanels);
            RenderStatus(session.Status); RenderInventory();
        }
        private void StartGame()
        {
            if (!session.CanStartGame) return;
            ClosePanels();
            if (!session.TryStartGame(out string reason)) main.statusLabel.text = reason;
        }
        private void OpenLoadout()
        {
            if (!session.IsLobbyInventoryReady || session.State != ClientSessionState.Lobby) return;
            settings.Close(); loadout.Bind(session.LobbyBackpack, session.LobbyEquipment, session.LobbyStash);
            if (loadout.Open()) main.SetInputEnabled(false);
        }
        private void OpenSettings() { if (session.State != ClientSessionState.Lobby) return; loadout.Close(); settings.Open(); main.SetInputEnabled(false); }
        private void ClosePanels() { loadout.Close(); settings.Close(); RestoreMain(); }
        private void RestoreMain()
        { main.SetInputEnabled(session != null && session.State == ClientSessionState.Lobby && !loadout.IsOpen && !settings.IsOpen); main.SelectFirst(); }
        private void RenderStatus(ClientSessionStatus status)
        {
            main.statusLabel.text = status.Failure ?? status.Message;
            main.resultLabel.text = session.LastRaidResultText;
            bool inLobby = status.State == ClientSessionState.Lobby;
            if (!inLobby) { loadout.Close(); settings.Close(); }
            if (inLobby && !session.CanStartGame) main.statusLabel.text = "正在同步仓库或处理上一局，请稍候。";
            main.startButton.interactable = session.CanStartGame;
            main.stashButton.interactable = inLobby && session.IsLobbyInventoryReady;
            main.SetInputEnabled(inLobby && !loadout.IsOpen && !settings.IsOpen);
        }
        private void RenderInventory()
        {
            if (!session.IsLobbyInventoryReady) loadout.Close();
            else loadout.Bind(session.LobbyBackpack, session.LobbyEquipment, session.LobbyStash);
            preview.BindEquipment(session.IsLobbyInventoryReady ? session.LobbyEquipment : null);
            RenderStatus(session.Status);
        }
        private void OnDestroy()
        {
            if (!ReferenceEquals(session, null)) { session.StatusChanged -= RenderStatus; session.LobbyInventoryChanged -= RenderInventory; }
            if (loadout != null) { loadout.CloseRequested -= ClosePanels; loadout.Closed -= RestoreMain; }
            if (settings != null) settings.CloseRequested -= ClosePanels;
        }
    }
}
