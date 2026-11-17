using System;
using Mirror;
using Mirror.Transports.Encryption;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using UnityEngine;

namespace RPG4.Frontend
{
    [Serializable]
    public sealed class TrustedClientServer
    {
        [SerializeField] private string displayName;
        [SerializeField] private string serverId;
        [SerializeField] private string address;
        [SerializeField, Range(1, 65535)] private int port = 7777;
        [SerializeField] private string publicKeyFingerprint;
        public string DisplayName => displayName;
        public string ServerId => serverId;
        public string Address => address;
        public int Port => port;
        public string PublicKeyFingerprint => publicKeyFingerprint;
    }

    [CreateAssetMenu(menuName = "RPG4/Frontend/Trusted Server Catalog")]
    public sealed class ClientServerCatalog : ScriptableObject
    {
        [SerializeField] private EntryContentManifest contentManifest;
        [SerializeField] private TrustedClientServer[] servers = Array.Empty<TrustedClientServer>();
        public string ContentHash => contentManifest != null ? contentManifest.ContentHash : string.Empty;
        public int Count => servers == null ? 0 : servers.Length;
        public TrustedClientServer Get(int index) => index >= 0 && index < Count ? servers[index] : null;
        public bool Validate(int index, string address, int port, out string reason)
        {
            TrustedClientServer server = Get(index);
            if (server == null) { reason = "尚未配置受信任服务器，请联系管理员。"; return false; }
            if (!IsValidAddress(address) || port < 1 || port > 65535)
            { reason = "请输入有效地址与 1–65535 端口。"; return false; }
            if (!EntryProtocol.IsAscii(server.ServerId, EntryProtocol.MaxServerIdBytes))
            { reason = "服务器标识配置无效，请联系管理员。"; return false; }
            if (!EntryProtocol.IsLowerHex(server.PublicKeyFingerprint, EntryProtocol.HashLength))
            { reason = "服务器安全配置缺失：管理员尚未配置可信指纹。"; return false; }
            if (contentManifest == null || !contentManifest.IsValid)
            { reason = "游戏内容清单尚未生成，暂时无法登录。"; return false; }
            reason = null; return true;
        }
        public static bool IsValidAddress(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 253 || value != value.Trim()) return false;
            foreach (char c in value)
                if (c > 127 || char.IsWhiteSpace(c) || c == '/' || c == (char)92 || c == '@' || c == '?' || c == '#') return false;
            return Uri.CheckHostName(value) != UriHostNameType.Unknown;
        }
        public static bool ValidateManager(AuthoritativeNetworkManager prefab, out string reason)
        {
            if (prefab == null || !prefab.UsesGameplaySceneBindings || !(prefab.authenticator is AccountAuthenticator)
                || !(prefab.transport is EncryptionTransport transport) || !(transport.Inner is PortTransport))
            { reason = "联机管理器配置不完整，暂时无法登录。"; return false; }
            reason = null; return true;
        }
    }
}
