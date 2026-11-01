using System;
using System.Collections.Generic;
using Mirror;
using RPG4.PlayerMovement;
using RPG4.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using RPG4.Backpack.Items;
using RPG4.Networking.Contracts;

namespace RPG4.Networking
{
    /// <summary>Implemented by scene UI adapters. A successful call must bind to the supplied player only.</summary>
    public interface IClientGameplayBinding
    {
        void BindLocalPlayer(Player player);
        void UnbindLocalPlayer();
        bool IsBoundTo(Player player);
    }

    /// <summary>S1 supplies the actual trusted spawn/profile configuration and its validation.
    /// Its scene component must be in a shared client/server assembly so OnlineGame resolves on both builds.</summary>
    public interface IServerGameplaySpawnConfiguration
    {
        bool ValidateServerConfiguration(out string reason);
    }

    /// <summary>S2 extension. Only the server manager invokes this before AddPlayerForConnection.
    /// The object has run Awake but is not spawned/isServer yet. Validate and initialize trusted identity,
    /// local authority cores, shared IDs, inventory and equipment synchronously; never publish/default-spawn
    /// first or wait for Start. On false the manager destroys the unpublished object and disconnects.</summary>
    public interface IServerPlayerSpawnInitializer
    {
        bool TryInitializeBeforeSpawn(GameObject player, TrustedPlayerSpawnContext context, out string reason);
    }

    public readonly struct TrustedPlayerSpawnContext
    {
        public AuthenticatedIdentity Identity { get; }
        public GameplaySceneBindings Bindings { get; }
        public Transform SpawnPoint { get; }
        public ItemIdGenerator ItemIds { get; }
        public long ServerWorldGeneration { get; }
        internal TrustedPlayerSpawnContext(AuthenticatedIdentity identity, GameplaySceneBindings bindings,
            Transform spawnPoint, ItemIdGenerator itemIds, long worldGeneration)
        {
            Identity = identity; Bindings = bindings; SpawnPoint = spawnPoint;
            ItemIds = itemIds; ServerWorldGeneration = worldGeneration;
        }
    }

    /// <summary>Scene-owned references. This component and every scene reference must belong to the online scene.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/Gameplay Scene Bindings")]
    public sealed class GameplaySceneBindings : MonoBehaviour
    {
        [SerializeField] private PlayerCameraController localCamera;
        [SerializeField] private CharacterHealthView[] localHealthViews = Array.Empty<CharacterHealthView>();
        [Tooltip("Include the required backpack/equipment UI adapter(s). Missing adapters never count as ready.")]
        [SerializeField] private MonoBehaviour[] requiredClientBindings = Array.Empty<MonoBehaviour>();
        [SerializeField] private NetworkStartPosition[] spawnPoints = Array.Empty<NetworkStartPosition>();
        [SerializeField] private Collider[] additionalCombatTargets = Array.Empty<Collider>();
        [Tooltip("Shared scene component implementing IServerGameplaySpawnConfiguration and IServerPlayerSpawnInitializer. It must resolve in both builds; S0 supplies no default-ready implementation.")]
        [SerializeField] private MonoBehaviour trustedSpawnConfiguration;

        private static readonly HashSet<GameplaySceneBindings> registered = new HashSet<GameplaySceneBindings>();
        internal static event Action RegistrationChanged;
        private Player boundPlayer;
        private long bindingRevision;
        private bool bindingInProgress, unbindingInProgress;
        public string ClientBindingFailureReason { get; private set; }

        public PlayerCameraController LocalCamera => localCamera;
        public IReadOnlyList<CharacterHealthView> LocalHealthViews => localHealthViews;
        public IReadOnlyList<MonoBehaviour> RequiredClientBindings => requiredClientBindings;
        public IReadOnlyList<NetworkStartPosition> SpawnPoints => spawnPoints;
        public IReadOnlyList<Collider> AdditionalCombatTargets => additionalCombatTargets;
        public MonoBehaviour TrustedSpawnConfiguration => trustedSpawnConfiguration;
        public Player BoundLocalPlayer => boundPlayer;
        public bool IsSceneValid => isActiveAndEnabled && gameObject.scene.IsValid() && gameObject.scene.isLoaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { registered.Clear(); RegistrationChanged = null; }

