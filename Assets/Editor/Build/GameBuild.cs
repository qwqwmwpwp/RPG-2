using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mirror;
using Mirror.Transports.Encryption;
using RPG4.Frontend;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using RPG4.Networking.Transports;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using kcp2k;

namespace RPG4.Editor.Build
{
    /// <summary>Explicit preparation/build actions. Only ApplyClientBuildSettings changes the default scene list;
    /// only GenerateContentManifest updates the shared generated digest. No action fabricates server trust.</summary>
    public static class GameBuild
    {
        private const string RequiredUnity = "2022.3.62f3";
        private const string ManifestPath = "Assets/Settings/EntryContentManifest.asset";
        private const string CatalogPath = "Assets/Settings/ClientServerCatalog.asset";
        private const string ManagerPath = "Assets/player/OnlineNetworkManager.prefab";
        private static readonly string[] ClientScenes = { "Assets/Scenes/ClientBootstrap.unity", EntryContentManifest.MainMenuScenePath, EntryProtocol.GameplayScene };
        private static readonly string[] ServerScenes = { "Assets/Scenes/ServerBootstrap.unity", EntryProtocol.GameplayScene };
        private static readonly string[] DatabasePlugins = {
            "MySqlConnector.dll", "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
            "Microsoft.Extensions.Logging.Abstractions.dll", "System.Diagnostics.DiagnosticSource.dll",
            "System.Runtime.CompilerServices.Unsafe.dll" };
        [Serializable]
        private sealed class AssemblyBoundary
        {
            public string name;
            public string[] references, precompiledReferences, defineConstraints;
            public bool overrideReferences;
        }

        [MenuItem("RPG4/Build/Windows Client")]
        public static void BuildClientWindows() => BuildWindows(false);
        [MenuItem("RPG4/Build/Windows Dedicated Server")]
        public static void BuildServerWindows() => BuildWindows(true);

