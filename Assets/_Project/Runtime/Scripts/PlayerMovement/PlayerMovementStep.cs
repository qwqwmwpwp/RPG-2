using RPG4.PlayerIntent;

namespace RPG4.PlayerMovement
{
    public struct MovementRuleContext
    {
        public bool Active, Alive, CanUseWeapon, IsHurt, Reloading, ShotActive, FadingOut, WaitingAutomatic;
        public bool PhysicallyCrouched, CanStand, UsingConsumable;
        public float ViewYaw, ExplorationMoveSpeed, AimMoveSpeed, HipFireMoveSpeed, ReloadMoveSpeed, CrouchMoveSpeed;
        public float FreeMoveInputThreshold, StrafeMoveInputThreshold;
    }

    public readonly struct MovementRuleResult
    {
        public readonly LocomotionPlan Plan;
        public readonly UpperBodyDemand Demand;
        public readonly bool Aiming;
        public MovementRuleResult(LocomotionPlan plan, UpperBodyDemand demand, bool aiming)
        { Plan = plan; Demand = demand; Aiming = aiming; }
    }

    public struct MovementKinematicsSettings
    {
        public float DirectionSmoothTime, SpeedSmoothTime, FreeTurnSmoothTime, StrafeTurnSmoothTime, Gravity;
    }

    public readonly struct PlayerMovementEvaluation
    {
        public readonly PlayerMotorState StartMotor;
        public readonly MovementRuleResult Rules;
        public readonly MovementKinematicsSettings Settings;
        public readonly float ViewYaw, ViewPitch, DeltaTime;
        public readonly long Tick;
        public readonly double Time;
        public readonly bool ResetTurn;
        public PlayerMovementEvaluation(PlayerMotorState startMotor, MovementRuleResult rules,
            MovementKinematicsSettings settings, float viewYaw, float viewPitch, long tick, double time,
            float deltaTime, bool resetTurn)
        {
            StartMotor = startMotor; Rules = rules; Settings = settings; ViewYaw = viewYaw;
            ViewPitch = viewPitch; Tick = tick; Time = time; DeltaTime = deltaTime; ResetTurn = resetTurn;
        }
    }

    public readonly struct PlayerMovementCommit
    {
        public readonly bool UseAuthorityMotor;
        public readonly PlayerMotorState Motor;
        public PlayerMovementCommit(bool useAuthorityMotor, PlayerMotorState motor)
        { UseAuthorityMotor = useAuthorityMotor; Motor = motor; }
    }

    public interface IPlayerNetworkMovementResolver
    {
        PlayerMovementCommit Resolve(in PlayerMovementEvaluation evaluation);
    }

    // Zero/default must never authorize a physical commit.
    public enum MovementPathKind : byte { RequiresAuthority, ClearFlatGround, Blocked }

    public readonly struct MovementPathResult
    {
        public readonly MovementPathKind Kind;
        public readonly float GroundedRootY;
        public readonly bool CanStand;
        public MovementPathResult(MovementPathKind kind, float groundedRootY, bool canStand)
        { Kind = kind; GroundedRootY = groundedRootY; CanStand = canStand; }
    }

    public interface IPlayerMovementWorld
    {
        MovementPathResult CheckPath(in PlayerMotorState from, in PlayerMotorState proposed);
    }
}
