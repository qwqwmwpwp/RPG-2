using System;
using System.IO;
using System.Text;
using RPG4.Frontend;
using RPG4.Networking.Contracts;
using RPG4.Server;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace RPG4.Editor.Build
{
    /// <summary>Offline deployment identity preflight only. Never load database configuration, connect,
    /// generate a key or alter client trust. Call for actual builds, not structural/content generation.</summary>
    public static class DeploymentIdentityValidation
    {
        [Serializable]
        private sealed class IdentityConfiguration
        {
            public string serverId;
            public string keyPath;
        }

        public static void Validate()
        {
            try
            {
                string path = ResolveConfigurationPath();
                ServerConfiguration.RequireOutsideAssets(path, Application.dataPath);
                var file = new FileInfo(path);
                Require(file.Exists, "Deployment identity check requires an external server configuration (-serverConfig or RPG4_SERVER_CONFIG; default 服务器/配置/server.json).");
                Require(file.Length > 0 && file.Length <= 65536, "External server configuration must be a nonempty JSON file within 64 KiB.");
                // This deliberately small DTO ignores database fields and never reads a database environment variable.
                var identity = JsonUtility.FromJson<IdentityConfiguration>(File.ReadAllText(path, new UTF8Encoding(false, true)));
                Require(identity != null && EntryProtocol.IsAscii(identity.serverId, EntryProtocol.MaxServerIdBytes)
                    && !string.IsNullOrWhiteSpace(identity.keyPath), "External server configuration must explicitly provide a valid serverId and keyPath.");
                string key = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), identity.keyPath));
                ServerConfiguration.RequireOutsideAssets(key, Application.dataPath);
                string fingerprint = ServerIdentityFiles.ValidateExisting(key, Application.dataPath);
                var catalog = AssetDatabase.LoadAssetAtPath<ClientServerCatalog>("Assets/Settings/ClientServerCatalog.asset");
                Require(catalog != null, "Deployment identity check requires the shared client server catalog.");
                TrustedClientServer selected = null;
                int count = 0;
                for (int i = 0; i < catalog.Count; ++i)
                {
                    var entry = catalog.Get(i);
                    if (entry == null || !string.Equals(entry.ServerId, identity.serverId, StringComparison.Ordinal)) continue;
                    selected = entry; ++count;
                }
                Require(count == 1, "External serverId must match exactly one trusted client catalog entry.");
                Require(EntryProtocol.IsLowerHex(selected.PublicKeyFingerprint, EntryProtocol.HashLength)
                    && string.Equals(selected.PublicKeyFingerprint, fingerprint, StringComparison.Ordinal),
                    "The configured server identity does not match the trusted client public-key fingerprint. Configure the matching public pin explicitly.");
            }
            catch (BuildFailedException) { throw; }
            catch (ServerOperationException)
            { throw new BuildFailedException("Deployment identity validation failed: configuration/key must be readable outside Assets and the existing key pair must be valid."); }
            catch (Exception)
            { throw new BuildFailedException("Deployment identity validation failed: check the external JSON and existing key file. Secret details are not logged."); }
        }

        private static string ResolveConfigurationPath()
        {
            string selected = null;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; ++i)
            {
                if (!string.Equals(arguments[i], "-serverConfig", StringComparison.OrdinalIgnoreCase)) continue;
                Require(selected == null && i + 1 < arguments.Length && !string.IsNullOrWhiteSpace(arguments[i + 1])
                    && !arguments[i + 1].StartsWith("-", StringComparison.Ordinal),
                    "Supply -serverConfig exactly once with its configuration file path.");
                selected = arguments[++i];
            }
            if (string.IsNullOrWhiteSpace(selected)) selected = Environment.GetEnvironmentVariable("RPG4_SERVER_CONFIG");
            if (string.IsNullOrWhiteSpace(selected)) selected = "服务器/配置/server.json";
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), selected));
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new BuildFailedException(message); }
    }
}