        [MenuItem("RPG4/Build/Generate Authority Content Manifest")]
        public static void GenerateContentManifest() => Prepare(() =>
        {
            EntrySceneValidation.Validate();
            ValidateDependencyBoundary();
            AuthoritySnapshot snapshot = AuthorityContentCollector.Calculate();
            EntryContentManifest manifest = AssetDatabase.LoadAssetAtPath<EntryContentManifest>(ManifestPath);
            if (manifest == null) throw new BuildFailedException("Shared entry manifest asset is unavailable.");
            using (var serialized = new SerializedObject(manifest))
            {
                serialized.FindProperty("contentHash").stringValue = snapshot.Hash;
                serialized.FindProperty("generationRule").stringValue = EntryContentManifest.GenerationRule;
                serialized.FindProperty("protocolVersion").longValue = EntryProtocol.Version;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            AssetDatabase.SaveAssetIfDirty(manifest);
            WriteContentArtifact(snapshot);
            Debug.Log("Generated " + EntryContentManifest.GenerationRule + " protocol=" + EntryProtocol.Version
                + " content=" + snapshot.Hash + " records=" + snapshot.RecordCount + ".");
        });

        [MenuItem("RPG4/Build/Validate Entry Configuration")]
        public static void ValidateEntryConfiguration() => Prepare(() =>
        {
            EntrySceneValidation.Validate();
            ValidateDependencyBoundary();
            AuthoritySnapshot snapshot = AuthorityContentCollector.Calculate();
            RequireCurrentManifest(snapshot);
            Debug.Log("Entry structure and shared authority digest are valid: protocol=" + EntryProtocol.Version
                + " content=" + snapshot.Hash + ". Deployment identity is checked separately before building.");
        });

        [MenuItem("RPG4/Build/Apply Client Build Settings")]
        public static void ApplyClientBuildSettings() => Prepare(() =>
        {
            EntrySceneValidation.Validate();
            EntrySceneValidation.ValidateSceneOrder(ClientScenes, false);
            EditorBuildSettings.scenes = ClientScenes.Select(path => new EditorBuildSettingsScene(path, true)).ToArray();
            Debug.Log("Default build scenes set to ClientBootstrap, MainMenu, OnlineGame. Dedicated Server uses its explicit entry list.");
        });

        [MenuItem("RPG4/Build/Validate Deployment Identity")]
        public static void ValidateDeploymentIdentity() => Prepare(DeploymentIdentityValidation.Validate);

        private static void Prepare(Action action)
        {
            try { ValidateEditorIdle(); action(); }
            catch (Exception exception)
            {
                string reason = exception is BuildFailedException ? exception.Message : "Entry preparation failed.";
                Debug.LogError(reason);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw new BuildFailedException(reason);
            }
        }

        private static EntryContentManifest RequireCurrentManifest(AuthoritySnapshot snapshot)
        {
            EntryContentManifest manifest = AssetDatabase.LoadAssetAtPath<EntryContentManifest>(ManifestPath);
            if (manifest == null || !manifest.IsValid || manifest.ContentHash != snapshot.Hash)
                throw new BuildFailedException("Authority content changed or its digest is unprepared. Generate the shared content manifest first.");
            return manifest;
        }
        private static void WriteContentArtifact(AuthoritySnapshot snapshot)
        {
            string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "服务器", "构建", "内容");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, EntryContentManifest.GenerationRule + ".json"), snapshot.Json, new UTF8Encoding(false, true));
        }

        private static void BuildWindows(bool server)
        {
            string log = null;
            try
            {
                string root = Path.GetDirectoryName(Application.dataPath);
                string outputRoot = Path.Combine(root, "服务器", "构建");
                string attempt = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
                string logDirectory = Path.Combine(outputRoot, "日志");
                Directory.CreateDirectory(logDirectory);
                log = Path.Combine(logDirectory, (server ? "server-" : "client-") + attempt + ".log");
                AppendLog(log, "validation_started");
                string[] scenes = server ? ServerScenes : ClientScenes;
                ScriptingImplementation backend = ValidateEnvironment(server);
                EntrySceneValidation.ValidateSceneOrder(scenes, server);
                EntrySceneValidation.Validate();
                AuthoritySnapshot snapshot = AuthorityContentCollector.Calculate();
                RequireCurrentManifest(snapshot);
                EntryContentManifest manifest = ValidateAssets(scenes);
                ValidateDependencyBoundary();
                DeploymentIdentityValidation.Validate();
                // Build into a new, empty attempt directory. Existing builds are never deleted, overlaid or
                // consulted as evidence that this attempt produced its required executable/runtime files.
                string outputDirectory = Path.Combine(outputRoot, server ? "ServerWindows" : "ClientWindows", attempt);
                if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
                    throw new BuildFailedException("Build attempt output already exists; existing output will not be overwritten.");
                Directory.CreateDirectory(outputDirectory);
                if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                    throw new BuildFailedException("Build attempt output must be empty.");
                string destination = Path.Combine(outputDirectory, server ? "RPG4Server.exe" : "RPG4Client.exe");
                AppendLog(log, "build_started protocol=" + EntryProtocol.Version + " content=" + manifest.ContentHash
                    + " output=" + destination);
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = destination,
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    subtarget = (int)(server ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player),
                    options = BuildOptions.None
                });
                ValidateBuildOutput(report, destination, server, backend);
                AppendLog(log, "build_succeeded output=" + destination);
                Debug.Log("Windows " + (server ? "Dedicated Server" : "Client") + " build succeeded: " + destination);
            }
            catch (Exception exception)
            {
                string reason = exception is BuildFailedException ? exception.Message : "Build setup or execution failed.";
                if (log != null) { try { AppendLog(log, "build_failed " + reason); } catch (Exception) { } }
                Debug.LogError(reason);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw new BuildFailedException(reason);
            }
        }

        private static ScriptingImplementation ValidateEnvironment(bool server)
        {
            ValidateEditorIdle();
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
                throw new BuildFailedException("Windows Build Support is unavailable.");
            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(server ? NamedBuildTarget.Server : NamedBuildTarget.Standalone);
            if (backend != ScriptingImplementation.Mono2x && backend != ScriptingImplementation.IL2CPP)
                throw new BuildFailedException("Unsupported Windows scripting backend.");
            string variations = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "windowsstandalonesupport", "Variations");
            // Unity 2022.3 DesktopStandalonePostProcessor selects platform_role_configuration_backend.
            // BuildOptions.None needs nondevelopment; a development-only module cannot satisfy this build.
            string variation = "win64_" + (server ? "server" : "player") + "_nondevelopment_"
                + (backend == ScriptingImplementation.IL2CPP ? "il2cpp" : "mono");
            if (!Directory.Exists(Path.Combine(variations, variation)))
                throw new BuildFailedException("Required Windows Build Support variation is unavailable: " + variation + ".");
            return backend;
        }

        private static void ValidateBuildOutput(BuildReport report, string destination, bool server, ScriptingImplementation backend)
        {
            StandaloneBuildSubtarget subtarget = server ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player;
            if (report == null || report.summary.result != BuildResult.Succeeded || report.summary.totalErrors != 0)
                throw new BuildFailedException("Windows build did not succeed. See the Unity build errors.");
            if (report.summary.platform != BuildTarget.StandaloneWindows64
                || report.summary.GetSubtarget<StandaloneBuildSubtarget>() != subtarget
                || !SamePath(report.summary.outputPath, destination))
                throw new BuildFailedException("Build report target, subtarget or output does not match this build attempt.");

            BuildFile[] files = report.GetFiles();
            if (files == null || files.Length == 0)
                throw new BuildFailedException("Build report contains no output files.");
            var reported = new HashSet<string>(files.Where(file => file.size > 0 && !string.IsNullOrWhiteSpace(file.path))
                .Select(file => Path.GetFullPath(file.path)), StringComparer.OrdinalIgnoreCase);
            string directory = Path.GetDirectoryName(destination);
            string data = Path.Combine(directory, Path.GetFileNameWithoutExtension(destination) + "_Data");
            RequireReportedFile(destination, reported);
            RequireReportedFile(Path.Combine(directory, "UnityPlayer.dll"), reported);
            if (!Directory.Exists(data) || !reported.Any(path => IsBelowDirectory(path, data) && IsNonemptyFile(path)))
                throw new BuildFailedException("Current Windows build is missing its data files.");
            if (backend == ScriptingImplementation.IL2CPP)
            {
                RequireReportedFile(Path.Combine(directory, "GameAssembly.dll"), reported);
                RequireReportedFile(Path.Combine(data, "il2cpp_data", "Metadata", "global-metadata.dat"), reported);
            }
            else
            {
                string managed = Path.Combine(data, "Managed");
                if (!Directory.Exists(managed) || !reported.Any(path => IsBelowDirectory(path, managed)
                    && string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase) && IsNonemptyFile(path)))
                    throw new BuildFailedException("Current Mono build is missing its managed assemblies.");
                RequireReportedFile(Path.Combine(directory, "MonoBleedingEdge", "EmbedRuntime", "mono-2.0-bdwgc.dll"), reported);
            }
            if (!server && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(path =>
                string.Equals(Path.GetFileName(path), "RPG4.Server.dll", StringComparison.OrdinalIgnoreCase)
                || DatabasePlugins.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
                throw new BuildFailedException("Client build output unexpectedly contains server database implementation.");
        }

        private static void RequireReportedFile(string path, HashSet<string> reported)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || !reported.Contains(info.FullName))
                throw new BuildFailedException("Current build is missing a required nonempty reported file: " + Path.GetFileName(path));
        }

        private static bool SamePath(string first, string second) => !string.IsNullOrWhiteSpace(first)
            && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        private static bool IsNonemptyFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
        private static bool IsBelowDirectory(string path, string directory) => Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static void ValidateEditorIdle()
        {
            if (!string.Equals(Application.unityVersion, RequiredUnity, StringComparison.Ordinal))
                throw new BuildFailedException("Build requires Unity " + RequiredUnity + ".");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new BuildFailedException("Wait until the editor is idle before building.");
        }

        private static EntryContentManifest ValidateAssets(string[] scenes)
        {
            foreach (string path in scenes)
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                    throw new BuildFailedException("A required entry or gameplay scene is unavailable: " + path);
            EntryContentManifest manifest = AssetDatabase.LoadAssetAtPath<EntryContentManifest>(ManifestPath);
            ClientServerCatalog catalog = AssetDatabase.LoadAssetAtPath<ClientServerCatalog>(CatalogPath);
            if (manifest == null || !manifest.IsValid)
                throw new BuildFailedException("S3 must generate the authoritative content digest before building.");
            if (catalog == null || catalog.Count < 1 || catalog.ContentHash != manifest.ContentHash)
                throw new BuildFailedException("Trusted server catalog and shared content manifest must be configured together.");
            for (int i = 0; i < catalog.Count; ++i)
            {
                TrustedClientServer server = catalog.Get(i);
                if (server == null || !catalog.Validate(i, server.Address, server.Port, out _))
                    throw new BuildFailedException("Trusted endpoint or public key fingerprint is missing or invalid.");
            }
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPath);
            AuthoritativeNetworkManager manager = prefab != null ? prefab.GetComponent<AuthoritativeNetworkManager>() : null;
            if (manager == null || !manager.UsesGameplaySceneBindings || !(manager.authenticator is AccountAuthenticator)
                || !(manager.transport is EncryptionTransport encrypted)
                || !(encrypted.Inner is ConnectionAdmissionTransport admission) || !(admission.Inner is KcpTransport)
                || manager.headlessStartMode != HeadlessStartOptions.DoNothing)
                throw new BuildFailedException("The shared online network prefab must contain the authenticated encrypted admission chain.");
            string[] dependencies = AssetDatabase.GetDependencies(scenes, true);
            if (!dependencies.Contains(ManagerPath) || !dependencies.Contains(ManifestPath)
                || dependencies.Contains("Assets/Scenes/Main.unity"))
                throw new BuildFailedException("Entry scene references must use the shared online prefab/manifest and exclude Main.");
            return manifest;
        }

        private static void ValidateDependencyBoundary()
        {
            ValidatePrivateAssetBoundary();
            AssemblyBoundary server = ReadBoundary("Assets/Scripts/Server/RPG4.Server.asmdef");
            string serverGuid = "GUID:" + AssetDatabase.AssetPathToGUID("Assets/Scripts/Server/RPG4.Server.asmdef");
            if (!server.overrideReferences || server.defineConstraints == null || server.defineConstraints.Length != 1
                || server.defineConstraints[0] != "UNITY_EDITOR || UNITY_SERVER")
                throw new BuildFailedException("Server code must compile only in Editor or Dedicated Server.");
            foreach (string dll in DatabasePlugins)
            {
                string path = "Assets/Plugins/Server/" + dll;
                if (!File.Exists(path) || !File.Exists(path + ".meta") || server.precompiledReferences == null
                    || !server.precompiledReferences.Contains(dll, StringComparer.OrdinalIgnoreCase))
                    throw new BuildFailedException("A pinned server database dependency is unavailable.");
                var importer = AssetImporter.GetAtPath(path) as PluginImporter;
                string[] constraints = importer != null ? importer.DefineConstraints : null;
                // Unity 2022.3 exposes DefineConstraints publicly, but IsExplicitlyReferenced is internal.
                // Read its exact serialized flag instead of relying on a misleading substring/comment match.
                bool explicitlyReferenced = File.ReadLines(path + ".meta").Any(line => line.Trim() == "isExplicitlyReferenced: 1");
                if (constraints == null || constraints.Length != 1 || constraints[0] != "UNITY_EDITOR || UNITY_SERVER"
                    || !explicitlyReferenced)
                    throw new BuildFailedException("Database plugins must be explicit references restricted to Editor or Dedicated Server.");
            }
            foreach (string path in new[] { "Assets/Scripts/Networking/RPG4.Networking.asmdef",
                "Assets/Scripts/Networking/Contracts/RPG4.Networking.Contracts.asmdef", "Assets/Scripts/Frontend/RPG4.Frontend.asmdef" })
            {
                AssemblyBoundary shared = ReadBoundary(path);
                if ((shared.references ?? Array.Empty<string>()).Any(reference =>
                    string.Equals(reference, server.name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(reference, serverGuid, StringComparison.OrdinalIgnoreCase))
                    || (shared.precompiledReferences ?? Array.Empty<string>()).Any(reference =>
                        DatabasePlugins.Contains(reference, StringComparer.OrdinalIgnoreCase)))
                    throw new BuildFailedException("Client/shared assemblies cannot reference server database implementation.");
            }
        }

        private static void ValidatePrivateAssetBoundary()
        {
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || Directory.Exists(path)) continue;
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".key" || extension == ".pem" || extension == ".pfx" || extension == ".p12")
                    throw new BuildFailedException("Private identity/certificate files must remain outside Assets.");
                if (extension == ".json" && (Path.GetFileName(path) == "server.json"
                    || File.ReadAllText(path).IndexOf("\"PrivateKey\"", StringComparison.OrdinalIgnoreCase) >= 0))
                    throw new BuildFailedException("Deployment configuration or private key JSON must remain outside Assets.");
            }
        }

        private static AssemblyBoundary ReadBoundary(string path)
        {
            if (!File.Exists(path)) throw new BuildFailedException("A required assembly definition is unavailable.");
            return JsonUtility.FromJson<AssemblyBoundary>(File.ReadAllText(path))
                ?? throw new BuildFailedException("An assembly definition is invalid.");
        }
        private static void AppendLog(string path, string message) =>
            File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
    }
}
