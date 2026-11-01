using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using RPG4.Server;
using UnityEditor;
using UnityEngine;

namespace RPG4.Editor.Server
{
    /// <summary>Explicit administrative actions. Opening this window never connects, migrates or creates anything.</summary>
    public sealed class ServerAdmin : EditorWindow
    {
        [SerializeField] private string configurationPath = "服务器/配置/server.json";
        [SerializeField] private string migrationPath = "../数据库/迁移/001_initial.sql";
        [SerializeField] private string username = string.Empty, displayName = string.Empty;
        [NonSerialized] private string password = string.Empty;
        [NonSerialized] private CancellationTokenSource operation;
        [NonSerialized] private bool busy;
        [NonSerialized] private string status = string.Empty;

        [MenuItem("RPG4/Server Administration")]
        public static void Open() => GetWindow<ServerAdmin>("服务器管理");

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("仅在点击对应动作后访问外部密钥或 MySQL。数据库连接串来自配置指定的环境变量，须使用 SslMode=VerifyFull。", MessageType.Info);
            using (new EditorGUI.DisabledScope(busy))
            {
                configurationPath = EditorGUILayout.TextField("外部 server.json", configurationPath);
                if (GUILayout.Button("GenerateIdentity：创建固定服务器身份文件")) GenerateIdentity();
                EditorGUILayout.Space();
                migrationPath = EditorGUILayout.TextField("SQL（相对配置目录）", migrationPath);
                if (GUILayout.Button("MigrateDatabase：应用 schema 1")) _ = MigrateDatabaseAsync();
                EditorGUILayout.Space();
                username = EditorGUILayout.TextField("账号", username);
                displayName = EditorGUILayout.TextField("角色显示名", displayName);
                password = EditorGUILayout.PasswordField("密码（至少 15 个字符）", password ?? string.Empty);
                EditorGUILayout.LabelField("可信出生配置版本", OnlineSpawnConfiguration.Version.ToString());
                if (GUILayout.Button("CreateAccount：创建账号和基础角色")) _ = CreateAccountAsync();
            }
            if (busy && GUILayout.Button("取消等待")) { password = string.Empty; operation?.Cancel(); }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
        }

        private void GenerateIdentity()
        {
            string path = EditorUtility.SaveFilePanel("创建服务器身份文件（禁止存入 Assets、禁止覆盖）", Path.GetDirectoryName(ConfigurationFilePath()), "server-key", "json");
            if (string.IsNullOrEmpty(path)) return;
            try { status = "身份文件已创建。公开指纹：" + ServerIdentityFiles.GenerateIdentity(path, Application.dataPath); }
            catch (ServerOperationException exception) { status = "未创建身份文件：" + exception.Code; }
            catch (Exception) { status = "未创建身份文件：identity_operation_failed"; }
        }

        private async Task MigrateDatabaseAsync()
        {
            if (busy) return;
            await RunAsync(async token =>
            {
                LoadedServerConfiguration loaded = ServerConfiguration.Load(ConfigurationFilePath(), Application.dataPath);
                string path = Path.GetFullPath(Path.Combine(loaded.DirectoryPath, migrationPath));
                ServerConfiguration.RequireOutsideAssets(path, Application.dataPath);
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 131072) throw new ServerOperationException("migration_file_invalid");
                string sql = File.ReadAllText(path);
                var repository = new MySqlAccountRepository(loaded);
                await repository.MigrateAsync(sql, token);
                return "Schema 1 已检查并应用。未启动游戏服务器。";
            });
        }

        private async Task CreateAccountAsync()
        {
            if (busy) return;
            string requestedUsername = username, requestedDisplay = displayName, secret = password;
            password = string.Empty; // Never serialize or retain credentials in the editor window.
            try
            {
                if (!EntryProtocol.IsUsername(requestedUsername) || !EntryProtocol.IsUtf8(requestedDisplay, 1, EntryProtocol.MaxDisplayNameBytes)
                    || !EntryProtocol.IsUtf8(secret, 1, EntryProtocol.MaxPasswordBytes) || CountCharacters(secret) < 15)
                { status = "账号、显示名或密码格式不符合要求。密码保留原样，不会自动裁剪或截断。"; return; }
                await RunAsync(async token =>
                {
                    LoadedServerConfiguration loaded = ServerConfiguration.Load(ConfigurationFilePath(), Application.dataPath);
                    var repository = new MySqlAccountRepository(loaded);
                    await repository.ValidateSchemaAsync(token);
                    var hasher = new PasswordHasher(1, 0, loaded.Settings.passwordIterations);
                    PasswordRecord record = await hasher.HashAsync(secret, 1, token);
                    try
                    {
                        CreatedAccount result = await repository.CreateAccountAsync(requestedUsername, record, requestedDisplay,
                            OnlineSpawnConfiguration.Version, token);
                        return "账号和角色已创建：accountId=" + result.AccountId + "，characterId=" + result.CharacterId + "。";
                    }
                    finally { Array.Clear(record.Hash, 0, record.Hash.Length); }
                });
            }
            finally { secret = null; }
        }

        private async Task RunAsync(Func<CancellationToken, Task<string>> action)
        {
            busy = true;
            var current = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            operation = current; status = "正在执行管理操作…";
            try
            {
                string result = await action(current.Token);
                if (this != null) status = result;
            }
            catch (OperationCanceledException) { if (this != null) status = "操作已取消或超时。若已提交数据库，请核对结果后再重试。"; }
            catch (ServerOperationException exception) { if (this != null) status = "操作失败：" + exception.Code; }
            catch (Exception) { if (this != null) status = "操作失败：database_or_configuration_error。未输出异常或连接秘密。"; }
            finally
            {
                if (ReferenceEquals(operation, current)) operation = null;
                current.Dispose(); busy = false;
                if (this != null) Repaint();
            }
        }
        private static int CountCharacters(string value)
        {
            int count = 0;
            for (int i = 0; i < value.Length; i++, count++)
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            return count;
        }
        private string ConfigurationFilePath() => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), configurationPath));
        private void OnDisable() { password = string.Empty; operation?.Cancel(); }
    }
}
