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
        private ServerRaidApplication raidApplication;
        private ServerRaidConnections raidConnections;
        private UnityRaidWorld raidWorld;
        private ServerMatchRuntime matchRuntime;

        /// <summary>Attaches the process-owned single-match lifecycle to the Mirror callbacks.</summary>
        public void ConfigureMatchRuntime(ServerMatchRuntime runtime)
        {
            matchRuntime = runtime;
            raidApplication?.ConfigureMatchRuntime(runtime);
            ServerMatchRuntimeLocator.Set(runtime);
        }

        public void ConfigureMatchDuration(double seconds)
        {
            if (seconds < 30d || seconds > 86400d) throw new ArgumentOutOfRangeException(nameof(seconds));
            raidDurationSeconds = (float)seconds;
        }

        private void StartRaidServerMessages()
        {
            raidConnections = new ServerRaidConnections(this);
            raidWorld = new UnityRaidWorld(this, raidConnections);
            raidApplication = new ServerRaidApplication(raidWorld, raidConnections, raidDurationSeconds, matchRuntime);
            raidApplication.Tick(Time.realtimeSinceStartupAsDouble);
            NetworkServer.RegisterHandler<LobbyRequestMessage>(OnLobbyRequest);
        }
        private void StopRaidServerMessages()
        {
            NetworkServer.UnregisterHandler<LobbyRequestMessage>();
            if (ReferenceEquals(ServerMatchRuntimeLocator.Current, matchRuntime))
                ServerMatchRuntimeLocator.Set(null);
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
            raidApplication.RegisterSession(entry.Key, new RaidIdentity(entry.Identity.AccountId, entry.Identity.CharacterId, entry.Identity.SessionId),
                Time.realtimeSinceStartupAsDouble);
            return entry.Key;
        }
        private void OnLobbyRequest(NetworkConnectionToClient connection, LobbyRequestMessage request)
        {
            string key = FindRaidSession(connection);
            if (key != null) raidApplication.HandleCommand(key, ServerRaidConnections.ToCommand(request), Time.realtimeSinceStartupAsDouble);
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
            if (key != null) raidApplication.AddPlayer(key, Time.realtimeSinceStartupAsDouble);
        }
        private void TickRaidSessions()
        {
            if (usesGameplaySceneBindings && NetworkClient.active) TickLobbyClient();
            if (NetworkServer.active) raidApplication?.Tick(Time.realtimeSinceStartupAsDouble);
        }
    }
}
