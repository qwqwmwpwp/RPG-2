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
        // Motor is the completed step; Plan is the committed plan consumed by the next step.
        // Movement reports use Player.ExecutedMovementRules for the completed step's flags.
        public LocomotionPlan Plan;
        public UpperBodyDemand Demand;
        public WeaponActionSnapshot Action;
        public ConsumableActionSnapshot Consumable;
        public static PlayerNetworkState Capture(Player player)
        {
            PlayerPublishedFrame frame = player.PublishedFrame;
            return new PlayerNetworkState
            {
                Tick = player.SimulationTick, Time = player.SimulationTime, Motor = player.CaptureMotorState(),
                ViewYaw = frame.ViewYaw, ViewPitch = frame.ViewPitch, Health = frame.CurrentHealth,
                MaxHealth = frame.MaxHealth, Alive = frame.Alive, HurtUntil = frame.HurtUntil,
                WeaponAvailable = frame.CanUseWeapon, Aiming = frame.IsAiming,
                Reloading = player.CanExecuteCombat && frame.Reloading, CanStand = frame.CanStand,
                ReloadPhase = player.CanExecuteCombat ? frame.ReloadPhase : 0f, Plan = frame.Plan, Demand = frame.WeaponDemand,
                Action = player.CanExecuteCombat ? frame.WeaponAction : default, WeaponId = frame.WeaponId,
                ShotSequence = player.CanExecuteCombat ? frame.ShotSequence : 0,
                ShotTime = player.CanExecuteCombat ? frame.ShotTime : 0d, ShotDuration = frame.ShotDuration,
                FadeDuration = frame.FadeDuration, ReloadDuration = frame.ReloadDuration,
                Consumable = player.CanExecuteCombat ? frame.Consumable : default
            };
        }
    }
}