        private void OnEnable() { registered.Add(this); PublishRegistrationChanged(); }
        private void OnDisable() => Unregister();
        private void OnDestroy() => Unregister();
        private void Unregister()
        {
            UnbindLocalPlayer();
            if (registered.Remove(this)) PublishRegistrationChanged();
        }

        private static void PublishRegistrationChanged()
        {
            Action handlers = RegistrationChanged;
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try { ((Action)handler)(); }
                catch (Exception) { Debug.LogError("A gameplay scene registration subscriber failed."); }
            }
        }

        private bool TryAdapterAction(Action action)
        {
            try { action(); return true; }
            catch (Exception)
            {
                // Fixed messages avoid disclosing arbitrary adapter exception text during login/cleanup.
                Debug.LogError("A gameplay scene binding adapter failed.", this);
                ClientBindingFailureReason = "A gameplay scene binding adapter failed.";
                return false;
            }
        }

        private bool IsAdapterBound(IClientGameplayBinding binding, Player player)
        {
            try { return binding.IsBoundTo(player); }
            catch (Exception)
            {
                Debug.LogError("A gameplay scene binding adapter failed.", this);
                ClientBindingFailureReason = "A gameplay scene binding adapter failed.";
                return false;
            }
        }

        internal static GameplaySceneBindings FindForScene(Scene scene)
        {
            GameplaySceneBindings result = null;
            foreach (GameplaySceneBindings candidate in registered)
            {
                if (candidate == null || !candidate.IsSceneValid || candidate.gameObject.scene != scene) continue;
                // Ambiguous scene ownership is a configuration error, never an arbitrary first match.
                if (result != null) return null;
                result = candidate;
            }
            return result;
        }

        private bool Owns(Component component) => component != null && component.gameObject.scene == gameObject.scene;

        public bool ValidateServerWorld(out string reason)
        {
            if (!IsSceneValid) { reason = "Gameplay bindings scene is not loaded and enabled."; return false; }
            if (spawnPoints == null || spawnPoints.Length == 0)
            { reason = "Online scene has no configured spawn points."; return false; }
            foreach (NetworkStartPosition point in spawnPoints)
                if (!Owns(point) || !point.isActiveAndEnabled || !NetworkManager.startPositions.Contains(point.transform))
                { reason = "A spawn point is missing, disabled, outside this scene, or not registered with Mirror."; return false; }
            if (additionalCombatTargets == null)
            { reason = "Combat target configuration is missing."; return false; }
            foreach (Collider target in additionalCombatTargets)
                // Death/respawn may disable hit colliders. Combat history filters their dynamic state.
                if (!Owns(target))
                { reason = "A configured combat target is missing or outside this scene."; return false; }
            if (!Owns(trustedSpawnConfiguration) || !trustedSpawnConfiguration.isActiveAndEnabled
                || !(trustedSpawnConfiguration is IServerGameplaySpawnConfiguration configuration))
            { reason = "A trusted server spawn configuration validator is required in the online scene."; return false; }
            try { return configuration.ValidateServerConfiguration(out reason); }
            catch (Exception)
            { reason = "Trusted server spawn configuration validation failed."; return false; }
        }

        internal Collider[] GetCombatTargets() => additionalCombatTargets;

