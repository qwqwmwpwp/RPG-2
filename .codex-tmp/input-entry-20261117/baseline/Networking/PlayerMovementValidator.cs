using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking
{
    public struct MovementValidationContext
    {
        public uint Epoch, ConstraintRevision;
        public ulong Step;
        public PlayerMovementEvaluation Evaluation;
        public PlayerMotorState ReferenceMotor;
        public float DeltaTime, PositionTolerance, VelocityTolerance, YawTolerance, TurnTolerance;
        public static MovementValidationContext Create(uint epoch, ulong step, uint revision, in PlayerMovementEvaluation evaluation, in PlayerMotorState reference)
            => new MovementValidationContext { Epoch = epoch, Step = step, ConstraintRevision = revision, Evaluation = evaluation,
                ReferenceMotor = reference, DeltaTime = 0.02f, PositionTolerance = 0.002f, VelocityTolerance = 0.005f, YawTolerance = 0.1f, TurnTolerance = 0.05f };
    }
    public readonly struct MovementValidationResult
    {
        public readonly bool Accepted, UseAuthorityMotor;
        public readonly MovementRejectReason Reason;
        public readonly PlayerMotorState Motor, ReferenceMotor;
        public MovementValidationResult(bool accepted, MovementRejectReason reason, PlayerMotorState motor, PlayerMotorState reference)
        { Accepted = accepted; UseAuthorityMotor = !accepted; Reason = reason; Motor = motor; ReferenceMotor = reference; }
        public PlayerMovementCommit ToCommit() => new PlayerMovementCommit(UseAuthorityMotor, Motor);
    }
    public static class PlayerMovementValidator
    {
        public static MovementValidationResult Validate(in MovementValidationContext context, in PlayerMovementReport report, IPlayerMovementWorld world)
        {
            if (!report.IsValid) return Reject(MovementRejectReason.InvalidInput);
            if (report.Epoch != context.Epoch || report.Step != context.Step) return Reject(MovementRejectReason.StaleEpoch);
            if (report.ConstraintRevision != context.ConstraintRevision) return Reject(MovementRejectReason.AuthorityChanged);
            PlayerMovementEvaluation e = context.Evaluation;
            if (Mathf.Abs(context.DeltaTime - 0.02f) > 0.000001f || Mathf.Abs(e.DeltaTime - context.DeltaTime) > 0.000001f) return Reject(MovementRejectReason.TimeBudget);
            if (report.Aiming != e.Rules.Aiming || report.Strafe != e.Rules.Plan.Strafe || report.PredictedMotor.Crouched != e.Rules.Plan.Crouched) return Reject(MovementRejectReason.InvalidMode);
            PlayerMotorState reference = PlayerMotorKinematics.Step(context.ReferenceMotor, e.Rules.Plan, e.Settings, e.ResetTurn, context.DeltaTime);
            // Reject unbounded coordinates before they can enter a physics query.
            if ((report.PredictedMotor.Position - reference.Position).sqrMagnitude > 1f
                || (report.PredictedMotor.Velocity - reference.Velocity).sqrMagnitude > 1f) return Reject(MovementRejectReason.Kinematics);
            if (world == null) return Reject(MovementRejectReason.Collision);
            MovementPathResult path = world.CheckPath(e.StartMotor, reference);
            if (path.Kind != MovementPathKind.ClearFlatGround || !path.CanStand && !reference.Crouched) return Reject(MovementRejectReason.Collision);
            reference.Position.y = path.GroundedRootY;
            if (reference.VerticalVelocity < 0f) reference.VerticalVelocity = 0f;
            PlayerMotorState candidate = report.PredictedMotor;
            if ((candidate.Position - reference.Position).sqrMagnitude > context.PositionTolerance * context.PositionTolerance
                || (candidate.Velocity - reference.Velocity).sqrMagnitude > context.VelocityTolerance * context.VelocityTolerance
                || Mathf.Abs(candidate.VerticalVelocity - reference.VerticalVelocity) > context.VelocityTolerance
                || Mathf.Abs(Mathf.DeltaAngle(candidate.Yaw, reference.Yaw)) > context.YawTolerance
                || Mathf.Abs(candidate.TurnVelocity - reference.TurnVelocity) > context.TurnTolerance) return Reject(MovementRejectReason.Kinematics);
            // Validate the actual accepted segment too, so endpoint tolerance never bypasses a wall.
            MovementPathResult actual = world.CheckPath(e.StartMotor, candidate);
            if (actual.Kind != MovementPathKind.ClearFlatGround || Mathf.Abs(candidate.Position.y - actual.GroundedRootY) > context.PositionTolerance) return Reject(MovementRejectReason.Collision);
            return new MovementValidationResult(true, MovementRejectReason.None, candidate, reference);
        }
        private static MovementValidationResult Reject(MovementRejectReason reason) => new MovementValidationResult(false, reason, default, default);
    }
}
