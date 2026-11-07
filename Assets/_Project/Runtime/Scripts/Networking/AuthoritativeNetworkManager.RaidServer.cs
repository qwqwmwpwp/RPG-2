using System;
using Mirror;
using RPG4.Networking.Contracts;
using RPG4.Raid.Application;
using RPG4.Raid.Domain;
using UnityEngine;

namespace RPG4.Networking
{
    public sealed partial class AuthoritativeNetworkManager
    {
        private IServerInventoryRaidService configuredRaidStore;
        private ServerRaidApplication raidApplication;
        private ServerRaidConnections raidConnections;
        private UnityRaidWorld raidWorld;
        private ServerMatchRuntime matchRuntime;

        /// <summary>Attaches the process-owned single-match lifecycle to the Mirror callbacks.</summary>
        public void ConfigureMatchRuntime(ServerMatchRuntime runtime)
        {
            matchRuntime = runtime;
        }

        public void ConfigureMatchDuration(double seconds)
        {
            if (seconds < 30d || seconds > 86400d) throw new ArgumentOutOfRangeException(nameof(seconds));
            raidDurationSeconds = (float)seconds;
        }

        public void ConfigureInventoryRaidService(IServerInventoryRaidService service)
        { configuredRaidStore = service ?? throw new ArgumentNullException(nameof(service)); }
        private void StartRaidServerMessages()
        {
            raidConnections = new ServerRaidConnections(this);
            raidWorld = new UnityRaidWorld(this, raidConnections);
            if (configuredRaidStore != null)
            {
                raidApplication = new ServerRaidApplication(configuredRaidStore, new ServerLobbyInventoryExecutor(this),
                    raidWorld, raidConnections, raidDurationSeconds);
                raidApplication.Tick(Time.realtimeSinceStartupAsDouble);
            }
            NetworkServer.RegisterHandler<LobbyRequestMessage>(OnLobbyRequest);
        }
        private void StopRaidServerMessages()
        {
            NetworkServer.UnregisterHandler<LobbyRequestMessage>();
            matchRuntime = null;
            // A stopped application never delivers old async completions to a later server run.
            raidApplication?.Dispose(); raidApplication = null;
            raidWorld?.Clear(); raidWorld = null;
            raidConnections?.Clear(); raidConnections = null;
        }
        private string FindRaidSession(NetworkConnectionToClient connection)
        {
            if (raidApplication == null) return null;
            ServerRaidConnections.Entry entry = raidConnections.FindOrCreate(connection);
            if (entry == null) return null;
            raidApplication.RegisterSession(entry.Key, new RaidIdentity(entry.Identity.AccountId, entry.Identity.CharacterId, entry.Identity.SessionId));
            return entry.Key;
        }
        private void OnLobbyRequest(NetworkConnectionToClient connection, LobbyRequestMessage request)
        {
            string key = FindRaidSession(connection);
            if (key != null) raidApplication.HandleCommand(key, ServerRaidConnections.ToCommand(request));
        }
        public override void OnServerReady(NetworkConnectionToClient connection)
        {
            if (!usesGameplaySceneBindings) { base.OnServerReady(connection); return; }
            string key = FindRaidSession(connection);
            if (key != null) raidApplication.Ready(key);
        }
        private void BeginAuthorizedRaidSpawn(NetworkConnectionToClient connection)
        {
            string key = FindRaidSession(connection);
            if (key != null) raidApplication.AddPlayer(key);
        }

        internal void NotifyRaidPlayerLoading(string key)
        {
            matchRuntime?.JoinPlayer(key, Time.realtimeSinceStartupAsDouble);
        }

        internal void NotifyRaidPlayerActive(string key)
        {
            matchRuntime?.MarkReady(key, Time.realtimeSinceStartupAsDouble);
        }

        internal void NotifyRaidPlayerReconnected(string key)
        {
            matchRuntime?.Reconnect(key, Time.realtimeSinceStartupAsDouble);
        }
        private void TickRaidSessions()
        {
            if (usesGameplaySceneBindings && NetworkClient.active) TickLobbyClient();
            if (NetworkServer.active) raidApplication?.Tick(Time.realtimeSinceStartupAsDouble);
        }
    }
}
