using RPG4.PlayerIntent;

namespace RPG4.PlayerMovement
{
    /// <summary>Pure movement/presentation demand shared by authority, prediction and local visual projection.</summary>
    internal static class PlayerMovementDemand
    {
        internal static UpperBodyDemand Compose(PlayerStateData data, bool canAct, bool aiming,
            bool reloading, bool shotActive, bool fadingOut, bool waitingAutomatic, bool usingConsumable = false)
        {
            MovementRuleContext rules = Configuration(data);
            rules.UsingConsumable = usingConsumable;
            rules.Reloading = reloading;
            rules.ShotActive = shotActive;
            rules.FadingOut = fadingOut;
            rules.WaitingAutomatic = waitingAutomatic;
            return PlayerMovementRules.ComposeDemand(rules, canAct, aiming);
        }

        internal static MovementRuleContext Configuration(PlayerStateData data) => new MovementRuleContext
        {
            ExplorationMoveSpeed = data.Exploration.MoveSpeed, AimMoveSpeed = data.Aim.MoveSpeed,
            HipFireMoveSpeed = data.HipFire.MoveSpeed, ReloadMoveSpeed = data.Reload.MoveSpeed,
            CrouchMoveSpeed = data.Crouch.MoveSpeed, FreeMoveInputThreshold = data.Exploration.MoveInputThreshold,
            StrafeMoveInputThreshold = data.Aim.MoveInputThreshold
        };
    }
}