        public bool BindLocalPlayer(Player player)
        {
            if (bindingInProgress || unbindingInProgress) return false;
            ClientBindingFailureReason = null;
            bool changed = boundPlayer != player;
            if (boundPlayer != player) UnbindLocalPlayer();
            if (!ValidateClientConfiguration(out _) || !Owns(player) || !player.isActiveAndEnabled
                || player.LocalView == null || player.Attributes == null) return false;
#if UNITY_SERVER
            return false;
#else
            if (!NetworkClient.isConnected) return false;
            bindingInProgress = true;
            long revision = ++bindingRevision;
            try
            {
                boundPlayer = player;
                if (!localCamera.IsBoundTo(player.LocalView))
                {
                    if (!TryAdapterAction(() => localCamera.BindView(player.LocalView)) || !IsCurrentBinding(player, revision)) return false;
                    changed = true;
                }
                foreach (CharacterHealthView view in localHealthViews)
                    if (view.SourceAttributes != player.Attributes)
                    {
                        if (!TryAdapterAction(() => view.BindAttributes(player.Attributes)) || !IsCurrentBinding(player, revision)) return false;
                        changed = true;
                    }
                foreach (MonoBehaviour adapter in requiredClientBindings)
                {
                    var binding = (IClientGameplayBinding)adapter;
                    bool alreadyBound = IsAdapterBound(binding, player);
                    if (!IsCurrentBinding(player, revision)) return false;
                    if (!alreadyBound)
                    {
                        if (!TryAdapterAction(() => binding.BindLocalPlayer(player)) || !IsCurrentBinding(player, revision)) return false;
                        changed = true;
                    }
                }
                if (changed && (!TryAdapterAction(player.RefreshLocalPresentationBindings) || !IsCurrentBinding(player, revision))) return false;
                return IsBoundTo(player);
            }
            finally { bindingInProgress = false; }
#endif
        }

        private bool IsCurrentBinding(Player player, long revision) => bindingRevision == revision
            && boundPlayer == player && IsSceneValid && Owns(player) && player.isActiveAndEnabled && NetworkClient.isConnected
            && NetworkClient.localPlayer != null && NetworkClient.localPlayer.gameObject == player.gameObject
            && NetworkClient.connection != null && NetworkClient.connection.identity == NetworkClient.localPlayer;

        public bool IsBoundTo(Player player)
        {
            long revision = bindingRevision;
            if (!ValidateClientConfiguration(out _) || player == null || boundPlayer != player || player.LocalView == null
                || player.Attributes == null
                || !localCamera.IsBoundTo(player.LocalView)) return false;
            foreach (CharacterHealthView view in localHealthViews)
                if (!Owns(view) || !view.isActiveAndEnabled || view.SourceAttributes != player.Attributes) return false;
            foreach (MonoBehaviour adapter in requiredClientBindings)
                if (!Owns(adapter) || !adapter.isActiveAndEnabled || !(adapter is IClientGameplayBinding binding)
                    || !IsAdapterBound(binding, player)) return false;
            return IsCurrentBinding(player, revision);
        }

        public bool ValidateClientConfiguration(out string reason)
        {
            if (!IsSceneValid || !Owns(localCamera) || !localCamera.isActiveAndEnabled)
            { reason = "The online scene requires its own enabled local camera binding."; return false; }
            if (localHealthViews == null || localHealthViews.Length == 0
                || requiredClientBindings == null || requiredClientBindings.Length == 0)
            { reason = "The online scene requires health and gameplay UI binding adapters."; return false; }
            foreach (CharacterHealthView view in localHealthViews)
                if (!Owns(view) || !view.isActiveAndEnabled)
                { reason = "A health view is missing, disabled, or outside the online scene."; return false; }
            foreach (MonoBehaviour adapter in requiredClientBindings)
                if (!Owns(adapter) || !adapter.isActiveAndEnabled || !(adapter is IClientGameplayBinding))
                { reason = "A gameplay UI adapter is missing, disabled, invalid, or outside the online scene."; return false; }
            reason = null; return true;
        }

        public void UnbindLocalPlayer()
        {
            // Destroyed Unity objects still need their scene UI/camera subscriptions cleared.
            if (ReferenceEquals(boundPlayer, null)) return;
            if (unbindingInProgress) return;
            unbindingInProgress = true;
            bindingRevision++;
            // Clear ownership first so reentrant adapters cannot repeat or revive this cleanup.
            boundPlayer = null;
            try
            {
                if (Owns(localCamera)) TryAdapterAction(() => localCamera.UnbindView());
                if (localHealthViews != null)
                    foreach (CharacterHealthView view in localHealthViews)
                        if (Owns(view)) TryAdapterAction(() => view.BindAttributes(null));
                if (requiredClientBindings != null)
                    foreach (MonoBehaviour adapter in requiredClientBindings)
                        if (Owns(adapter) && adapter is IClientGameplayBinding binding)
                            TryAdapterAction(binding.UnbindLocalPlayer);
            }
            finally { unbindingInProgress = false; }
        }
    }
}
