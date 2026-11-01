using System;
using System.Collections.Generic;
using System.Net;
using Mirror;
using UnityEngine;

namespace RPG4.Networking.Transports
{
    /// <summary>Bounds raw established connections before EncryptionTransport allocates handshake state.
    /// Configure EncryptionTransport -> this wrapper -> KCP. This is not UDP flood protection.</summary>
    [DisallowMultipleComponent]
    public sealed class ConnectionAdmissionTransport : Transport, PortTransport
    {
        public Transport Inner;
        [SerializeField] private int totalLimit = 140;
        [SerializeField] private int perIpLimit = 8;
        private readonly Dictionary<int, string> admitted = new Dictionary<int, string>();
        private readonly Dictionary<string, int> perIp = new Dictionary<string, int>(StringComparer.Ordinal);
        public int RawConnectionCount => admitted.Count;
        public ushort Port { get => Inner is PortTransport port ? port.Port : (ushort)0;
            set { if (Inner is PortTransport port) port.Port = value; else throw new InvalidOperationException("Inner transport has no port."); } }
        public void ConfigureLimits(int total, int perAddress)
        {
            if (ServerActive() || admitted.Count != 0 || total < 1 || total > 20000 || perAddress < 1 || perAddress > total)
                throw new InvalidOperationException("Invalid raw connection admission configuration.");
            totalLimit = total; perIpLimit = perAddress;
        }
        private void ValidateInner()
        { if (Inner == null || Inner == this) throw new InvalidOperationException("Admission inner transport is missing."); }
        public override bool Available() => Inner != null && Inner != this && Inner.Available();
        public override bool IsEncrypted => Inner != null && Inner.IsEncrypted;
        public override string EncryptionCipher => Inner != null ? Inner.EncryptionCipher : string.Empty;
        public override bool ClientConnected() => Inner != null && Inner.ClientConnected();
        private void BindClient()
        {
            ValidateInner();
            Inner.OnClientConnected = () => OnClientConnected?.Invoke();
            Inner.OnClientDisconnected = () => OnClientDisconnected?.Invoke();
            Inner.OnClientDataReceived = (data, channel) => OnClientDataReceived?.Invoke(data, channel);
            Inner.OnClientDataSent = (data, channel) => OnClientDataSent?.Invoke(data, channel);
            Inner.OnClientError = (error, message) => OnClientError?.Invoke(error, message);
            Inner.OnClientTransportException = exception => OnClientTransportException?.Invoke(exception);
        }
        public override void ClientConnect(string address) { BindClient(); Inner.ClientConnect(address); }
        public override void ClientConnect(Uri uri) { BindClient(); Inner.ClientConnect(uri); }
        public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable) => Inner.ClientSend(segment, channelId);
        public override void ClientDisconnect() => Inner.ClientDisconnect();
        public override Uri ServerUri() => Inner.ServerUri();
        public override bool ServerActive() => Inner != null && Inner != this && Inner.ServerActive();
        public override void ServerStart()
        {
            ValidateInner();
            if (totalLimit < 1 || perIpLimit < 1 || perIpLimit > totalLimit) throw new InvalidOperationException("Invalid admission limits.");
            admitted.Clear(); perIp.Clear();
#pragma warning disable CS0618
            Inner.OnServerConnected = id => Connected(id, Inner.ServerGetClientAddress(id));
#pragma warning restore CS0618
            Inner.OnServerConnectedWithAddress = Connected;
            Inner.OnServerDisconnected = Disconnected;
            Inner.OnServerDataReceived = (id, bytes, channel) => { if (admitted.ContainsKey(id)) OnServerDataReceived?.Invoke(id, bytes, channel); };
            Inner.OnServerDataSent = (id, bytes, channel) => { if (admitted.ContainsKey(id)) OnServerDataSent?.Invoke(id, bytes, channel); };
            Inner.OnServerError = (id, error, message) => { if (admitted.ContainsKey(id)) OnServerError?.Invoke(id, error, message); };
            Inner.OnServerTransportException = (id, exception) => { if (admitted.ContainsKey(id)) OnServerTransportException?.Invoke(id, exception); };
            Inner.ServerStart();
        }
        private void Connected(int id, string address)
        {
            if (!IPAddress.TryParse(address, out IPAddress parsed)) { Inner.ServerDisconnect(id); return; }
            string key = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4().ToString() : parsed.ToString();
            perIp.TryGetValue(key, out int count);
            if (admitted.ContainsKey(id) || admitted.Count >= totalLimit || count >= perIpLimit)
            { Inner.ServerDisconnect(id); return; }
            admitted.Add(id, key); perIp[key] = count + 1;
            try
            {
                if (OnServerConnectedWithAddress != null) OnServerConnectedWithAddress(id, key);
#pragma warning disable CS0618
                else OnServerConnected?.Invoke(id);
#pragma warning restore CS0618
            }
            catch (Exception) { Inner.ServerDisconnect(id); }
        }
        private void Disconnected(int id)
        {
            if (!admitted.TryGetValue(id, out string key)) return;
            admitted.Remove(id);
            int count = perIp[key] - 1;
            if (count == 0) perIp.Remove(key); else perIp[key] = count;
            OnServerDisconnected?.Invoke(id);
        }
        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable)
        { if (admitted.ContainsKey(connectionId)) Inner.ServerSend(connectionId, segment, channelId); }
        public override void ServerDisconnect(int connectionId) => Inner.ServerDisconnect(connectionId);
        public override string ServerGetClientAddress(int connectionId) => Inner.ServerGetClientAddress(connectionId);
        public override void ServerStop() { try { Inner.ServerStop(); } finally { admitted.Clear(); perIp.Clear(); } }
        public override int GetMaxPacketSize(int channelId = Channels.Reliable) => Inner.GetMaxPacketSize(channelId);
        public override int GetBatchThreshold(int channelId = Channels.Reliable) => Inner.GetBatchThreshold(channelId);
        public override void ClientEarlyUpdate() { if (enabled) Inner.ClientEarlyUpdate(); }
        public override void ClientLateUpdate() { if (enabled) Inner.ClientLateUpdate(); }
        public override void ServerEarlyUpdate() { if (enabled) Inner.ServerEarlyUpdate(); }
        public override void ServerLateUpdate() { if (enabled) Inner.ServerLateUpdate(); }
        public override void Shutdown() { try { Inner?.Shutdown(); } finally { admitted.Clear(); perIp.Clear(); } }
    }
}
