using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cinemachine;
using Mirror;
using Mirror.Transports.Encryption;
using RPG4.Backpack.Items;
using RPG4.Backpack.Network;
using RPG4.Backpack.Runtime;
using RPG4.Backpack.UI;
using RPG4.Frontend;
using RPG4.Input;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using RPG4.Networking.Transports;
using RPG4.PlayerMovement;
using RPG4.Server;
using RPG4.UI;
using TMPro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RPG4.Editor.Build
{
    /// <summary>Read authored entry configuration, never start gameplay, call runtime world validation, save
    /// scenes or generate secrets. Empty digest/pins are handled by the build preflight;
    /// structural validation must also work before the first content-manifest generation.</summary>
    public static class EntrySceneValidation
    {
        private const string ClientBootstrapPath = "Assets/Scenes/ClientBootstrap.unity";
        private const string ServerBootstrapPath = "Assets/Scenes/ServerBootstrap.unity";
        private const string ManagerPath = "Assets/player/OnlineNetworkManager.prefab";
        private const string PlayerPath = "Assets/player/OnlinePlayer.prefab";
        private const string ManifestPath = "Assets/Settings/EntryContentManifest.asset";
        private const string CatalogPath = "Assets/Settings/ClientServerCatalog.asset";
        private const string MenuPrefabPath = "Assets/UI/Frontend/ClientMenu.prefab";

        public static void Validate()
        {
            RequireEditorInspectionIdle();
            foreach (string path in new[] { ClientBootstrapPath, EntryContentManifest.MainMenuScenePath,
                ServerBootstrapPath, EntryProtocol.GameplayScene }) Load<SceneAsset>(path);
            var scenes = new[] { ClientBootstrapPath, EntryContentManifest.MainMenuScenePath, ServerBootstrapPath, EntryProtocol.GameplayScene };
            Require(scenes.Select(path => AssetDatabase.AssetPathToGUID(path)).Distinct().Count() == scenes.Length,
                "Entry scenes must have independent asset GUIDs.");
            string[] dependencies = AssetDatabase.GetDependencies(scenes, true);
            Require(!dependencies.Contains("Assets/Scenes/Main.unity")
                && !dependencies.Contains("Assets/player/AuthoritativeNetworkManager.prefab"),
                "Entry assets must not reference Main or the protected legacy manager prefab.");
            var manifest = Load<EntryContentManifest>(ManifestPath);
            var catalog = Load<ClientServerCatalog>(CatalogPath);
            Require(Reference<EntryContentManifest>(catalog, "contentManifest") == manifest,
                "ClientServerCatalog must reference the shared EntryContentManifest asset.");
            var prefab = Load<GameObject>(ManagerPath);
            var manager = prefab.GetComponent<AuthoritativeNetworkManager>();
            ValidateManager(prefab, manager);
            var authoredIds = ReadAuthoredSceneIds(EntryProtocol.GameplayScene);
            Preview(ClientBootstrapPath, scene => ValidateClientBootstrap(scene, manager, catalog));
            Preview(ServerBootstrapPath, scene => ValidateServerBootstrap(scene, manager, manifest));
            Preview(EntryContentManifest.MainMenuScenePath, ValidateMenu);
            Preview(EntryProtocol.GameplayScene, scene => ValidateWorld(scene, authoredIds.Count));
        }

        public static void ValidateSceneOrder(string[] scenes, bool server)
        {
            string[] expected = server ? new[] { ServerBootstrapPath, EntryProtocol.GameplayScene }
                : new[] { ClientBootstrapPath, EntryContentManifest.MainMenuScenePath, EntryProtocol.GameplayScene };
            Require(scenes != null && scenes.SequenceEqual(expected, StringComparer.Ordinal),
                server ? "Dedicated scenes must be ServerBootstrap -> OnlineGame."
                    : "Client scenes must be ClientBootstrap -> MainMenu -> OnlineGame.");
        }

        /// <summary>Stable identity projection for validators/content generation. Preview OnValidate can assign
        /// temporary duplicate IDs when the same scene is already open. Only persisted YAML is authoritative.
        /// Keys are NetworkIdentity component fileIDs (GlobalObjectId.targetObjectId), not hierarchy names.</summary>
        public static IReadOnlyDictionary<long, ulong> ReadAuthoredSceneIds(string scenePath)
        {
            Require(scenePath == EntryProtocol.GameplayScene, "Only the agreed OnlineGame scene identity projection is supported.");
            Load<SceneAsset>(scenePath);
            string guid = AssetDatabase.AssetPathToGUID("Assets/Mirror/Core/NetworkIdentity.cs");
            Require(!string.IsNullOrEmpty(guid), "Mirror NetworkIdentity script asset is missing.");
            var result = new Dictionary<long, ulong>();
            var seen = new HashSet<ulong>();
            string text = File.ReadAllText(scenePath);
            foreach (Match block in Regex.Matches(text, @"(?ms)^--- !u!114 &(-?\d+)[^\r\n]*\r?\n(.*?)(?=^--- !u!|\z)"))
            {
                if (!Regex.IsMatch(block.Groups[2].Value, @"(?m)^\s*m_Script:\s*\{[^\r\n}]*guid:\s*" + guid + @"(?:,|\s*\})")) continue;
                Match value = Regex.Match(block.Groups[2].Value, @"(?m)^\s*sceneId:\s*(\d+)\s*$");
                Require(value.Success && ulong.TryParse(value.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out _), "OnlineGame NetworkIdentity has no persisted numeric sceneId.");
                // Parse separately so definite assignment is independent of Require's runtime throw contract.
                ulong parsed = ulong.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture);
                Require(parsed != 0 && (uint)parsed != 0 && parsed >> 32 == ScenePathHash(scenePath),
                    "OnlineGame sceneId must have a nonzero local ID and the current Mirror scene-path hash.");
                Require(seen.Add(parsed), "OnlineGame contains duplicate authored sceneIds.");
                long fileId = long.Parse(block.Groups[1].Value, CultureInfo.InvariantCulture);
                Require(!result.ContainsKey(fileId), "OnlineGame contains duplicate NetworkIdentity fileIDs.");
                result.Add(fileId, parsed);
            }
            Require(result.Count > 0, "OnlineGame has no persisted NetworkIdentity components; save valid authored scene IDs before building.");
            return result;
        }

        private static uint ScenePathHash(string path)
        {
            unchecked
            {
                uint hash = 0x811c9dc5;
                foreach (char character in path.ToLowerInvariant()) hash = (hash ^ (byte)character) * 0x1000193;
                return hash;
            }
        }

        /// <summary>Unity 2022.3 public APIs only. Reuse an already-open clean scene without closing it;
        /// otherwise own one temporary additive scene. Reject unsaved input, never save/restore a scene setup,
        /// and restore the previously active loaded scene if opening/inspection changed it.</summary>
        public static void ReadScene(string path, Action<Scene> read)
        {
            RequireEditorInspectionIdle();
            Require(read != null, "Scene inspection requires a read-only callback.");
            Load<SceneAsset>(path);
            Scene previousActive = SceneManager.GetActiveScene();
            Scene inspected = default;
            bool openedHere = false;
            bool openingAttempted = false;
            var originalHandles = new HashSet<int>();
            for (int i = 0; i < SceneManager.sceneCount; ++i)
            {
                Scene existing = SceneManager.GetSceneAt(i);
                originalHandles.Add(existing.handle);
                if (string.Equals(existing.path, path, StringComparison.OrdinalIgnoreCase)) inspected = existing;
            }
            try
            {
                if (inspected.IsValid())
                    Require(inspected.isLoaded, "The required scene is open but unloaded. Load it explicitly before inspection: " + path);
                else
                {
                    openingAttempted = true;
                    inspected = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    openedHere = inspected.IsValid() && !originalHandles.Contains(inspected.handle);
                }
                Require(inspected.IsValid() && inspected.isLoaded, "Could not open required scene for inspection: " + path);
                Require(!inspected.isDirty, openedHere
                    ? "Opening the inspection scene triggered editor changes; none were saved. Inspect its authored configuration and editor callbacks: " + path
                    : "Required scene has unsaved changes. Save or discard them explicitly before inspection: " + path);
                read(inspected);
                Require(!inspected.isDirty, "Scene inspection encountered unsaved changes; no changes were saved: " + path);
            }
            catch (BuildFailedException) { throw; }
            catch (Exception) { throw new BuildFailedException("Entry asset inspection failed in " + path + ". Inspect its serialized configuration."); }
            finally
            {
                // OpenScene can invoke editor callbacks before returning. Recover only our requested scene
                // if one was created before an exception; never remove a handle that predated this call.
                if (openingAttempted && !inspected.IsValid())
                {
                    Scene partial = SceneManager.GetSceneByPath(path);
                    if (partial.IsValid() && !originalHandles.Contains(partial.handle))
                    { inspected = partial; openedHere = true; }
                }
                try
                {
                    if (openedHere && inspected.IsValid())
                    {
                        try { RestoreActiveScene(previousActive); }
                        finally
                        {
                            Require(EditorSceneManager.CloseScene(inspected, true), "Could not close the temporary inspection scene: " + path);
                        }
                    }
                }
                finally { RestoreActiveScene(previousActive); }
            }
        }

        private static void RestoreActiveScene(Scene previous)
        {
            if (previous.IsValid() && previous.isLoaded && SceneManager.GetActiveScene() != previous)
                Require(SceneManager.SetActiveScene(previous), "Could not restore the previously active scene after entry inspection.");
        }

        private static void RequireEditorInspectionIdle()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating,
                "Inspect entry assets only while the editor is idle, outside Play mode, compilation and asset import.");
            Require(PrefabStageUtility.GetCurrentPrefabStage() == null,
                "Close the current Prefab Stage explicitly before entry inspection; validation does not leave or save it.");
        }

        private static void Preview(string path, Action<Scene> validate)
        {
            ReadScene(path, scene =>
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                        Require(component != null, "Missing script in " + path + ".");
                ValidateNoLegacyActors(scene);
                validate(scene);
            });
        }

        private static void ValidateNoLegacyActors(Scene scene)
        {
            Require(All<NetworkManager>(scene).Length == 0, scene.path + " must not contain a NetworkManager instance.");
            Require(All<Player>(scene).Length == 0 && All<NetworkPlayerController>(scene).Length == 0
                && All<CharacterBackpack>(scene).Length == 0, scene.path + " must not contain an offline or preplaced player.");
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
                {
                    string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(node.gameObject);
                    Require(source != "Assets/player/AuthoritativeNetworkManager.prefab",
                        scene.path + " contains the protected legacy manager prefab.");
                }
        }

        private static void ValidateManager(GameObject root, AuthoritativeNetworkManager manager)
        {
            Require(manager != null && root.GetComponentsInChildren<NetworkManager>(true).Length == 1
                && manager.gameObject == root && manager.UsesGameplaySceneBindings, "OnlineNetworkManager must have one root authoritative manager.");
            Require(manager.headlessStartMode == HeadlessStartOptions.DoNothing && Property(manager, "startMode").intValue == 0
                && Property(manager, "editorAutoStart").intValue == 0, "OnlineNetworkManager automatic startup modes must all be None.");
            Require(manager.onlineScene == EntryProtocol.GameplayScene && string.IsNullOrEmpty(manager.offlineScene)
                && manager.autoCreatePlayer && manager.dontDestroyOnLoad, "Shared manager scene/lifetime/player creation defaults are invalid.");
            Require(Property(manager, "offlinePlayer").objectReferenceValue == null
                && Property(manager, "localCamera").objectReferenceValue == null
                && Property(manager, "offlineSceneObjects").arraySize == 0
                && Property(manager, "additionalCombatTargets").arraySize == 0
                && root.GetComponentsInChildren<NetworkStartPosition>(true).Length == 0,
                "OnlineNetworkManager must not carry legacy scene references or child spawn points.");
            var authentication = manager.authenticator as AccountAuthenticator;
            var encryption = manager.transport as EncryptionTransport;
            var admission = encryption != null ? encryption.Inner as ConnectionAdmissionTransport : null;
            var kcp = admission != null ? admission.Inner as kcp2k.KcpTransport : null;
            Require(authentication != null && encryption != null && admission != null && kcp != null
                && authentication.gameObject == root && encryption.gameObject == root && admission.gameObject == root && kcp.gameObject == root
                && authentication.enabled && encryption.enabled && admission.enabled && kcp.enabled
                && root.GetComponentsInChildren<NetworkAuthenticator>(true).Length == 1
                && root.GetComponentsInChildren<Transport>(true).Length == 3,
                "Online transport chain must be enabled root AccountAuthenticator + Encryption -> Admission -> KCP.");
            Require(encryption.ClientValidateServerPubKey == EncryptionTransport.ValidationMode.Callback
                && encryption.ServerLoadKeyPairFromFile && string.IsNullOrEmpty(encryption.ServerKeypairPath),
                "Encrypted prefab must require callback pin validation and an externally supplied fixed server key path.");
            Require(kcp.Port != 0, "Online KCP port must be nonzero.");
            int total = Property(admission, "totalLimit").intValue, perIp = Property(admission, "perIpLimit").intValue;
            Require(total > 0 && perIp > 0 && perIp <= total, "Online raw admission limits are invalid.");
            var player = Load<GameObject>(PlayerPath);
            Require(manager.playerPrefab == player, "OnlineNetworkManager must reference OnlinePlayer.prefab.");
            ValidatePlayer(player);
            var ids = new HashSet<uint> { AuthoredAssetId(player.GetComponent<NetworkIdentity>()) };
            foreach (GameObject spawnable in manager.spawnPrefabs)
                Require(spawnable != null && spawnable.GetComponent<NetworkIdentity>() != null
                    && AuthoredAssetId(spawnable.GetComponent<NetworkIdentity>()) != 0
                    && ids.Add(AuthoredAssetId(spawnable.GetComponent<NetworkIdentity>())),
                    "Network spawn prefabs require unique, nonzero asset IDs.");
        }

        private static void ValidatePlayer(GameObject player)
        {
            Require(player.activeSelf && player.GetComponent<NetworkIdentity>() != null
                && AuthoredAssetId(player.GetComponent<NetworkIdentity>()) != 0
                && player.GetComponentsInChildren<NetworkIdentity>(true).Length == 1
                && player.GetComponent<NetworkIdentity>().sceneId == 0
                && player.GetComponent<OnlinePlayerIdentity>() != null && player.GetComponent<NetworkPlayerController>() != null
                && player.GetComponent<Player>() != null && player.GetComponent<PlayerEquipment>() != null
                && player.GetComponent<BackpackInventory>() != null && player.GetComponent<EquipmentInventory>() != null
                && player.GetComponent<BackpackNetworkInventory>() != null && player.GetComponent<BackpackNetworkEquipment>() != null
                && player.GetComponent<CharacterBackpack>() != null && player.GetComponent<Rigidbody>() != null
                && player.GetComponent<CapsuleCollider>() != null, "OnlinePlayer is missing authoritative player/identity/inventory/physics components.");
            var backpack = player.GetComponent<CharacterBackpack>();
            Require(!Property(backpack, "fillCatalogOnStart").boolValue && !Property(backpack, "isLocalPlayer").boolValue,
                "OnlinePlayer must disable debug fill and offline local ownership.");
            foreach (Behaviour component in new Behaviour[] { player.GetComponent<NetworkIdentity>(),
                player.GetComponent<OnlinePlayerIdentity>(), player.GetComponent<NetworkPlayerController>(), player.GetComponent<Player>(),
                player.GetComponent<PlayerEquipment>(), player.GetComponent<BackpackInventory>(), player.GetComponent<EquipmentInventory>(),
                player.GetComponent<BackpackNetworkInventory>(), player.GetComponent<BackpackNetworkEquipment>(), backpack })
                Require(component.enabled, "OnlinePlayer's required identity, simulation and inventory components must be enabled.");
            var inventory = player.GetComponent<BackpackInventory>();
            var networkInventory = player.GetComponent<BackpackNetworkInventory>();
            int rows = Property(inventory, "rows").intValue, columns = Property(inventory, "columns").intValue;
            Require(rows > 0 && columns > 0 && Property(networkInventory, "rows").intValue == rows
                && Property(networkInventory, "columns").intValue == columns
                && !Property(networkInventory, "sharedContainer").boolValue,
                "OnlinePlayer requires matching positive inventory dimensions and a private, non-shared network inventory.");
            var equipment = player.GetComponent<PlayerEquipment>();
            var rig = Reference<PlayerWeaponRig>(equipment, "weaponRig");
            Require(Reference<BackpackInventory>(equipment, "inventory") == inventory
                && Reference<EquipmentInventory>(equipment, "equipmentInventory") == player.GetComponent<EquipmentInventory>()
                && rig.gameObject == player && rig.enabled,
                "OnlinePlayer equipment must use its own inventory, equipment core and enabled weapon rig.");
            Require(Reference<Transform>(rig, "equipmentCarrier").IsChildOf(player.transform),
                "OnlinePlayer equipment carrier must belong to its authored player hierarchy.");
            var supportHand = Reference<Component>(rig, "supportHand");
            Require(supportHand.transform.IsChildOf(player.transform), "OnlinePlayer support-hand rig must belong to this player.");
            var gripTarget = Property(supportHand, "m_Data.m_Target").objectReferenceValue as Transform;
            Require(gripTarget != null && gripTarget.IsChildOf(player.transform),
                "OnlinePlayer support-hand rig requires an authored target in this player hierarchy.");
        }

        private static void ValidateClientBootstrap(Scene scene, AuthoritativeNetworkManager prefab, ClientServerCatalog catalog)
        {
            var bootstrap = Single<ClientBootstrap>(scene);
            var controller = Single<ClientSessionController>(scene);
            var status = Single<SessionStatusView>(scene);
            Require(bootstrap.enabled && bootstrap.gameObject.activeInHierarchy && controller.enabled
                && controller.gameObject.activeInHierarchy && status.enabled && status.gameObject.activeInHierarchy,
                "Client bootstrap, persistent session and status components must be enabled application objects.");
            Require(All<ServerBootstrap>(scene).Length == 0 && All<GameplaySceneBindings>(scene).Length == 0,
                "ClientBootstrap must not contain server/gameplay startup components.");
            Require(Reference<ClientSessionController>(bootstrap, "sessionController") == controller
                && Reference<AuthoritativeNetworkManager>(controller, "networkManagerPrefab") == prefab
                && Reference<ClientServerCatalog>(controller, "serverCatalog") == catalog
                && Reference<SessionStatusView>(controller, "sessionStatus") == status,
                "ClientBootstrap/controller/catalog/online manager/status references must form one application entry.");
            Require(controller.transform.parent == null && status.transform.IsChildOf(controller.transform),
                "Persistent client controller/status must share a root separate from each network manager.");
            Require(All<InputManager>(scene).Length == 1 && All<EventSystem>(scene).Length == 1
                && All<InputSystemUIInputModule>(scene).Length == 1, "ClientBootstrap requires one persistent gameplay input host and UI EventSystem.");
            foreach (Component input in new Component[] { Single<InputManager>(scene), Single<EventSystem>(scene), Single<InputSystemUIInputModule>(scene) })
                Require(input.transform.IsChildOf(controller.transform) && input.gameObject.activeInHierarchy
                    && ((Behaviour)input).enabled, "Enabled input hosts must survive with the independent client controller.");
            var module = Single<InputSystemUIInputModule>(scene);
            foreach (string field in new[] { "m_ActionsAsset", "m_PointAction", "m_LeftClickAction", "m_SubmitAction", "m_CancelAction", "m_MoveAction" })
                Require(Property(module, field).objectReferenceValue != null, "Client EventSystem is missing an authored UI input action: " + field);
            RequireReferencesInScene(status, scene, "overlayGroup", "phaseLabel", "failureLabel", "cancelButton", "leaveButton", "quitButton", "playingControls");
            foreach (string timeout in new[] { "connectionTimeout", "authenticationTimeout", "sceneTimeout", "playerTimeout" })
                Require(Property(controller, timeout).floatValue > 0, "Client phase timeout must be positive: " + timeout);
        }

        private static void ValidateServerBootstrap(Scene scene, AuthoritativeNetworkManager prefab, EntryContentManifest manifest)
        {
            var bootstrap = Single<ServerBootstrap>(scene);
            Require(bootstrap.enabled && bootstrap.gameObject.activeInHierarchy,
                "ServerBootstrap must be an enabled scene entry.");
            Require(Reference<AuthoritativeNetworkManager>(bootstrap, "networkManagerPrefab") == prefab
                && Reference<EntryContentManifest>(bootstrap, "contentManifest") == manifest,
                "ServerBootstrap must reference the shared online manager and content manifest.");
            Require(All<ClientBootstrap>(scene).Length == 0 && All<ClientSessionController>(scene).Length == 0
                && All<GameplaySceneBindings>(scene).Length == 0 && All<Canvas>(scene).Length == 0
                && All<Camera>(scene).Length == 0 && All<AudioListener>(scene).Length == 0 && All<AudioSource>(scene).Length == 0
                && All<InputManager>(scene).Length == 0 && All<EventSystem>(scene).Length == 0
                && All<InputSystemUIInputModule>(scene).Length == 0,
                "ServerBootstrap must contain no client application or presentation roots.");
        }

        private static void ValidateMenu(Scene scene)
        {
            Require(All<ClientBootstrap>(scene).Length == 0 && All<ServerBootstrap>(scene).Length == 0
                && All<ClientSessionController>(scene).Length == 0 && All<NetworkIdentity>(scene).Length == 0,
                "MainMenu must consume the persistent application rather than create another entry/network object.");
            var presenter = Single<MainMenuPresenter>(scene);
            Require(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(presenter.gameObject) == MenuPrefabPath,
                "MainMenu must instance the shared authored ClientMenu prefab.");
            var main = Single<MainMenuView>(scene); var login = Single<LoginPanelView>(scene);
            var servers = Single<ServerSelectionView>(scene); var settings = Single<SettingsView>(scene);
            Require(Reference<MainMenuView>(presenter, "main") == main && Reference<LoginPanelView>(presenter, "login") == login
                && Reference<ServerSelectionView>(presenter, "servers") == servers && Reference<SettingsView>(presenter, "settings") == settings,
                "MainMenuPresenter must reference this menu's four authored views.");
            RequireReferencesInScene(main, scene, "startButton", "serversButton", "settingsButton", "quitButton", "serverCard", "statusLabel", "inputGroup");
            RequireReferencesInScene(login, scene, "username", "password", "serverLabel", "validationLabel", "passwordVisibilityLabel", "submitButton", "closeButton", "passwordVisibilityButton");
            RequireReferencesInScene(servers, scene, "servers", "address", "port", "validationLabel", "continueButton", "closeButton");
            ValidateSettings(settings);
            foreach (FrontendWindow window in new FrontendWindow[] { main, login, servers, settings }) ValidateFocus(window);
            Require(Property(login.password, "m_InputType").intValue == (int)TMP_InputField.InputType.Password
                && login.password.characterLimit == 0, "Login password must be masked and must not silently truncate input.");
            Require(All<UIManager>(scene).Length == 1, "MainMenu requires one scene-owned UIManager.");
            Require(All<InputManager>(scene).Length == 0 && All<EventSystem>(scene).Length == 0
                && All<InputSystemUIInputModule>(scene).Length == 0,
                "MainMenu must use the persistent client input and EventSystem rather than duplicate them.");
        }

        private static void ValidateWorld(Scene scene, int authoredIdentityCount)
        {
            Require(All<ClientBootstrap>(scene).Length == 0 && All<ServerBootstrap>(scene).Length == 0
                && All<ClientSessionController>(scene).Length == 0 && All<InputManager>(scene).Length == 0
                && All<EventSystem>(scene).Length == 0 && All<InputSystemUIInputModule>(scene).Length == 0,
                "OnlineGame must not duplicate application/bootstrap/input lifetime owners.");
            var bindings = Single<GameplaySceneBindings>(scene);
            var loadout = Single<OnlineSpawnConfiguration>(scene);
            var presentation = Single<ClientPresentationRoot>(scene);
            var adapter = Single<GameplayUiBindingAdapter>(scene);
            Require(Reference<OnlineSpawnConfiguration>(bindings, "trustedSpawnConfiguration") == loadout,
                "OnlineGame bindings must reference its own trusted loadout component.");
            var roots = ReferenceArray<GameObject>(presentation, "clientOnlyRoots");
            Require(roots.Length > 0 && roots.All(root => root.scene == scene && !root.activeSelf
                && root != bindings.gameObject && !bindings.transform.IsChildOf(root.transform)),
                "Client presentation roots must start inactive and must not contain the world bindings.");
            foreach (Component world in new Component[] { bindings, loadout, presentation, adapter })
                Require(world.gameObject.activeInHierarchy && ((Behaviour)world).enabled && !InsidePresentation(world, roots),
                    "World lifecycle/configuration components must be enabled outside inactive client roots.");
            var spawns = ReferenceArray<NetworkStartPosition>(bindings, "spawnPoints");
            Require(spawns.Length >= 2 && spawns.Distinct().Count() == spawns.Length
                && All<NetworkStartPosition>(scene).Length == spawns.Length, "OnlineGame must list each of its own spawn points exactly once.");
            foreach (var spawn in spawns) Require(spawn.gameObject.scene == scene && spawn.enabled
                && spawn.gameObject.activeInHierarchy && !InsidePresentation(spawn, roots), "Spawn points must be enabled same-scene world objects.");
            foreach (Collider target in ReferenceArray<Collider>(bindings, "additionalCombatTargets", false))
            {
                Require(target.gameObject.scene == scene && target.enabled && !target.isTrigger
                    && target.gameObject.activeInHierarchy && !InsidePresentation(target, roots),
                    "Combat targets must remain enabled non-trigger same-scene world colliders.");
                var health = target.GetComponentInParent<NetworkCharacterHealth>();
                if (health != null)
                {
                    var identity = health.GetComponent<NetworkIdentity>();
                    Require(health.enabled && identity != null && identity.enabled && !identity.serverOnly,
                        "Network combat actors require enabled health synchronization and a client-visible scene identity.");
                }
            }
            Require(All<Collider>(scene).Any(collider => collider.enabled && !collider.isTrigger
                && collider.gameObject.activeInHierarchy && !InsidePresentation(collider, roots)), "OnlineGame requires active world collision.");
            Require(All<NetworkIdentity>(scene).Length == authoredIdentityCount,
                "Preview network identities differ from the persisted scene identity projection; inspect prefab identity serialization.");
            ValidateLoadout(loadout);
            var camera = Reference<PlayerCameraController>(bindings, "localCamera");
            var virtualCamera = Reference<CinemachineVirtualCamera>(camera, "virtualCamera");
            Require(virtualCamera.gameObject == camera.gameObject && virtualCamera.enabled
                && virtualCamera.GetComponentsInChildren<Cinemachine3rdPersonFollow>(true).Any(follow => follow.enabled)
                && Property(camera, "viewController").objectReferenceValue == null
                && Property(virtualCamera, "m_Follow").objectReferenceValue == null
                && Property(virtualCamera, "m_LookAt").objectReferenceValue == null,
                "Online camera must await the local player's view, with its own enabled virtual camera and 3rd Person Follow pipeline.");
            var renderCamera = Single<Camera>(scene);
            Require(renderCamera.enabled && renderCamera.CompareTag("MainCamera")
                && Single<AudioListener>(scene).gameObject == renderCamera.gameObject && Single<AudioListener>(scene).enabled,
                "OnlineGame requires one enabled MainCamera and its own enabled audio listener.");
            var views = ReferenceArray<CharacterHealthView>(bindings, "localHealthViews");
            Require(camera.enabled && views.All(view => view.enabled && view.gameObject.scene == scene && Property(view, "allowUnboundSource").boolValue
                && Property(view, "attributes").objectReferenceValue == null), "Local HUD must await the owned player's attributes.");
            foreach (var view in views) RequireReferencesInScene(view, scene, "healthFill", "healthLabel");
            var required = ReferenceArray<MonoBehaviour>(bindings, "requiredClientBindings");
            Require(required.Contains(adapter) && required.Distinct().Count() == required.Length
                && required.All(component => component.enabled && component is IClientGameplayBinding
                && component.gameObject.scene == scene), "Required client adapters must implement the shared binding contract in OnlineGame.");
            var ui = Reference<UIManager>(adapter, "uiManager");
            var hud = Reference<WeaponAmmoHUD>(adapter, "weaponHud");
            var router = Reference<InventoryUIInputRouter>(adapter, "inventoryInput");
            var coordinator = Reference<InventoryUICoordinator>(adapter, "inventoryCoordinator");
            Require(ui == Single<UIManager>(scene) && router.gameObject == ui.gameObject && coordinator.gameObject == ui.gameObject
                && ui.enabled && hud.enabled && !router.enabled && !coordinator.enabled && Property(hud, "explicitSessionSource").boolValue,
                "Online inventory input must start gated; its weapon HUD must require an explicit session source.");
            var windowRoot = Reference<RectTransform>(ui, "windowRoot");
            Require(windowRoot.gameObject.scene == scene && InsidePresentation(windowRoot, roots),
                "Online UIManager must instantiate windows under its own client presentation hierarchy.");
            foreach (Component client in All<Canvas>(scene).Cast<Component>().Concat(All<Camera>(scene))
                .Concat(All<AudioListener>(scene)).Concat(All<AudioSource>(scene)).Concat(All<UIManager>(scene)).Concat(All<CharacterHealthView>(scene))
                .Concat(new Component[] { camera, ui, hud, router, coordinator }))
                Require(client.gameObject.scene == scene && InsidePresentation(client, roots), "Client camera/UI/audio must be inside authored inactive presentation roots.");
            RequireReferencesInScene(hud, scene, "contentRoot", "weaponName", "rounds", "reserve", "durability", "actionLabel", "icon", "reloadProgress");
            var windows = ReferenceArray<UIBase>(ui, "windowPrefabs");
            foreach (UIBase window in windows)
                foreach (MonoBehaviour component in window.GetComponentsInChildren<MonoBehaviour>(true))
                    Require(component != null, "An online window prefab contains a missing script.");
            Require(windows.OfType<BackpackGridUI>().Count() == 1 && windows.OfType<ChestBackpackUI>().Count() == 1
                && windows.OfType<SettingsView>().Count() == 1, "Online UIManager requires one backpack, chest and settings window prefab.");
            var backpack = windows.OfType<BackpackGridUI>().Single();
            Require(backpack.ContainerUI != null && backpack.EquipmentView != null && backpack.ContainerUI != backpack.EquipmentView
                && backpack.Interaction != null, "Backpack prefab requires separate inventory/equipment views and its interaction controller.");
            Require(backpack.Interaction.transform.IsChildOf(backpack.transform),
                "Backpack interaction controller must belong to its window prefab.");
            ValidateContainerView(backpack, backpack.ContainerUI);
            ValidateContainerView(backpack, backpack.EquipmentView);
            ValidateSettings(windows.OfType<SettingsView>().Single());
            Require(Property(windows.OfType<SettingsView>().Single(), "blocksOtherWindows").boolValue,
                "Online settings must block inventory/chest windows while the settings modal is open.");
            var chests = All<ChestInventory>(scene);
            Require(chests.Length > 0, "OnlineGame requires an authored shared network chest.");
            foreach (var chest in chests)
            {
                var network = chest.GetComponent<BackpackNetworkInventory>();
                var identity = chest.GetComponent<NetworkIdentity>();
                var inventory = chest.GetComponent<BackpackInventory>();
                Require(chest.enabled && chest.gameObject.activeInHierarchy && identity != null && identity.enabled && !identity.serverOnly
                    && inventory != null && inventory.enabled && network != null && network.enabled
                    && Property(network, "sharedContainer").boolValue
                    && Property(network, "rows").intValue > 0 && Property(network, "columns").intValue > 0
                    && Property(network, "rows").intValue == Property(inventory, "rows").intValue
                    && Property(network, "columns").intValue == Property(inventory, "columns").intValue
                    && Reference<ItemManifestCatalog>(network, "catalog") == Reference<ItemManifestCatalog>(loadout, "catalog")
                    && Reference<ItemManifestCatalog>(inventory, "catalog") == Reference<ItemManifestCatalog>(loadout, "catalog")
                    && !Property(chest, "fillCatalogOnStart").boolValue && !InsidePresentation(chest, roots),
                    "Online chests require a shared network inventory, scene identity and no debug startup filling.");
            }
        }

        private static void ValidateLoadout(OnlineSpawnConfiguration loadout)
        {
            var catalog = Reference<ItemManifestCatalog>(loadout, "catalog");
            var player = Load<GameObject>(PlayerPath);
            Require(Reference<ItemManifestCatalog>(player.GetComponent<BackpackInventory>(), "catalog") == catalog
                && Reference<ItemManifestCatalog>(player.GetComponent<BackpackNetworkInventory>(), "catalog") == catalog,
                "OnlinePlayer's authored inventory catalogs must match the trusted scene loadout for client restoration.");
            var weapon = Reference<ItemManifest>(loadout, "initialWeapon");
            ValidateSeed(catalog, weapon, 1);
            Require(weapon.GetFragment<WeaponDefinitionFragment>() != null && weapon.GetFragment<EquipmentFragment>() != null,
                "Trusted initial weapon requires weapon/equipment fragments.");
            var equipment = weapon.GetFragment<EquipmentFragment>();
            Require(equipment.EquipPrefab != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(equipment.EquipPrefab)),
                "Trusted initial weapon requires an authored equipment model asset.");
            Require(string.IsNullOrEmpty(equipment.SocketAttachPoint)
                || player.GetComponentsInChildren<Transform>(true).Any(node => node.name == equipment.SocketAttachPoint),
                "Trusted initial weapon socket must exist in the OnlinePlayer hierarchy.");
            var seeds = Property(loadout, "inventoryItems");
            Require(seeds.isArray, "Trusted loadout inventory seeds must be serialized.");
            for (int i = 0; i < seeds.arraySize; ++i)
            {
                var seed = seeds.GetArrayElementAtIndex(i);
                var manifest = seed.FindPropertyRelative("Manifest")?.objectReferenceValue as ItemManifest;
                var count = seed.FindPropertyRelative("Count");
                Require(manifest != null && count != null, "A trusted inventory seed is incomplete.");
                ValidateSeed(catalog, manifest, count.intValue);
            }
        }

        private static void ValidateSeed(ItemManifestCatalog catalog, ItemManifest item, int count)
        {
            Require(item != null && catalog.TryGet(item.ItemType, out var registered) && registered == item
                && count > 0 && count <= (item.GetFragment<StackableFragment>()?.MaxStackSize ?? 1),
                "Trusted inventory seed is invalid or absent from its exact catalog.");
            try { item.ValidateConfiguration(); }
            catch (Exception) { throw new BuildFailedException("Trusted item configuration is invalid: " + AssetDatabase.GetAssetPath(item)); }
        }

        private static void ValidateSettings(SettingsView view)
        {
            foreach (string field in new[] { "textScaleButton", "hitFeedbackButton", "lowHealthPulseButton", "closeButton",
                "textScaleLabel", "hitFeedbackLabel", "lowHealthPulseLabel" })
                Require(Property(view, field).objectReferenceValue is Component component
                    && component.transform.IsChildOf(view.transform), "Settings prefab has a missing or external control: " + field);
            ValidateFocus(view);
        }

        private static void ValidateContainerView(BackpackGridUI window, BackpackContainerUI view)
        {
            Require(view.enabled && view.transform.IsChildOf(window.transform)
                && Property(view, "initialColumns").intValue > 0 && Property(view, "initialRows").intValue > 0,
                "Backpack inventory/equipment views must be enabled children with positive authored grid dimensions.");
            foreach (string field in new[] { "slotPrefab", "itemPrefab" })
            {
                var template = Reference<GameObject>(view, field);
                Require(!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(template)),
                    "Backpack view requires an authored reusable prefab asset: " + field);
            }
            foreach (string field in new[] { "slotLayer", "itemLayer" })
                Require(Reference<RectTransform>(view, field).IsChildOf(view.transform),
                    "Backpack view layers must belong to their container: " + field);
        }

        private static void ValidateFocus(FrontendWindow view)
        {
            var group = Reference<CanvasGroup>(view, "canvasGroup");
            Require(group.gameObject == view.gameObject, "Frontend window must own its CanvasGroup.");
            foreach (var selectable in ReferenceArray<Selectable>(view, "focusOrder"))
                Require(selectable.transform.IsChildOf(view.transform), "Window focus order must contain its own authored controls.");
        }

        private static bool InsidePresentation(Component component, GameObject[] roots) =>
            roots.Any(root => component.transform.IsChildOf(root.transform));
        private static T[] All<T>(Scene scene) where T : Component => scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true)).ToArray();
        private static T Single<T>(Scene scene) where T : Component
        {
            T[] values = All<T>(scene);
            Require(values.Length == 1, scene.path + " requires exactly one " + typeof(T).Name + ".");
            return values[0];
        }
        private static T Load<T>(string path) where T : Object
        {
            T value = AssetDatabase.LoadAssetAtPath<T>(path);
            Require(value != null, "Required entry asset is missing or has the wrong type: " + path);
            return value;
        }
        private static uint AuthoredAssetId(NetworkIdentity identity)
        {
            // Mirror's public assetId getter calls SetupIDs when the value is zero in the editor.
            // Read serialized state directly: validation must reject missing IDs, never generate them.
            long value = Property(identity, "_assetId").longValue;
            Require(value >= 0 && value <= uint.MaxValue, "Network prefab asset ID is outside the unsigned 32-bit range.");
            return (uint)value;
        }
        private static SerializedProperty Property(Object owner, string field)
        {
            Require(owner != null, "Cannot inspect missing component for " + field + ".");
            var property = new SerializedObject(owner).FindProperty(field);
            Require(property != null, owner.name + " is missing serialized field " + field + ".");
            return property;
        }
        private static T Reference<T>(Object owner, string field) where T : Object
        {
            var value = Property(owner, field).objectReferenceValue as T;
            Require(value != null, owner.name + "." + field + " must reference " + typeof(T).Name + ".");
            return value;
        }
        private static T[] ReferenceArray<T>(Object owner, string field, bool required = true) where T : Object
        {
            var property = Property(owner, field);
            Require(property.isArray && (!required || property.arraySize > 0), owner.name + "." + field + " requires an authored array.");
            var values = new T[property.arraySize];
            for (int i = 0; i < values.Length; ++i)
            {
                values[i] = property.GetArrayElementAtIndex(i).objectReferenceValue as T;
                Require(values[i] != null, owner.name + "." + field + " contains a missing or invalid reference.");
            }
            return values;
        }
        private static void RequireReferencesInScene(Object owner, Scene scene, params string[] fields)
        {
            foreach (string field in fields)
            {
                Object value = Property(owner, field).objectReferenceValue;
                GameObject go = value as GameObject ?? (value as Component)?.gameObject;
                Require(go != null && go.scene == scene, owner.name + "." + field + " must reference this scene's own object.");
            }
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new BuildFailedException(message); }
    }
}
