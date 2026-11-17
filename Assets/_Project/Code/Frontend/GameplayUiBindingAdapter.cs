using Mirror;
using RPG4.LoadoutTemplate;
using RPG4.Backpack.UI;
using RPG4.Networking;
using RPG4.PlayerMovement;
using RPG4.UI;
using UnityEngine;
using UnityEngine.InputSystem;
namespace RPG4.Frontend
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-250)] // Open the modal before PlayerInput (-200) handles Escape/cursor capture.
    public sealed class GameplayUiBindingAdapter : MonoBehaviour, IClientGameplayBinding
    {
        [SerializeField] private UIManager uiManager;
        [SerializeField] private WeaponAmmoHUD weaponHud;
        [SerializeField] private GameObject[] clientOnlyRoots = System.Array.Empty<GameObject>();
        private Player boundPlayer;
        private NetworkRoleLoadout loadout;
        private SettingsView settings;
        private PlayerDeathUI deathWindow;
        private ClientSessionController observedSession;
        private AuthoritativeNetworkManager observedManager;
        public void BindLocalPlayer(Player player)
        {
            if (boundPlayer != player) UnbindLocalPlayer();
            if (!IsCurrentOwnedPlayer(player) || !Owns(uiManager) || !uiManager.isActiveAndEnabled)
            { UnbindLocalPlayer(); return; }
            settings = uiManager.Get<SettingsView>();
            ObserveDeathWindow();
            if (!player.TryGetComponent(out loadout) || !loadout.IsReady || settings == null) return;
            boundPlayer = player;
            if (weaponHud != null) weaponHud.BindSessionSource(player.Equipment);
            SyncInputGate();
        }
        private bool Owns(Component component) => component != null && component.gameObject.scene == gameObject.scene;
        private bool IsCurrentOwnedPlayer(Player player) => player != null && player.isActiveAndEnabled
            && player.gameObject.scene == gameObject.scene && NetworkClient.localPlayer != null
            && NetworkClient.localPlayer.gameObject == player.gameObject
            && player.TryGetComponent(out NetworkPlayerController controller)
            && controller.isActiveAndEnabled && controller.isLocalPlayer && controller.isOwned;
        public bool IsBoundTo(Player player) => isActiveAndEnabled && IsCurrentOwnedPlayer(player) && boundPlayer == player
            && uiManager != null && uiManager.isActiveAndEnabled && uiManager.gameObject.scene == gameObject.scene
            && loadout != null && loadout.gameObject == player.gameObject && loadout.IsReady
            && Owns(settings) && settings.Manager == uiManager && settings.isActiveAndEnabled;
        public void UnbindLocalPlayer()
        {
            if (deathWindow != null) deathWindow.ReturnToLobbyRequested -= ReturnToLobby;
            deathWindow = null;
            boundPlayer = null;
            if (uiManager != null) uiManager.CloseAll();
            if (weaponHud != null) weaponHud.UnbindSessionSource();
            loadout?.SetLocalInputAllowed(false);
            loadout = null; settings = null;
        }
        private void Awake()
        {
            if (!NetworkClient.active && clientOnlyRoots != null)
                foreach (GameObject root in clientOnlyRoots)
                    if (root != null && root != gameObject && !transform.IsChildOf(root.transform)) root.SetActive(false);
        }
        private void OnEnable()
        {
            ObserveSession(); ObserveManager(); SyncInputGate();
        }
        private void OnDisable() { StopObserving(); UnbindLocalPlayer(); }
        private void OnDestroy() { StopObserving(); UnbindLocalPlayer(); }
        private void StopObserving()
        {
            StopObservingSession(); StopObservingManager();
        }
        private void ObserveManager()
        {
            var current = NetworkManager.singleton as AuthoritativeNetworkManager;
            if (ReferenceEquals(observedManager, current)) return;
            StopObservingManager(); observedManager = current;
            if (observedManager != null) observedManager.LocalGameplayReadyChanged += OnGameplayReadyChanged;
        }
        private void StopObservingManager()
        {
            if (!ReferenceEquals(observedManager, null)) observedManager.LocalGameplayReadyChanged -= OnGameplayReadyChanged;
            observedManager = null;
        }
        private void OnGameplayReadyChanged(GameplayLifecycleEvent value)
        {
            if (observedManager == null || !ReferenceEquals(value.Session.Manager, observedManager)
                || value.Session.ManagerInstanceId != observedManager.GetInstanceID()
                || value.Session.SessionId != observedManager.ClientSessionId
                || !ReferenceEquals(value.Session.Connection, observedManager.CurrentClientSession.Connection)) return;
            var current = observedManager.CurrentGameplayLifecycle;
            if (value.Revision != current.Revision || value.SceneHandle != current.SceneHandle
                || value.ServerWorldGeneration != current.ServerWorldGeneration
                || !ReferenceEquals(value.LocalPlayer, current.LocalPlayer)) return;
            SyncInputGate();
        }
        private void ObserveSession()
        {
            if (ReferenceEquals(observedSession, ClientSessionController.Instance)) return;
            StopObservingSession();
            observedSession = ClientSessionController.Instance;
            if (observedSession != null) observedSession.StatusChanged += OnSessionStatusChanged;
        }
        private void StopObservingSession()
        {
            if (!ReferenceEquals(observedSession, null)) observedSession.StatusChanged -= OnSessionStatusChanged;
            observedSession = null;
        }
        private void OnSessionStatusChanged(ClientSessionStatus _) { SyncInputGate(); RefreshRaidResult(); }
        private void ObserveDeathWindow()
        {
            var current = uiManager != null ? uiManager.Get<PlayerDeathUI>() : null;
            if (current == deathWindow) return;
            if (deathWindow != null) deathWindow.ReturnToLobbyRequested -= ReturnToLobby;
            deathWindow = current;
            if (deathWindow != null) deathWindow.ReturnToLobbyRequested += ReturnToLobby;
            RefreshRaidResult();
        }
        private void RefreshRaidResult()
        {
            if (deathWindow != null) deathWindow.SetRaidResult(observedSession != null ? observedSession.LastRaidResultText : string.Empty,
                observedSession != null && observedSession.State == ClientSessionState.Playing);
        }
        private void ReturnToLobby()
        {
            if (observedSession != null && !observedSession.TryReturnToLobby(out string reason))
                deathWindow.SetRaidResult(reason, true);
        }
        private void SyncInputGate()
        {
            // Legacy inventory routers stay closed; role UI owns its Tab and drag policy.
            bool allowed = isActiveAndEnabled && observedSession != null
                && ReferenceEquals(observedSession, ClientSessionController.Instance)
                && observedSession.State == ClientSessionState.Playing && IsBoundTo(boundPlayer)
                && boundPlayer.TryGetComponent(out NetworkPlayerController controller)
                && controller.HasInitialAuthoritativeState && controller.LocalGameplayInputAllowed
                && observedManager != null && ReferenceEquals(NetworkManager.singleton, observedManager)
                && observedManager.IsLocalGameplayReady
                && ReferenceEquals(observedManager.CurrentGameplayLifecycle.LocalPlayer, controller)
                && uiManager != null && !uiManager.BlocksGameplayInput;
            loadout?.SetLocalInputAllowed(allowed);
        }

        private void Update()
        {
            ObserveSession(); ObserveManager(); SyncInputGate(); ObserveDeathWindow(); RefreshRaidResult();
            var session = ClientSessionController.Instance;
            if (boundPlayer == null || session == null || session.State != ClientSessionState.Playing
                || uiManager == null || uiManager.BlocksGameplayInput || Keyboard.current == null) return;
            if (Keyboard.current.escapeKey.wasPressedThisFrame && (loadout == null || !loadout.IsWindowOpen)) uiManager.Open<SettingsView>();
        }
    }
}
