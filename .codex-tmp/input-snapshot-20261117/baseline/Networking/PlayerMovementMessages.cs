using RPG4.PlayerMovement;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.Networking
{
    public enum MovementRejectReason : byte
    { None, InvalidInput, StaleEpoch, MissingInput, TimeBudget, InvalidMode, Kinematics, Collision, AuthorityChanged, HistoryOverflow, Disabled }
    public enum MovementTakeResult : byte { None, Ready, RebaseRequired }
    public struct PlayerMovementReport
    {
        public uint Epoch, CorrectionId, ConstraintRevision;
        public ulong Step;
        public PlayerInputPacket Input;
        public PlayerMotorState PredictedMotor;
        public bool Aiming, Strafe;
        public bool IsValid => Epoch != 0 && Step != 0 && Step < ulong.MaxValue - 128 && Input.IsValid
            && MovementValues.Finite(PredictedMotor) && Mathf.Abs(PredictedMotor.Yaw) <= 360f;
    }
    public struct PlayerMovementAck
    {
        public uint Epoch, CorrectionId, ConstraintRevision;
        public ulong AcceptedStep, ProcessedInputSequence;
        public long ServerTick;
    }
    public struct PlayerMovementBaseline
    {
        public uint Epoch, CorrectionId, ConstraintRevision;
        public ulong Step, ProcessedInputSequence;
        public PlayerNetworkState State;
        public bool IsValid => Epoch != 0 && State.Tick >= 0 && MovementValues.Finite(State.Time)
            && MovementValues.Finite(State.Motor) && MovementValues.Finite(State.ViewYaw) && MovementValues.Finite(State.ViewPitch)
            && MovementValues.Valid(State.Consumable);
    }
    public struct PlayerMovementCorrection { public PlayerMovementBaseline Baseline; public MovementRejectReason Reason; }
    public struct PlayerAuthorityState { public uint Revision, ConstraintRevision; public PlayerAuthoritySnapshot Snapshot; }
    public struct PlayerMovementRecord { public PlayerMovementReport Report; public PlayerNetworkState PredictedState; }
    internal static class MovementValues
    {
        internal static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        internal static bool Finite(double f) => !double.IsNaN(f) && !double.IsInfinity(f);
        internal static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        internal static bool Finite(PlayerMotorState m) => Finite(m.Position) && Finite(m.Velocity)
            && Finite(m.Yaw) && Finite(m.VerticalVelocity) && Finite(m.TurnVelocity);
        internal static bool Valid(ConsumableActionSnapshot s) => Finite(s.StartTime) && Finite(s.EndTime)
            && s.StartTime >= 0d && s.EndTime >= s.StartTime
            && (!s.Active || s.ActionId != 0 && s.ItemId != 0 && s.EndTime > s.StartTime);
        internal static bool SameInput(PlayerInputPacket a, PlayerInputPacket b) => a.Sequence == b.Sequence
            && a.ObservedServerTick == b.ObservedServerTick && a.FireRequestId == b.FireRequestId
            && a.Move.Equals(b.Move) && a.Look.Equals(b.Look) && a.Crouch == b.Crouch && a.Aim == b.Aim
            && a.FirePressed == b.FirePressed && a.FireHeld == b.FireHeld && a.Reload == b.Reload && a.Interact == b.Interact;
        internal static bool SamePrediction(PlayerMovementReport a, PlayerMovementReport b) => a.PredictedMotor.Equals(b.PredictedMotor)
            && a.Aiming == b.Aiming && a.Strafe == b.Strafe && a.ConstraintRevision == b.ConstraintRevision;
    }
}
