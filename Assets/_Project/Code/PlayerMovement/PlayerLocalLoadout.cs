using System;
using Mirror;
using RPG4.LoadoutTemplate;

namespace RPG4.PlayerMovement
{
    /// <summary>本机角色的快捷操作与 HUD 共用同一份配装绑定。</summary>
    public sealed class PlayerLocalLoadout
    {
        private NetworkRoleLoadout networkSource;
        public bool Available { get; private set; }
        public event Action<bool> AvailabilityChanged;

        public bool IsBoundTo(NetworkRoleLoadout source) => source != null && networkSource == source;
        public void BindNetwork(NetworkRoleLoadout source) => networkSource = source;
        public void UnbindNetwork()
        {
            networkSource = null;
            SetAvailable(false);
        }

        internal void Refresh(Player owner)
        {
            bool local = owner.isActiveAndEnabled && owner.Input != null && owner.Input.UsesLocalInput
                && owner.Equipment?.Role != null;
            bool available = local && (owner.IsNetworkSimulation
                ? networkSource != null && networkSource.isLocalPlayer && networkSource.isOwned && networkSource.IsReady
                : !NetworkClient.active && !NetworkServer.active);
            SetAvailable(available);
        }

        internal void SetAvailable(bool available)
        {
            if (Available == available) return;
            Available = available;
            AvailabilityChanged?.Invoke(available);
        }
    }
}
