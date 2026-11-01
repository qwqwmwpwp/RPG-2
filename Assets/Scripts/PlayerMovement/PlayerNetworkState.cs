using RPG4.PlayerIntent;

namespace RPG4.PlayerMovement
{
    /// <summary>Server-authored read-only state. No client command accepts this payload.</summary>
    public struct PlayerNetworkState
    {
        public long Tick;
        public double Time, HurtUntil, ShotTime;
        public PlayerMotorState Motor;
        public float ViewYaw, ViewPitch, Health, MaxHealth, ReloadPhase, ShotDuration, FadeDuration, ReloadDuration;
        public bool Alive, Aiming, Reloading, CanStand, WeaponAvailable;
        public ulong WeaponId, ShotSequence;
        public LocomotionPlan Plan;
        public UpperBodyDemand Demand;
        public WeaponActionSnapshot Action;
        public static PlayerNetworkState Capture(Player player)
        {
            PlayerPublishedFrame frame = player.PublishedFrame;
            return new PlayerNetworkState
            {
                Tick = player.SimulationTick, Time = player.SimulationTime, Motor = player.CaptureMotorState(),
                ViewYaw = frame.ViewYaw, ViewPitch = frame.ViewPitch, Health = frame.CurrentHealth,
                MaxHealth = frame.MaxHealth, Alive = frame.Alive, HurtUntil = frame.HurtUntil,
                WeaponAvailable = frame.CanUseWeapon, Aiming = frame.IsAiming, Reloading = frame.Reloading, CanStand = frame.CanStand,
                ReloadPhase = frame.ReloadPhase, Plan = frame.Plan, Demand = frame.WeaponDemand,
                Action = frame.WeaponAction, WeaponId = frame.WeaponId, ShotSequence = frame.ShotSequence,
                ShotTime = frame.ShotTime, ShotDuration = frame.ShotDuration,
                FadeDuration = frame.FadeDuration, ReloadDuration = frame.ReloadDuration
            };
        }
    }
}
