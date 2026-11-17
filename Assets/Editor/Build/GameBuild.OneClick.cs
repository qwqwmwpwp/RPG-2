using System;
using System.Linq;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG4.Editor.Build
{
    public static partial class GameBuild
    {
        private const string OneClickKey = "RPG4.GameBuild.OneClick.v1";
        private const double WaitTimeoutSeconds = 1200;
        private const string BuildBothMenu = "RPG4/Build/一键构建客户端与服务器";
        private const string PrepareMenu = "RPG4/Build/一键准备与校验";
        private const string CancelMenu = "RPG4/Build/取消一键流程";

        private enum OneClickStage
        {
            WaitingForEditor, ConfigureClient, WaitingForClient, PrepareSharedContent,
            BuildClient, ConfigureServer, WaitingForServer, BuildServer,
            RestoreSettings, WaitingForRestore, Complete
        }

        [Serializable]
        private sealed class BuildAttempt
        {
            public string destination, log, hash;
            public string recordId, batchId;
        }

        // SessionState survives script/domain reload, but never resumes a build across an Editor restart.
        [Serializable]
        private sealed class OneClickRun
        {
            public bool buildBoth, buildInProgress, compilationStarted, compilationFinished, compilationFailed;
            public OneClickStage stage;
            public int previousTarget, previousSubtarget;
            public long waitStartedUtcTicks;
            public string hash, currentLog, failure;
            public BuildAttempt client, server;
            public BuildTrace trace, currentTrace;
        }

        private static OneClickRun oneClick;
        private static bool oneClickTickRunning;

        [InitializeOnLoadMethod]
        private static void InitializeOneClickBuild()
        {
            RecoverInterruptedSingleBuild();
            string saved = SessionState.GetString(OneClickKey, "");
            if (!string.IsNullOrEmpty(saved)) oneClick = JsonUtility.FromJson<OneClickRun>(saved);
            EditorApplication.update -= TickOneClickBuild;
            EditorApplication.update += TickOneClickBuild;
            CompilationPipeline.compilationStarted -= OneClickCompilationStarted;
            CompilationPipeline.compilationStarted += OneClickCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OneClickAssemblyFinished;
            CompilationPipeline.assemblyCompilationFinished += OneClickAssemblyFinished;
            CompilationPipeline.compilationFinished -= OneClickCompilationFinished;
            CompilationPipeline.compilationFinished += OneClickCompilationFinished;
            if (oneClick != null && oneClick.buildInProgress)
            {
                // BuildPlayer is synchronous. A reload that unwound its caller has no verified report;
                // never repeat it, even if a partially written executable happens to exist.
                oneClick.buildInProgress = false;
                oneClick.failure = "构建期间发生脚本重载，未取得本次构建报告；流程已中止，已有输出保留。";
                var error = new BuildFailedException("Domain reload interrupted the build attempt.");
                try { FinishTrace(oneClick.currentTrace, false, error); CompleteTraceStage(oneClick.trace, error); }
                catch (Exception) { Debug.LogError("Unable to record the interrupted build attempt."); }
                InvalidateOneClickAttemptLogs();
                SetOneClickStage(OneClickStage.RestoreSettings);
            }
            else if (oneClick != null && oneClick.trace == null)
            {
                // An older Editor session has no pinned source identity. Never continue its outputs.
                oneClick.failure = "旧的一键流程缺少构建来源记录，请重新开始。";
                SetOneClickStage(OneClickStage.RestoreSettings);
            }
        }

        [MenuItem(BuildBothMenu)]
        public static void BuildClientAndServerOneClick() => StartOneClickBuild(true);

        [MenuItem(PrepareMenu)]
        public static void PrepareAndValidateOneClick() => StartOneClickBuild(false);

        [MenuItem(BuildBothMenu, true)]
        [MenuItem(PrepareMenu, true)]
        private static bool CanStartOneClickBuild() => oneClick == null && !BuildPipeline.isBuildingPlayer
            && !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem("RPG4/Build/Windows Client", true)]
        [MenuItem("RPG4/Build/Windows Dedicated Server", true)]
        [MenuItem("RPG4/Build/Generate Authority Content Manifest", true)]
        [MenuItem("RPG4/Build/Validate Entry Configuration", true)]
        [MenuItem("RPG4/Build/Apply Client Build Settings", true)]
        [MenuItem("RPG4/Build/Validate Deployment Identity", true)]
        [MenuItem("RPG4/Build/Validate Build Readiness (Both Roles)", true)]
        private static bool CanRunIndividualBuildAction() => oneClick == null;

        [MenuItem(CancelMenu)]
        public static void CancelOneClickBuild()
        {
            if (oneClick == null) return;
            FailOneClickBuild(new OperationCanceledException("用户取消了一键流程。"));
        }

        [MenuItem(CancelMenu, true)]
        private static bool CanCancelOneClickBuild() => oneClick != null && !oneClick.buildInProgress
            && oneClick.stage < OneClickStage.RestoreSettings;

        private static void StartOneClickBuild(bool buildBoth)
        {
            if (!CanStartOneClickBuild())
                throw new BuildFailedException("已有一键流程、构建或播放正在运行，请先结束当前操作。");
            if (Application.unityVersion != RequiredUnity)
                throw new BuildFailedException("Build requires Unity " + RequiredUnity + ".");
            oneClick = new OneClickRun
            {
                buildBoth = buildBoth,
                previousTarget = (int)EditorUserBuildSettings.activeBuildTarget,
                previousSubtarget = (int)EditorUserBuildSettings.standaloneBuildSubtarget,
                trace = CreateTrace("batch-" + NewBuildId(), buildBoth ? "client_and_server" : "prepare_and_validate")
            };
            SetOneClickStage(OneClickStage.WaitingForEditor);
            Debug.Log("一键流程开始：" + (buildBoth ? "构建客户端与服务器" : "准备与校验")
                + "。客户端场景列表将保留；结束时恢复原构建目标和 Standalone 子目标。"
                + "可通过 RPG4/Build/取消一键流程 停止等待。");
        }

        private static void TickOneClickBuild()
        {
            if (oneClick == null || oneClickTickRunning) return;
            oneClickTickRunning = true;
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new BuildFailedException("进入播放模式，一键流程已中止。");
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
                {
                    RequireOneClickWaitWithinLimit();
                    return;
                }
                switch (oneClick.stage)
                {
                    case OneClickStage.WaitingForEditor:
                        RequireOneClickCompilationHealthy();
                        ConfirmRelevantUnsavedChanges();
                        SetOneClickStage(OneClickStage.ConfigureClient);
                        break;
                    case OneClickStage.ConfigureClient:
                        EntrySceneValidation.Validate();
                        EntrySceneValidation.ValidateSceneOrder(ClientScenes, false);
                        EditorBuildSettings.scenes = ClientScenes.Select(path => new EditorBuildSettingsScene(path, true)).ToArray();
                        SwitchOneClickTarget(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player,
                            OneClickStage.WaitingForClient);
                        break;
                    case OneClickStage.WaitingForClient:
                        if (OneClickCompilationReady()) SetOneClickStage(OneClickStage.PrepareSharedContent);
                        break;
                    case OneClickStage.PrepareSharedContent:
                        RequireOneClickTarget(false);
                        GenerateContentManifest();
                        oneClick.hash = AuthorityContentCollector.Calculate().Hash;
                        RequirePinnedOneClickManifest(oneClick.hash);
                        ValidateEntryConfiguration();
                        ValidateAssets(ClientScenes);
                        ValidateAssets(ServerScenes);
                        oneClick.trace.contentHash = oneClick.hash;
                        oneClick.trace.source = CaptureBuildSource();
                        SaveTrace(oneClick.trace);
                        SaveOneClickRun();
                        Debug.Log("一键流程共享摘要已固定：protocol=" + EntryProtocol.Version + " content=" + oneClick.hash);
                        SetOneClickStage(oneClick.buildBoth ? OneClickStage.BuildClient : OneClickStage.RestoreSettings);
                        break;
                    case OneClickStage.BuildClient:
                        RunOneClickPlayerBuild(false);
                        SetOneClickStage(OneClickStage.ConfigureServer);
                        break;
                    case OneClickStage.ConfigureServer:
                        RequirePinnedOneClickManifest(oneClick.hash);
                        RequireSameBuildSource(oneClick.trace.source);
                        SwitchOneClickTarget(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Server,
                            OneClickStage.WaitingForServer);
                        break;
                    case OneClickStage.WaitingForServer:
                        if (OneClickCompilationReady()) SetOneClickStage(OneClickStage.BuildServer);
                        break;
                    case OneClickStage.BuildServer:
                        RunOneClickPlayerBuild(true);
                        SetOneClickStage(OneClickStage.RestoreSettings);
                        break;
                    case OneClickStage.RestoreSettings:
                        if (SettingsAlreadyRestored()) SetOneClickStage(OneClickStage.Complete);
                        else SwitchOneClickTarget((BuildTarget)oneClick.previousTarget,
                            (StandaloneBuildSubtarget)oneClick.previousSubtarget, OneClickStage.WaitingForRestore);
                        break;
                    case OneClickStage.WaitingForRestore:
                        if (OneClickCompilationReady())
                        {
                            if (!SettingsAlreadyRestored()) throw new BuildFailedException("原构建目标/子目标恢复失败。");
                            SetOneClickStage(OneClickStage.Complete);
                        }
                        break;
                    case OneClickStage.Complete:
                        if (string.IsNullOrEmpty(oneClick.failure))
                        {
                            RequirePinnedOneClickManifest(oneClick.hash);
                            RequireSameBuildSource(oneClick.trace.source);
                        }
                        FinishOneClickBuild();
                        break;
                }
            }
            catch (Exception exception) { FailOneClickBuild(exception); }
            finally { oneClickTickRunning = false; }
        }

        private static void RunOneClickPlayerBuild(bool server)
        {
            RequireOneClickCompilationHealthy();
            RequireOneClickTarget(server);
            RequirePinnedOneClickManifest(oneClick.hash);
            oneClick.buildInProgress = true;
            oneClick.currentLog = null;
            oneClick.currentTrace = null;
            SaveOneClickRun(); // Persist before BuildPlayer, so a reload can never launch this step twice.
            BuildAttempt result = BuildWindowsAttempt(server, oneClick.hash, false);
            if (server) oneClick.server = result;
            else oneClick.client = result;
            oneClick.buildInProgress = false;
            SaveOneClickRun();
        }

        private static void SwitchOneClickTarget(BuildTarget target, StandaloneBuildSubtarget subtarget,
            OneClickStage waitStage)
        {
            bool settingsChanged = EditorUserBuildSettings.activeBuildTarget != target
                || EditorUserBuildSettings.standaloneBuildSubtarget != subtarget;
            oneClick.compilationStarted = false;
            oneClick.compilationFinished = false;
            oneClick.compilationFailed = false;
            SetOneClickStage(waitStage); // Target changes may request a domain reload.
            if (!settingsChanged)
            {
                // The caller already observed an idle Editor. A no-op compilation request may emit no
                // compilation events at all, so do not wait for an event that Unity need not send.
                oneClick.compilationStarted = true;
                oneClick.compilationFinished = true;
                SaveOneClickRun();
                return;
            }
            EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;
            if (EditorUserBuildSettings.activeBuildTarget != target
                && !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(target), target))
                throw new BuildFailedException("无法切换构建目标：" + target + ".");
            CompilationPipeline.RequestScriptCompilation();
        }

        private static bool OneClickCompilationReady()
        {
            RequireOneClickWaitWithinLimit();
            if (!oneClick.compilationStarted || !oneClick.compilationFinished) return false;
            RequireOneClickCompilationHealthy();
            return true;
        }

        private static void RequireOneClickCompilationHealthy()
        {
            if (oneClick.compilationFailed || EditorUtility.scriptCompilationFailed)
                throw new BuildFailedException("脚本编译失败，请查看 Unity Console 中的编译错误。");
        }

        private static void RequireOneClickWaitWithinLimit()
        {
            if ((DateTime.UtcNow.Ticks - oneClick.waitStartedUtcTicks) / (double)TimeSpan.TicksPerSecond > WaitTimeoutSeconds)
                throw new BuildFailedException("等待编译/资源导入超时；流程已停止，请查看 Unity Console。");
        }

        private static void RequireOneClickTarget(bool server)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64
                || EditorUserBuildSettings.standaloneBuildSubtarget != (server ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player))
                throw new BuildFailedException("构建目标在一键流程中被修改，请重新开始。");
        }

        private static bool SettingsAlreadyRestored() => EditorUserBuildSettings.activeBuildTarget == (BuildTarget)oneClick.previousTarget
            && EditorUserBuildSettings.standaloneBuildSubtarget == (StandaloneBuildSubtarget)oneClick.previousSubtarget;

        private static void RequirePinnedOneClickManifest(string hash)
        {
            AuthoritySnapshot snapshot = AuthorityContentCollector.Calculate();
            EntryContentManifest manifest = RequireCurrentManifest(snapshot);
            if (string.IsNullOrEmpty(hash) || snapshot.Hash != hash || manifest.ContentHash != hash)
                throw new BuildFailedException("一键流程期间权威内容已改变；本轮停止，请重新开始以生成一份两端共享摘要。");
        }

        private static void ConfirmRelevantUnsavedChanges()
        {
            string[] paths = ClientScenes.Concat(ServerScenes).Distinct().ToArray();
            string[] dependencies = AssetDatabase.GetDependencies(paths, true);
            PrefabStage prefab = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefab != null && prefab.scene.isDirty && dependencies.Contains(prefab.assetPath))
                throw new BuildFailedException("构建依赖的 Prefab 有未保存编辑，请先保存或关闭该编辑：" + prefab.assetPath);
            Scene[] dirtyScenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Where(scene => scene.isDirty && paths.Contains(scene.path)).ToArray();
            if (dirtyScenes.Length != 0 && !EditorSceneManager.SaveModifiedScenesIfUserWantsTo(dirtyScenes))
                throw new OperationCanceledException("用户取消了构建场景的保存选择；一键流程已停止。");
        }

        private static void OneClickCompilationStarted(object context)
        {
            if (oneClick == null) return;
            oneClick.compilationStarted = true;
            oneClick.compilationFinished = false;
            SaveOneClickRun();
        }

        private static void OneClickAssemblyFinished(string assembly, CompilerMessage[] messages)
        {
            if (oneClick == null || !messages.Any(message => message.type == CompilerMessageType.Error)) return;
            oneClick.compilationFailed = true;
            SaveOneClickRun();
        }

        private static void OneClickCompilationFinished(object context)
        {
            if (oneClick == null) return;
            oneClick.compilationFinished = true;
            SaveOneClickRun();
        }

        private static void RecordOneClickBuildLog(string path)
        {
            oneClick.currentLog = path;
            SaveOneClickRun();
        }

        private static void SetOneClickStage(OneClickStage stage)
        {
            oneClick.stage = stage;
            oneClick.waitStartedUtcTicks = DateTime.UtcNow.Ticks;
            StartTraceStage(oneClick.trace, stage.ToString());
            SaveOneClickRun();
            Debug.Log("一键流程阶段：" + OneClickStageLabel(stage));
        }

        private static string OneClickStageLabel(OneClickStage stage)
        {
            switch (stage)
            {
                case OneClickStage.WaitingForEditor: return "等待编辑器编译和导入完成";
                case OneClickStage.ConfigureClient: return "检查入口并设置客户端配置";
                case OneClickStage.WaitingForClient: return "等待客户端配置编译完成";
                case OneClickStage.PrepareSharedContent: return "生成一次共享清单并校验";
                case OneClickStage.BuildClient: return "构建并校验客户端";
                case OneClickStage.ConfigureServer: return "切换服务器配置";
                case OneClickStage.WaitingForServer: return "等待服务器配置编译完成";
                case OneClickStage.BuildServer: return "构建并校验服务器";
                case OneClickStage.RestoreSettings: return "恢复原构建目标和子目标";
                case OneClickStage.WaitingForRestore: return "等待原配置恢复编译完成";
                default: return "汇总结果";
            }
        }

        private static void SaveOneClickRun() => SessionState.SetString(OneClickKey, JsonUtility.ToJson(oneClick));

        private static void FailOneClickBuild(Exception exception)
        {
            string message = "阶段：" + OneClickStageLabel(oneClick.stage) + "；原因：" + exception.Message;
            Debug.LogError("一键流程停止。" + message + "\n当前构建日志：" + (oneClick.currentLog ?? "尚未启动构建"));
            Debug.LogException(exception);
            try
            {
                CompleteTraceStage(oneClick.trace, exception);
                if (oneClick.buildInProgress) FinishTrace(oneClick.currentTrace, false, exception);
            }
            catch (Exception) { Debug.LogError("Unable to record failed build trace; continuing settings restoration."); }
            InvalidateOneClickAttemptLogs();
            oneClick.buildInProgress = false;
            if (string.IsNullOrEmpty(oneClick.failure)) oneClick.failure = message;
            else oneClick.failure += "\n" + message;
            // A failed restore must terminate too, rather than repeatedly switching/compiling forever.
            if (oneClick.stage >= OneClickStage.RestoreSettings || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (!SettingsAlreadyRestored()) oneClick.failure += "\n原构建设置尚未恢复，请退出播放/修复编译后手动恢复。";
                FinishOneClickBuild();
            }
            else SetOneClickStage(OneClickStage.RestoreSettings);
        }

        private static void InvalidateOneClickAttemptLogs()
        {
            // Existing launch tooling consumes the legacy .log, so a failed shared-source batch must
            // also invalidate earlier successful attempts there. Keep all output files for diagnosis.
            foreach (string log in new[] { oneClick.client?.log, oneClick.server?.log, oneClick.currentLog }
                .Where(path => !string.IsNullOrEmpty(path)).Distinct())
            {
                try { AppendLog(log, "build_failed Shared-source build batch did not complete successfully."); }
                catch (Exception) { Debug.LogError("Unable to record failed batch in an attempt log."); }
            }
        }

        private static void FinishOneClickBuild()
        {
            string summary = "一键流程" + (string.IsNullOrEmpty(oneClick.failure) ? "完成" : "失败")
                + "\n共享摘要：" + (oneClick.hash ?? "未生成")
                + "\n客户端：" + (oneClick.buildBoth ? DescribeOneClickAttempt(oneClick.client) : "未请求构建")
                + "\n服务器：" + (oneClick.buildBoth ? DescribeOneClickAttempt(oneClick.server) : "未请求构建")
                + "\n" + (oneClick.failure ?? "入口、引用与共享清单校验通过。")
                + "\n当前构建日志：" + (oneClick.currentLog ?? "无");
            bool failed = !string.IsNullOrEmpty(oneClick.failure);
            try { FinishTrace(oneClick.trace, !failed); }
            catch (Exception)
            {
                failed = true;
                summary += "\n构建批次记录写入失败；本轮不能视为成功。";
                InvalidateOneClickAttemptLogs();
            }
            if (oneClick.trace != null) summary += "\n构建批次记录：" + TracePath(oneClick.trace);
            SessionState.EraseString(OneClickKey);
            oneClick = null;
            if (failed) Debug.LogError(summary);
            else Debug.Log(summary);
            if (failed && Application.isBatchMode) EditorApplication.Exit(1);
        }

        private static string DescribeOneClickAttempt(BuildAttempt attempt) => attempt == null ? "未取得成功构建报告"
            : "成功，" + attempt.destination + "\n日志：" + attempt.log + "\ncontent=" + attempt.hash;
    }
}
