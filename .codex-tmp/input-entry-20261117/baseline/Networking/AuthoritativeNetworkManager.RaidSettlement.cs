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
            string key = entry?.Key ?? raidWorld?.FindActorSession(controller != null ? controller.gameObject : null);
            if (key == null) return;
            raidApplication?.EndRaid(key, RaidOutcome.Death, Time.realtimeSinceStartupAsDouble);
        }
        public bool TryExtractPlayer(NetworkPlayerController controller)
        {
            if (!NetworkServer.active || controller == null || !controller.isActiveAndEnabled || controller.Player == null
                || !controller.Player.IsAlive || controller.connectionToClient == null) return false;
            ServerRaidConnections.Entry entry = raidConnections?.Find(controller.connectionToClient);
            return entry != null && raidApplication != null
                && raidApplication.TryExtract(entry.Key, Time.realtimeSinceStartupAsDouble);
        }
        private bool CaptureDisconnectedRaid(NetworkConnectionToClient connection)
        {
            ServerRaidConnections.Entry entry = raidConnections?.Find(connection);
            if (entry == null) return false;
            raidApplication?.Disconnect(entry.Key, Time.realtimeSinceStartupAsDouble);
            entry.Disconnected = true;
            return raidApplication != null && (raidApplication.CanReconnect(entry.Key) || raidApplication.HasPendingSettlement(entry.Key));
        }
    }
}
