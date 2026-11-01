using System.Collections.Generic;
using TMPro;
using UnityEngine;
namespace RPG4.Frontend
{
    public sealed class MainMenuPresenter : MonoBehaviour
    {
        [SerializeField] private MainMenuView main;
        [SerializeField] private LoginPanelView login;
        [SerializeField] private ServerSelectionView servers;
        [SerializeField] private SettingsView settings;
        private ClientSessionController session;
        private int selectedIndex;
        private string selectedAddress;
        private int selectedPort;
        private bool subscribed;
        private void Start()
        {
            session = ClientSessionController.Instance;
            main.Open();
            if (session == null) { main.statusLabel.text = "请从 ClientBootstrap 启动客户端。"; main.startButton.interactable = main.serversButton.interactable = false; return; }
            session.StatusChanged += RenderStatus; session.CredentialsDispatched += ClearCredentials; subscribed = true;
            main.startButton.onClick.AddListener(OpenLogin);
            main.serversButton.onClick.AddListener(OpenServers);
            main.settingsButton.onClick.AddListener(OpenSettings);
            main.quitButton.onClick.AddListener(session.RequestQuit);
            login.Submitted += Submit;
            login.closeButton.onClick.AddListener(ClosePanels); login.CloseRequested += ClosePanels;
            servers.closeButton.onClick.AddListener(ClosePanels); servers.CloseRequested += ClosePanels;
            settings.closeButton.onClick.AddListener(ClosePanels); settings.CloseRequested += ClosePanels;
            servers.servers.onValueChanged.AddListener(SelectServer);
            servers.continueButton.onClick.AddListener(ConfirmServer);
            login.username.onValueChanged.AddListener(_ => ValidateLogin());
            login.password.onValueChanged.AddListener(_ => ValidateLogin());
            servers.address.onValueChanged.AddListener(_ => ValidateEndpoint());
            servers.port.onValueChanged.AddListener(_ => ValidateEndpoint());
            var choices = new List<TMP_Dropdown.OptionData>();
            if (session.ServerCatalog != null)
                for (int i = 0; i < session.ServerCatalog.Count; i++)
                {
                    var server = session.ServerCatalog.Get(i);
                    choices.Add(new TMP_Dropdown.OptionData(server == null ? "服务器配置无效" : server.DisplayName + " · " + server.Address + ":" + server.Port));
                }
            if (choices.Count == 0) choices.Add(new TMP_Dropdown.OptionData("尚未配置受信任服务器"));
            servers.servers.ClearOptions(); servers.servers.AddOptions(choices);
            string savedId = PlayerPrefs.GetString("RPG4.Frontend.Server", string.Empty);
            selectedIndex = 0;
            if (session.ServerCatalog != null) for (int i = 0; i < session.ServerCatalog.Count; i++)
                if (session.ServerCatalog.Get(i) != null && session.ServerCatalog.Get(i).ServerId == savedId) selectedIndex = i;
            servers.servers.SetValueWithoutNotify(selectedIndex); SelectServer(selectedIndex);
            login.username.SetTextWithoutNotify(PlayerPrefs.GetString("RPG4.Frontend.Username", string.Empty));
            RenderStatus(session.Status);
        }
        private void SelectServer(int index)
        {
            selectedIndex = index;
            var server = session.ServerCatalog != null ? session.ServerCatalog.Get(index) : null;
            selectedAddress = server != null ? PlayerPrefs.GetString("RPG4.Frontend.Address." + server.ServerId, server.Address) : string.Empty;
            selectedPort = server != null ? PlayerPrefs.GetInt("RPG4.Frontend.Port." + server.ServerId, server.Port) : 7777;
            servers.address.SetTextWithoutNotify(selectedAddress); servers.port.SetTextWithoutNotify(selectedPort.ToString());
            main.serverCard.text = server != null ? server.DisplayName + "\n" + selectedAddress + ":" + selectedPort : "尚未配置服务器";
            ValidateEndpoint();
        }
        private void OpenLogin()
        {
            if (!session.CanSubmit) return;
            servers.Close(); settings.Close(); main.SetInputEnabled(false);
            var server = session.ServerCatalog != null ? session.ServerCatalog.Get(selectedIndex) : null;
            login.serverLabel.text = server != null ? server.DisplayName + "  ·  " + selectedAddress + ":" + selectedPort : "尚未配置服务器";
            login.Open(); ValidateLogin();
        }
        private void OpenServers() { if (!session.CanSubmit) return; login.Close(); settings.Close(); main.SetInputEnabled(false); servers.Open(); ValidateEndpoint(); }
        private void OpenSettings() { if (!session.CanSubmit) return; login.Close(); servers.Close(); main.SetInputEnabled(false); settings.Open(); }
        private void ClosePanels()
        {
            login.Close(); servers.Close(); settings.Close(); main.SetInputEnabled(session == null || session.CanSubmit); main.SelectFirst();
        }
        private void ConfirmServer()
        {
            if (!session.CanSubmit || session.ServerCatalog == null || !ClientServerCatalog.IsValidAddress(servers.address.text)
                || !int.TryParse(servers.port.text, out int port) || port < 1 || port > 65535) return;
            selectedAddress = servers.address.text; selectedPort = port;
            var server = session.ServerCatalog.Get(selectedIndex);
            if (server == null) return;
            PlayerPrefs.SetString("RPG4.Frontend.Server", server.ServerId);
            PlayerPrefs.SetString("RPG4.Frontend.Address." + server.ServerId, selectedAddress);
            PlayerPrefs.SetInt("RPG4.Frontend.Port." + server.ServerId, selectedPort); PlayerPrefs.Save();
            main.serverCard.text = server.DisplayName + "\n" + selectedAddress + ":" + selectedPort; OpenLogin();
        }
        private void ValidateEndpoint()
        {
            bool valid = ClientServerCatalog.IsValidAddress(servers.address.text) && int.TryParse(servers.port.text, out int port) && port > 0 && port < 65536;
            servers.continueButton.interactable = session.CanSubmit && valid && session.ServerCatalog != null && session.ServerCatalog.Get(selectedIndex) != null;
            servers.validationLabel.text = valid ? "服务器标识与可信指纹由管理员配置，修改地址不会修改信任。" : "请输入有效服务器地址与 1–65535 端口。";
        }
        private void ValidateLogin()
        {
            bool configuration = session.ValidateConfiguration(selectedIndex, selectedAddress, selectedPort, out string reason);
            bool form = RPG4.Networking.Contracts.EntryProtocol.IsUsername(login.username.text)
                && RPG4.Networking.Contracts.EntryProtocol.IsUtf8(login.password.text, 1, RPG4.Networking.Contracts.EntryProtocol.MaxPasswordBytes);
            login.submitButton.interactable = session.CanSubmit && configuration && form;
            login.validationLabel.text = !configuration ? reason : !form ? "请输入已有账号和密码；账号由服务器管理员创建。" : "安全配置已就绪，可以登录。";
        }
        private void Submit()
        {
            if (!login.IsOpen || !session.CanSubmit || !login.submitButton.IsInteractable()) return;
            try
            {
                if (session.TryLogin(selectedIndex, selectedAddress, selectedPort, login.username.text, login.password.text, out string reason))
                {
                    PlayerPrefs.SetString("RPG4.Frontend.Username", login.username.text);
                    var server = session.ServerCatalog.Get(selectedIndex);
                    PlayerPrefs.SetString("RPG4.Frontend.Server", server.ServerId);
                    PlayerPrefs.SetString("RPG4.Frontend.Address." + server.ServerId, selectedAddress);
                    PlayerPrefs.SetInt("RPG4.Frontend.Port." + server.ServerId, selectedPort);
                    PlayerPrefs.Save();
                    main.SetInputEnabled(false); login.submitButton.interactable = false;
                }
                else login.validationLabel.text = reason;
            }
            finally
            {
                login.ClearPassword();
                // Clearing without a value-changed event must also invalidate the previous form result.
                login.submitButton.interactable = false;
            }
        }
        private void ClearCredentials() { if (login != null) login.ClearPassword(); }
        private void RenderStatus(ClientSessionStatus status)
        {
            if (main == null) return;
            main.statusLabel.text = status.Failure ?? status.Message;
            bool idle = status.State == ClientSessionState.MenuIdle;
            if (!idle) { login.Close(); servers.Close(); settings.Close(); }
            main.SetInputEnabled(idle && !login.IsOpen && !servers.IsOpen && !settings.IsOpen);
            if (idle) { main.SelectFirst(); ValidateLogin(); }
        }
        private void OnDestroy()
        {
            if (subscribed && !ReferenceEquals(session, null)) { session.StatusChanged -= RenderStatus; session.CredentialsDispatched -= ClearCredentials; }
            if (login != null) { login.ClearPassword(); login.Submitted -= Submit; login.CloseRequested -= ClosePanels; }
            if (servers != null) servers.CloseRequested -= ClosePanels;
            if (settings != null) settings.CloseRequested -= ClosePanels;
        }
    }
}
