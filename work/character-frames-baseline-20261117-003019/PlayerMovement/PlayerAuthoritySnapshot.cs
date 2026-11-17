using RPG4.PlayerIntent;

namespace RPG4.PlayerMovement
{
    /// <summary>Business facts only; installing this value never restores movement or view.</summary>
    public struct PlayerAuthoritySnapshot
    {
        public long Tick;
        public double Time, HurtUntil, ShotTime;
        public float Health, MaxHealth, ReloadPhase, ShotDuration, FadeDuration, ReloadDuration;
        public bool Alive, WeaponAvailable, Reloading;
        public ulong WeaponId, ShotSequence;
        public WeaponActionSnapshot Action;
        public ConsumableActionSnapshot Consumable;

        public static PlayerAuthoritySnapshot Capture(Player player) => player.CaptureNetworkAuthorityState();
    }
}
