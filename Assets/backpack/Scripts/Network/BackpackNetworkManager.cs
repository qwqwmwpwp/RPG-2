using System;
using Mirror;
using UnityEngine;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Network
{
    /// <summary>网络启动方式；正式流程应由登录 / 匹配流程驱动，而不是场景自动启动。</summary>
    public enum BackpackNetworkStartMode
    {
        None = 0, // 不自动启动，由 NetworkManagerHUD 或外部代码驱动。

        Host = 1, // 本进程同时启动服务端与本地客户端。

        Client = 2, // 只启动客户端，连接到 NetworkManager.networkAddress。

        Server = 3, // 只启动服务端。
    }

    /// <summary>背包网络生命周期入口（网络基座）：只负责「生命周期诊断日志 + 服务端物品标识生成器托管」，不含背包业务规则也不持有库存状态；物品列表同步见 <see cref="BackpackNetworkInventory"/>，玩家对象只负责身份与归属（见 <see cref="BackpackNetworkPlayer"/>）。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG4/Backpack Network Manager")]
    public partial class BackpackNetworkManager : NetworkManager
    {
        [Tooltip("场景启动时自动进入的网络模式；None 表示由 HUD 或外部代码手动启动。")]
        [SerializeField]
        private BackpackNetworkStartMode startMode = BackpackNetworkStartMode.None;

        public ItemIdGenerator ServerItemIds { get; private set; } // 服务端物品标识生成器：OnStartServer 创建、OnStopServer 释放，客户端恒为 null；每个会话使用新实例，避免跨会话复用标识。

        public bool IsServerRunning => NetworkServer.active; // 服务端是否处于运行状态。

        public bool IsClientRunning => NetworkClient.active; // 客户端是否处于连接中或已连接状态。

        /// <inheritdoc/>
        public override void Start()
        {
            base.Start();
            ApplyStartMode();
        }

        /// <inheritdoc/>
        public override void OnStartHost()
        {
            base.OnStartHost();
            BackpackLog.Info(LogModule.Network, "主机已启动：" + DescribeTransport() + "。");
        }

        /// <inheritdoc/>
        public override void OnStartServer()
        {
            base.OnStartServer();
            statisticsServerRunning = true;
            StartTrafficCapture();
            ServerItemIds = new ItemIdGenerator();
            BackpackLog.Info(LogModule.Network, "服务端已启动：" + DescribeTransport() + "，物品标识从 " + ItemIdGenerator.FirstValue + " 开始发放。");
        }

        /// <inheritdoc/>
        public override void OnStartClient()
        {
            base.OnStartClient();
            statisticsClientRunning = true;
            StartTrafficCapture();
            BackpackLog.Info(LogModule.Network, "客户端已启动，目标地址 " + networkAddress + "。");
        }

        /// <inheritdoc/>
        public override void OnStopServer()
        {
            statisticsServerRunning = false;
            StopTrafficCaptureIfIdle();
            if (ServerItemIds != null)
            {
                BackpackLog.Info(LogModule.Network, "服务端已停止，本次会话最后发放的物品标识 " + ServerItemIds.LastIssued + "。");
                ServerItemIds = null;
            }
            else
            {
                BackpackLog.Info(LogModule.Network, "服务端已停止。");
            }

            base.OnStopServer();
        }

        /// <inheritdoc/>
        public override void OnStopClient()
        {
            statisticsClientRunning = false;
            StopTrafficCaptureIfIdle();
            BackpackLog.Info(LogModule.Network, "客户端已停止。");
            base.OnStopClient();
        }

        /// <inheritdoc/>
        public override void OnStopHost()
        {
            BackpackLog.Info(LogModule.Network, "主机已停止。");
            base.OnStopHost();
        }

        /// <inheritdoc/>
        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            base.OnServerConnect(conn);
            BackpackLog.Info(LogModule.Network, "客户端已连接：connectionId=" + conn.connectionId + "。");
        }

        /// <inheritdoc/>
        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            base.OnServerDisconnect(conn);
            BackpackLog.Info(LogModule.Network, "客户端已断开：connectionId=" + conn.connectionId + "，剩余连接数 " + NetworkServer.connections.Count + "。");
        }

        /// <inheritdoc/>
        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            base.OnServerAddPlayer(conn);
            NetworkIdentity identity = conn.identity;
            BackpackLog.Info(LogModule.Network, "已为 connectionId=" + conn.connectionId + " 生成玩家对象：netId=" + (identity != null ? identity.netId : 0u) + "。");
        }

        /// <inheritdoc/>
        public override void OnClientConnect()
        {
            base.OnClientConnect();
            BackpackLog.Info(LogModule.Network, "已连接到服务端 " + networkAddress + "。");
        }

        /// <inheritdoc/>
        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();
            BackpackLog.Info(LogModule.Network, "已从服务端断开。");
        }

        /// <inheritdoc/>
        public override void OnServerError(NetworkConnectionToClient conn, TransportError error, string reason)
        {
            base.OnServerError(conn, error, reason);
            BackpackLog.Error(LogModule.Network, "服务端传输错误：connectionId=" + (conn != null ? conn.connectionId : -1) + "，error=" + error + "，reason=" + reason + "。");
        }

        /// <inheritdoc/>
        public override void OnClientError(TransportError error, string reason)
        {
            base.OnClientError(error, reason);
            BackpackLog.Error(LogModule.Network, "客户端传输错误：error=" + error + "，reason=" + reason + "。");
        }

        /// <inheritdoc/>
        public override void OnServerTransportException(NetworkConnectionToClient conn, Exception exception)
        {
            base.OnServerTransportException(conn, exception);
            BackpackLog.Error(LogModule.Network, exception);
        }

        /// <inheritdoc/>
        public override void OnClientTransportException(Exception exception)
        {
            base.OnClientTransportException(exception);
            BackpackLog.Error(LogModule.Network, exception);
        }

        /// <summary>按设计时配置的启动模式自动进入网络模式；None 不启动任何服务。</summary>
        private void ApplyStartMode()
        {
            switch (startMode)
            {
                case BackpackNetworkStartMode.Host:
                    BackpackLog.Info(LogModule.Network, "startMode=Host，自动启动主机。");
                    StartHost();
                    break;
                case BackpackNetworkStartMode.Client:
                    BackpackLog.Info(LogModule.Network, "startMode=Client，自动连接 " + networkAddress + "。");
                    StartClient();
                    break;
                case BackpackNetworkStartMode.Server:
                    BackpackLog.Info(LogModule.Network, "startMode=Server，自动启动服务端。");
                    StartServer();
                    break;
            }
        }

        /// <summary>描述当前传输组件类型与监听端口，用于启动日志。</summary>
        private string DescribeTransport()
        {
            if (transport == null)
            {
                return "未配置传输（Transport 为空）";
            }

            return transport is PortTransport portTransport
                ? transport.GetType().Name + "，端口 " + portTransport.Port
                : transport.GetType().Name;
        }
    }
}
