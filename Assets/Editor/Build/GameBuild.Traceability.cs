using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace RPG4.Editor.Build
{
    public static partial class GameBuild
    {
        private const string SingleBuildTraceKey = "RPG4.GameBuild.SingleTrace.v1";
        // A deliberately bounded source identity, not a claim to hash every art asset or external tool.
        // AuthorityContentCollector remains responsible for the authored gameplay projection.
        [Serializable]
        private sealed class BuildSource
        {
            public string scope = "source-packages-settings-v1: Assets/Packages cs,asmdef,asmref,rsp,dll,shader,compute,hlsl,cginc,glslinc and companion meta; package manifest/lock; entry manifest/catalog and meta; ProjectSettings except EditorBuildSettings.asset; authority assets use contentHash";
            public string unityVersion, sourceHash, packageManifestHash, packageLockHash, gitHead;
            public int sourceFileCount;
        }

        [Serializable]
        private sealed class CheckStage
        {
            public string name, status, startedUtc, finishedUtc, errorType;
        }

        [Serializable]
        private sealed class BuildTrace
        {
            public int schemaVersion = 1;
            public string id, batchId, kind, status = "running", startedUtc, finishedUtc, failureType;
            public string unityVersion = Application.unityVersion;
            public uint protocolVersion = EntryProtocol.Version;
            public string contentRule = EntryContentManifest.GenerationRule, contentHash;
            public BuildSource source;
            public List<CheckStage> stages = new List<CheckStage>();
            public List<string> attemptRecordIds = new List<string>();
        }

        private static string NewBuildId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        private static string BuildLogDirectory => Path.Combine(Path.GetDirectoryName(Application.dataPath), "服务器", "构建", "日志");
        private static string TracePath(BuildTrace trace) => Path.Combine(BuildLogDirectory, trace.id + ".json");

        private static BuildTrace CreateTrace(string id, string kind, string batchId = null)
        {
            var trace = new BuildTrace { id = id, batchId = batchId ?? id, kind = kind, startedUtc = DateTime.UtcNow.ToString("o") };
            SaveTrace(trace);
            return trace;
        }

        private static void SaveTrace(BuildTrace trace)
        {
            if (trace == null) return;
            Directory.CreateDirectory(BuildLogDirectory);
            // Only identifiers, digests and fixed stage names are serialized. No paths, config values,
            // exception messages, environment variables or private identity material enter this record.
            File.WriteAllText(TracePath(trace), JsonUtility.ToJson(trace, true), new UTF8Encoding(false, true));
            if ((trace.kind == "client" || trace.kind == "server") && trace.batchId == trace.id)
            {
                if (trace.status == "running") SessionState.SetString(SingleBuildTraceKey, JsonUtility.ToJson(trace));
                else SessionState.EraseString(SingleBuildTraceKey);
            }
            if (oneClick != null && (ReferenceEquals(oneClick.trace, trace) || ReferenceEquals(oneClick.currentTrace, trace)))
                SaveOneClickRun();
        }

        private static void RecoverInterruptedSingleBuild()
        {
            string saved = SessionState.GetString(SingleBuildTraceKey, "");
            if (string.IsNullOrEmpty(saved)) return;
            try
            {
                BuildTrace trace = JsonUtility.FromJson<BuildTrace>(saved);
                FinishTrace(trace, false, new BuildFailedException("Domain reload interrupted the build attempt."));
                if (trace != null) AppendLog(Path.Combine(BuildLogDirectory, trace.id + ".log"), "build_failed Domain reload interrupted the build attempt; no verified completion.");
            }
            catch (Exception) { Debug.LogError("Unable to record the interrupted single build; its output is unverified."); }
            finally { SessionState.EraseString(SingleBuildTraceKey); }
        }

        private static void StartTraceStage(BuildTrace trace, string name)
        {
            if (trace == null) return;
            CompleteTraceStage(trace, null);
            trace.stages.Add(new CheckStage { name = name, status = "running", startedUtc = DateTime.UtcNow.ToString("o") });
            SaveTrace(trace);
        }

        private static void CompleteTraceStage(BuildTrace trace, Exception error)
        {
            if (trace == null || trace.stages.Count == 0) return;
            CheckStage stage = trace.stages[trace.stages.Count - 1];
            if (stage.status != "running") return;
            stage.status = error == null ? "succeeded" : "failed";
            stage.errorType = error?.GetType().Name;
            stage.finishedUtc = DateTime.UtcNow.ToString("o");
            SaveTrace(trace);
        }

        private static void Check(BuildTrace trace, string name, Action action)
        {
            StartTraceStage(trace, name);
            try { action(); CompleteTraceStage(trace, null); }
            catch (Exception error) { CompleteTraceStage(trace, error); throw; }
        }

        private static void FinishTrace(BuildTrace trace, bool succeeded, Exception error = null)
        {
            if (trace == null) return;
            CompleteTraceStage(trace, succeeded ? null : error ?? new BuildFailedException("Build did not complete."));
            trace.status = succeeded ? "succeeded" : "failed";
            trace.failureType = succeeded ? null : error?.GetType().Name ?? nameof(BuildFailedException);
            trace.finishedUtc = DateTime.UtcNow.ToString("o");
            SaveTrace(trace);
        }

        private static BuildSource CaptureBuildSource()
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            var extensions = new HashSet<string>(new[] { ".cs", ".asmdef", ".asmref", ".rsp", ".dll", ".shader", ".compute", ".hlsl", ".cginc", ".glslinc" }, StringComparer.OrdinalIgnoreCase);
            var inputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (string folder in new[] { "Assets", "Packages" })
                foreach (string file in Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
                    if (extensions.Contains(Path.GetExtension(file)))
                    {
                        inputs.Add(file);
                        if (File.Exists(file + ".meta")) inputs.Add(file + ".meta");
                    }
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "ProjectSettings"), "*", SearchOption.AllDirectories))
                // OneClick owns the explicit scene arrays and intentionally changes this editor-only list.
                if (!string.Equals(Path.GetFileName(file), "EditorBuildSettings.asset", StringComparison.OrdinalIgnoreCase)) inputs.Add(file);
            inputs.Add(Path.Combine(root, "Packages", "manifest.json"));
            inputs.Add(Path.Combine(root, "Packages", "packages-lock.json"));
            foreach (string asset in new[] { ManifestPath, CatalogPath })
            {
                inputs.Add(Path.Combine(root, asset));
                inputs.Add(Path.Combine(root, asset + ".meta"));
            }
            var hashes = inputs.OrderBy(path => path, StringComparer.Ordinal).ToDictionary(path => path, FileHash, StringComparer.Ordinal);
            var source = new BuildSource
            {
                unityVersion = Application.unityVersion,
                packageManifestHash = hashes[Path.Combine(root, "Packages", "manifest.json")],
                packageLockHash = hashes[Path.Combine(root, "Packages", "packages-lock.json")],
                gitHead = ReadGitHead(root),
                sourceFileCount = inputs.Count
            };
            var canonical = new StringBuilder(source.scope).Append('\n').Append(source.unityVersion).Append('\n');
            foreach (string file in inputs.OrderBy(path => path, StringComparer.Ordinal))
                canonical.Append(file.Substring(root.Length + 1).Replace('\\', '/')).Append(':').Append(hashes[file]).Append('\n');
            using (var sha = SHA256.Create()) source.sourceHash = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString())));
            return source;
        }

        private static string FileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path)) return Hex(sha.ComputeHash(stream));
        }

        private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();

        private static string ReadGitHead(string root)
        {
            // Optional context only. The content-based sourceHash works in exported/dirty checkouts,
            // without Git installed, and never invokes status/diff or records repository URLs.
            try
            {
                using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git", Arguments = "rev-parse --verify HEAD", WorkingDirectory = root,
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (process == null) return null;
                    if (!process.WaitForExit(2000)) { process.Kill(); return null; }
                    if (process.ExitCode != 0) return null;
                    string head = process.StandardOutput.ReadToEnd().Trim().ToLowerInvariant();
                    return (head.Length == 40 || head.Length == 64) && head.All(character => character >= '0' && character <= '9'
                        || character >= 'a' && character <= 'f') ? head : null;
                }
            }
            catch (Exception) { return null; }
        }

        private static void RequireSameBuildSource(BuildSource expected)
        {
            if (expected == null || CaptureBuildSource().sourceHash != expected.sourceHash)
                throw new BuildFailedException("Source, package or project settings changed during the build. Restart the whole build batch.");
        }

        private static EntryContentManifest ValidateBuildInputs(bool server, BuildTrace trace, out ScriptingImplementation backend)
        {
            string role = server ? "server/" : "client/";
            ScriptingImplementation selectedBackend = default;
            Check(trace, role + "environment", () => selectedBackend = ValidateEnvironment(server));
            backend = selectedBackend;
            string[] scenes = server ? ServerScenes : ClientScenes;
            Check(trace, role + "entry_structure", () => { EntrySceneValidation.ValidateSceneOrder(scenes, server); EntrySceneValidation.Validate(); });
            EntryContentManifest manifest = null;
            Check(trace, role + "authority_manifest", () => manifest = RequireCurrentManifest(AuthorityContentCollector.Calculate()));
            trace.contentHash = manifest.ContentHash;
            SaveTrace(trace);
            Check(trace, role + "scene_assets_and_trust", () => ValidateAssets(scenes));
            Check(trace, role + "assembly_and_private_asset_boundary", ValidateDependencyBoundary);
            Check(trace, role + "deployment_identity_offline", DeploymentIdentityValidation.Validate);
            return manifest;
        }

        // For -executeMethod. Reuses the actual pre-build gates for both roles without generating the
        // manifest, switching targets, compiling or calling BuildPlayer. No BuildReport/output claim.
        [MenuItem("RPG4/Build/Validate Build Readiness (Both Roles)")]
        public static void ValidateBuildReadiness() => Prepare(() =>
        {
            BuildTrace trace = CreateTrace("validate-" + NewBuildId(), "validation_only");
            try
            {
                ValidateBuildInputs(false, trace, out _);
                ValidateBuildInputs(true, trace, out _);
                Check(trace, "source_snapshot", () => trace.source = CaptureBuildSource());
                FinishTrace(trace, true);
                Debug.Log("Both-role pre-build validation succeeded. BuildReport, output files and runtime behavior were not checked. Record: " + TracePath(trace));
            }
            catch (Exception error) { FinishTrace(trace, false, error); throw; }
        });
    }
}
