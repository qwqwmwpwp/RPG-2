using Mirror;
using RPG4.Raid.Domain;
using UnityEngine;

namespace RPG4.Networking
{
    public sealed partial class AuthoritativeNetworkManager
    {
        public void OnServerPlayerDied(NetworkPlayerController controller)
        {
            ServerRaidConnections.Entry entry = raidConnections?.Find(controller != null ? controller.connectionToClient : null);
            if (entry != null)
            {
                raidApplication?.EndRaid(entry.Key, RaidOutcome.Death);
                matchRuntime?.MarkDead(entry.Key, Time.realtimeSinceStartupAsDouble);
            }
        }
        public bool TryExtractPlayer(NetworkPlayerController controller)
        {
            if (!NetworkServer.active || controller == null || !controller.isActiveAndEnabled || controller.Player == null
                || !controller.Player.IsAlive || controller.connectionToClient == null) return false;
            ServerRaidConnections.Entry entry = raidConnections?.Find(controller.connectionToClient);
            if (entry == null || raidApplication == null || !raidApplication.TryExtract(entry.Key)) return false;
            matchRuntime?.MarkExtracted(entry.Key, Time.realtimeSinceStartupAsDouble);
            return true;
        }
        private bool CaptureDisconnectedRaid(NetworkConnectionToClient connection)
        {
            ServerRaidConnections.Entry entry = raidConnections?.Find(connection);
            if (entry == null) return false;
            raidApplication?.Disconnect(entry.Key);
            matchRuntime?.Disconnect(entry.Key, Time.realtimeSinceStartupAsDouble);
            entry.Disconnected = true;
            return raidApplication != null && raidApplication.CanReconnect(entry.Key);
        }
    }
}
