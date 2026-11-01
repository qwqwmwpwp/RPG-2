using System;
using System.IO;
using RPG4.Networking.Contracts;
using UnityEngine;

namespace RPG4.Server
{
    [Serializable]
    public sealed class ServerConfiguration
    {
        public string serverId = "local-dev-01";
        public int port = 7777;
        public string keyPath = "identity/server-key.json";
        public string databaseConnectionEnvironmentVariable = "RPG4_MYSQL_CONNECTION";
        public int maxPlayers = 32;
        public int maxConnections = 64;
        public int maxRawConnections = 96;
        public int maxRawConnectionsPerIp = 8;
        public int databaseTimeoutSeconds = 5;
        public int startupTimeoutSeconds = 90;
        public int shutdownTimeoutSeconds = 15;
        public int healthIntervalSeconds = 10;
        public int passwordIterations = 600000;
        public int maxStoredPasswordIterations = 1200000;
        public int kdfConcurrency = 2;
        public int kdfQueueCapacity = 16;
        public int maxPendingAuthentications = 32;
        public int rateWindowSeconds = 60;
        public int attemptsPerIp = 30;
        public int attemptsPerUsername = 8;
        public int attemptsGlobal = 120;
        public int rateKeyCapacity = 4096;

        public void Validate()
        {
            if (!EntryProtocol.IsAscii(serverId, 64) || port < 1 || port > 65535
                || string.IsNullOrWhiteSpace(keyPath) || string.IsNullOrWhiteSpace(databaseConnectionEnvironmentVariable)
                || maxPlayers < 1 || maxPlayers > 1000 || maxConnections < maxPlayers || maxConnections > 4096
                || maxRawConnections < maxConnections || maxRawConnections > 8192
                || maxRawConnectionsPerIp < 1 || maxRawConnectionsPerIp > maxRawConnections
                || databaseTimeoutSeconds < 1 || databaseTimeoutSeconds > 10
                || startupTimeoutSeconds < 10 || startupTimeoutSeconds > 300 || shutdownTimeoutSeconds < 1 || shutdownTimeoutSeconds > 60
                || healthIntervalSeconds < 2 || healthIntervalSeconds > 300
                || passwordIterations < PasswordHasher.CurrentIterations || maxStoredPasswordIterations < passwordIterations
                || maxStoredPasswordIterations > PasswordHasher.MaxIterations || kdfConcurrency < 1 || kdfConcurrency > 16
                || kdfQueueCapacity < 0 || kdfQueueCapacity > 256 || maxPendingAuthentications < 1 || maxPendingAuthentications > 512
                || rateWindowSeconds < 1 || rateWindowSeconds > 3600 || attemptsPerIp < 1 || attemptsPerUsername < 1
                || attemptsGlobal < 1 || rateKeyCapacity < 64 || rateKeyCapacity > 65536)
                throw new ServerOperationException("configuration_invalid");
        }

        /// <summary>Only paths and non-secret options are stored in JSON. Call from the main thread.</summary>
        public static LoadedServerConfiguration Load(string path, string assetsPath)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                RequireOutsideAssets(fullPath, assetsPath);
                var file = new FileInfo(fullPath);
                if (!file.Exists || file.Length > 65536) throw new ServerOperationException("configuration_missing");
                ServerConfiguration settings = JsonUtility.FromJson<ServerConfiguration>(File.ReadAllText(fullPath));
                if (settings == null) throw new ServerOperationException("configuration_invalid");
                settings.Validate();
                string key = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath), settings.keyPath));
                RequireOutsideAssets(key, assetsPath);
                string connection = Environment.GetEnvironmentVariable(settings.databaseConnectionEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(connection)) throw new ServerOperationException("database_configuration_missing");
                return new LoadedServerConfiguration(settings, Path.GetDirectoryName(fullPath), assetsPath, key, connection);
            }
            catch (ServerOperationException) { throw; }
            catch (Exception) { throw new ServerOperationException("configuration_invalid"); }
        }

        public static void RequireOutsideAssets(string path, string assetsPath)
        {
            string root = Path.GetFullPath(assetsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(path);
            if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ServerOperationException("secret_path_inside_assets");
        }
    }

    public sealed class LoadedServerConfiguration
    {
        public ServerConfiguration Settings { get; }
        public string KeyPath { get; }
        public string DirectoryPath { get; }
        internal string AssetsPath { get; }
        internal string ConnectionString { get; }
        internal LoadedServerConfiguration(ServerConfiguration settings, string directoryPath, string assetsPath, string keyPath, string connectionString)
        { Settings = settings; DirectoryPath = directoryPath; AssetsPath = assetsPath; KeyPath = keyPath; ConnectionString = connectionString; }
    }

    /// <summary>Fixed, non-secret classification; never wrap the original database or credential exception.</summary>
    public sealed class ServerOperationException : Exception
    {
        public string Code { get; }
        public ServerOperationException(string code) : base(code) { Code = code; }
    }
}
