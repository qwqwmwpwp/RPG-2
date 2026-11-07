using Mirror;
using RPG4.PlayerMovement;
using RPG4.PlayerIntent;

namespace RPG4.Networking
{
    public static class PlayerMovementSerialization
    {
        public static void WritePlayerMovementReport(this NetworkWriter w, PlayerMovementReport v)
        {
            w.WriteUInt(v.Epoch); w.WriteUInt(v.CorrectionId); w.WriteUInt(v.ConstraintRevision); w.WriteULong(v.Step);
            w.WritePlayerInputPacket(v.Input); w.WriteMovementMotor(v.PredictedMotor); w.WriteBool(v.Aiming); w.WriteBool(v.Strafe);
        }
        public static PlayerMovementReport ReadPlayerMovementReport(this NetworkReader r) => new PlayerMovementReport
        { Epoch = r.ReadUInt(), CorrectionId = r.ReadUInt(), ConstraintRevision = r.ReadUInt(), Step = r.ReadULong(),
            Input = r.ReadPlayerInputPacket(), PredictedMotor = r.ReadMovementMotor(), Aiming = r.ReadBool(), Strafe = r.ReadBool() };
        public static void WritePlayerInputPacket(this NetworkWriter w, PlayerInputPacket v)
        {
            w.WriteULong(v.Sequence); w.WriteULong(v.FireRequestId); w.WriteLong(v.ObservedServerTick);
            w.WriteVector2(v.Move); w.WriteVector2(v.Look); w.WriteBool(v.Crouch); w.WriteBool(v.Aim);
            w.WriteBool(v.FirePressed); w.WriteBool(v.FireHeld); w.WriteBool(v.Reload); w.WriteBool(v.Interact);
        }
        public static PlayerInputPacket ReadPlayerInputPacket(this NetworkReader r) => new PlayerInputPacket
        { Sequence = r.ReadULong(), FireRequestId = r.ReadULong(), ObservedServerTick = r.ReadLong(), Move = r.ReadVector2(),
            Look = r.ReadVector2(), Crouch = r.ReadBool(), Aim = r.ReadBool(), FirePressed = r.ReadBool(), FireHeld = r.ReadBool(), Reload = r.ReadBool(), Interact = r.ReadBool() };
        public static void WriteMovementMotor(this NetworkWriter w, PlayerMotorState v)
        { w.WriteVector3(v.Position); w.WriteVector3(v.Velocity); w.WriteFloat(v.Yaw); w.WriteFloat(v.VerticalVelocity); w.WriteFloat(v.TurnVelocity); w.WriteBool(v.Crouched); }
        public static PlayerMotorState ReadMovementMotor(this NetworkReader r) => new PlayerMotorState
        { Position = r.ReadVector3(), Velocity = r.ReadVector3(), Yaw = r.ReadFloat(), VerticalVelocity = r.ReadFloat(), TurnVelocity = r.ReadFloat(), Crouched = r.ReadBool() };
        public static void WritePlayerMovementAck(this NetworkWriter w, PlayerMovementAck v)
        { w.WriteUInt(v.Epoch); w.WriteUInt(v.CorrectionId); w.WriteUInt(v.ConstraintRevision); w.WriteULong(v.AcceptedStep); w.WriteULong(v.ProcessedInputSequence); w.WriteLong(v.ServerTick); }
        public static PlayerMovementAck ReadPlayerMovementAck(this NetworkReader r) => new PlayerMovementAck
        { Epoch = r.ReadUInt(), CorrectionId = r.ReadUInt(), ConstraintRevision = r.ReadUInt(), AcceptedStep = r.ReadULong(), ProcessedInputSequence = r.ReadULong(), ServerTick = r.ReadLong() };
        public static void WritePlayerMovementBaseline(this NetworkWriter w, PlayerMovementBaseline v)
        { w.WriteUInt(v.Epoch); w.WriteUInt(v.CorrectionId); w.WriteUInt(v.ConstraintRevision); w.WriteULong(v.Step); w.WriteULong(v.ProcessedInputSequence); w.WritePlayerNetworkState(v.State); }
        public static PlayerMovementBaseline ReadPlayerMovementBaseline(this NetworkReader r) => new PlayerMovementBaseline
        { Epoch = r.ReadUInt(), CorrectionId = r.ReadUInt(), ConstraintRevision = r.ReadUInt(), Step = r.ReadULong(), ProcessedInputSequence = r.ReadULong(), State = r.ReadPlayerNetworkState() };
        public static void WritePlayerMovementCorrection(this NetworkWriter w, PlayerMovementCorrection v)
        { w.WritePlayerMovementBaseline(v.Baseline); w.WriteByte((byte)v.Reason); }
        public static PlayerMovementCorrection ReadPlayerMovementCorrection(this NetworkReader r) => new PlayerMovementCorrection
        { Baseline = r.ReadPlayerMovementBaseline(), Reason = (MovementRejectReason)r.ReadByte() };
        public static void WritePlayerAuthorityState(this NetworkWriter w, PlayerAuthorityState v)
        {
            w.WriteUInt(v.Revision); w.WriteUInt(v.ConstraintRevision); PlayerAuthoritySnapshot s = v.Snapshot;
            w.WriteLong(s.Tick); w.WriteDouble(s.Time); w.WriteDouble(s.HurtUntil); w.WriteDouble(s.ShotTime);
            w.WriteFloat(s.Health); w.WriteFloat(s.MaxHealth); w.WriteFloat(s.ReloadPhase); w.WriteFloat(s.ShotDuration); w.WriteFloat(s.FadeDuration); w.WriteFloat(s.ReloadDuration);
            w.WriteBool(s.Alive); w.WriteBool(s.WeaponAvailable); w.WriteBool(s.Reloading); w.WriteULong(s.WeaponId); w.WriteULong(s.ShotSequence);
            WeaponActionSnapshot a = s.Action;
            w.WriteBool(a.ShotStarted); w.WriteBool(a.FadeStarted); w.WriteBool(a.ShotActive); w.WriteBool(a.FadingOut); w.WriteBool(a.IsAutomatic); w.WriteFloat(a.ShotPhase);
            w.WriteConsumableAction(s.Consumable);
        }
        public static PlayerAuthorityState ReadPlayerAuthorityState(this NetworkReader r)
        {
            var v = new PlayerAuthorityState { Revision = r.ReadUInt(), ConstraintRevision = r.ReadUInt() };
            v.Snapshot = new PlayerAuthoritySnapshot { Tick = r.ReadLong(), Time = r.ReadDouble(), HurtUntil = r.ReadDouble(), ShotTime = r.ReadDouble(),
                Health = r.ReadFloat(), MaxHealth = r.ReadFloat(), ReloadPhase = r.ReadFloat(), ShotDuration = r.ReadFloat(), FadeDuration = r.ReadFloat(), ReloadDuration = r.ReadFloat(),
                Alive = r.ReadBool(), WeaponAvailable = r.ReadBool(), Reloading = r.ReadBool(), WeaponId = r.ReadULong(), ShotSequence = r.ReadULong(),
                Action = new WeaponActionSnapshot(r.ReadBool(), r.ReadBool(), r.ReadBool(), r.ReadBool(), r.ReadBool(), r.ReadFloat()) };
            v.Snapshot.Consumable = r.ReadConsumableAction();
            return v;
        }
    }
}
