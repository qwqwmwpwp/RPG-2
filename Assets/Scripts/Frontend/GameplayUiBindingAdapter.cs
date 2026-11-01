using Mirror;
using RPG4.Backpack.Network;
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
        [SerializeField] private InventoryUIInputRouter inventoryInput;
        [SerializeField] private InventoryUICoordinator inventoryCoordinator;
        [SerializeField] private GameObject[] clientOnlyRoots = System.Array.Empty<GameObject>();
        private Player boundPlayer;
        private BackpackGridUI window;
        private BackpackNetworkInventory inventory;
        private BackpackNetworkEquipment equipment;
        private SettingsView settings;
        private ClientSessionController observedSession;
        private AuthoritativeNetworkManager observedManager;
        public void BindLocalPlayer(Player player)
        {
            if (boundPlayer != player) UnbindLocalPlayer();
            if (!IsCurrentOwnedPlayer(player) || !Owns(uiManager) || !uiManager.isActiveAndEnabled
                || !Owns(weaponHud) || !weaponHud.isActiveAndEnabled || !Owns(inventoryInput)
                || !Owns(inventoryCoordinator))
            { UnbindLocalPlayer(); return; }
            window = uiManager.Get<BackpackGridUI>();
            settings = uiManager.Get<SettingsView>();
            if (window == null || !player.TryGetComponent(out inventory) || !player.TryGetComponent(out equipment)
                || !inventory.IsReady || !equipment.IsReady || settings == null) return;
            if (!window.BindSessionSources(inventory, equipment)) return;
            if (!weaponHud.BindSessionSource(inventory)) return;
            boundPlayer = player;
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
            && window != null && window.Manager == uiManager && window.isActiveAndEnabled
            && inventory != null && equipment != null && inventory.gameObject == player.gameObject && equipment.gameObject == player.gameObject
            && inventory.IsReady && equipment.IsReady && window.IsSessionBoundTo(inventory, equipment)
            && Owns(weaponHud) && weaponHud.IsSessionBoundTo(inventory)
            && Owns(settings) && settings.Manager == uiManager && settings.isActiveAndEnabled
            // Router stays disabled until Playing; its enabled flag must not be a readiness prerequisite.
            && Owns(inventoryInput) && Owns(inventoryCoordinator);
        public void UnbindLocalPlayer()
        {
            boundPlayer = null;
            if (inventoryCoordinator != null) inventoryCoordinator.enabled = false;
            if (inventoryInput != null) inventoryInput.enabled = false;
            if (uiManager != null) uiManager.CloseAll();
            if (window != null) window.UnbindSessionSources();
            if (weaponHud != null) weaponHud.UnbindSessionSource();
            window = null; inventory = null; equipment = null; settings = null;
        }
        private void Awake()
        {
            if (!NetworkClient.active && clientOnlyRoots != null)
                foreach (GameObject root in clientOnlyRoots)
                    if (root != null && root != gameObject && !transform.IsChildOf(root.transform)) root.SetActive(false);
        }
        private void OnEnable()
        {
            if (weaponHud != null) weaponHud.SessionBindingChanged += OnWeaponHudBindingChanged;
            ObserveSession(); ObserveManager(); SyncInputGate();
        }
        private void OnDisable() { StopObserving(); UnbindLocalPlayer(); }
        private void OnDestroy() { StopObserving(); UnbindLocalPlayer(); }
        private void StopObserving()
        {
            if (!ReferenceEquals(weaponHud, null)) weaponHud.SessionBindingChanged -= OnWeaponHudBindingChanged;
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
        private void OnWeaponHudBindingChanged(WeaponAmmoHUD _) => SyncInputGate();
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
        private void OnSessionStatusChanged(ClientSessionStatus _) => SyncInputGate();
        private void SyncInputGate()
        {
            if (!Owns(inventoryInput) || !Owns(inventoryCoordinator)) return;
            bool wasEnabled = inventoryInput.enabled || inventoryCoordinator.enabled;
            bool enabled = isActiveAndEnabled && observedSession != null
                && ReferenceEquals(observedSession, ClientSessionController.Instance)
                && observedSession.State == ClientSessionState.Playing && IsBoundTo(boundPlayer)
                && boundPlayer.TryGetComponent(out NetworkPlayerController controller)
                && controller.HasInitialAuthoritativeState && controller.LocalGameplayInputAllowed
                && observedManager != null && ReferenceEquals(NetworkManager.singleton, observedManager)
                && observedManager.IsLocalGameplayReady
                && ReferenceEquals(observedManager.CurrentGameplayLifecycle.LocalPlayer, controller);
            // InputSystem may already hold a delegate when OnDisable removes its subscription. The existing
            // router callbacks also check their coordinator, so close that policy gate before unsubscribing.
            if (inventoryCoordinator.enabled != enabled) inventoryCoordinator.enabled = enabled;
            if (inventoryInput.enabled != enabled) inventoryInput.enabled = enabled;
            if (!enabled && wasEnabled && Owns(uiManager)) uiManager.CloseAll();
        }
        private void Update()
        {
            ObserveSession(); ObserveManager(); SyncInputGate();
            var session = ClientSessionController.Instance;
            if (boundPlayer == null || session == null || session.State != ClientSessionState.Playing
                || inventoryInput == null || !inventoryInput.enabled
                || uiManager == null || uiManager.BlocksGameplayInput || Keyboard.current == null) return;
            if (Keyboard.current.escapeKey.wasPressedThisFrame) uiManager.Open<SettingsView>();
        }
    }
}
