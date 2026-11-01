using Mirror;
using RPG4.PlayerMovement;
using RPG4.PlayerIntent;

namespace RPG4.Networking
{
    // Intent value types have readonly fields; explicit serializers preserve their constructor contract.
    public static class PlayerStateSerialization
    {
        public static void WritePlayerNetworkState(this NetworkWriter w, PlayerNetworkState s)
        {
            w.WriteLong(s.Tick); w.WriteDouble(s.Time); w.WriteDouble(s.HurtUntil); w.WriteDouble(s.ShotTime);
            w.WriteVector3(s.Motor.Position); w.WriteVector3(s.Motor.Velocity); w.WriteFloat(s.Motor.Yaw);
            w.WriteFloat(s.Motor.VerticalVelocity); w.WriteFloat(s.Motor.TurnVelocity); w.WriteBool(s.Motor.Crouched);
            w.WriteFloat(s.ViewYaw); w.WriteFloat(s.ViewPitch); w.WriteFloat(s.Health); w.WriteFloat(s.MaxHealth);
            w.WriteFloat(s.ReloadPhase); w.WriteFloat(s.ShotDuration); w.WriteFloat(s.FadeDuration); w.WriteFloat(s.ReloadDuration);
            w.WriteBool(s.Alive); w.WriteBool(s.Aiming); w.WriteBool(s.Reloading); w.WriteBool(s.CanStand); w.WriteBool(s.WeaponAvailable);
            w.WriteULong(s.WeaponId); w.WriteULong(s.ShotSequence);
            LocomotionPlan p = s.Plan;
            w.WriteBool(p.CanMove); w.WriteBool(p.Crouched); w.WriteBool(p.Strafe); w.WriteVector2(p.Move);
            w.WriteFloat(p.ViewYaw); w.WriteFloat(p.MoveSpeed); w.WriteFloat(p.AnimationSpeed);
            UpperBodyDemand d = s.Demand;
            w.WriteBool(d.Strafe); w.WriteBool(d.AimCamera); w.WriteBool(d.ShotPose); w.WriteBool(d.Reloading);
            w.WriteFloat(d.MoveSpeed); w.WriteFloat(d.AnimationSpeed); w.WriteBool(d.AttackCamera);
            WeaponActionSnapshot a = s.Action;
            w.WriteBool(a.ShotStarted); w.WriteBool(a.FadeStarted); w.WriteBool(a.ShotActive); w.WriteBool(a.FadingOut);
            w.WriteBool(a.IsAutomatic); w.WriteFloat(a.ShotPhase);
        }
        public static PlayerNetworkState ReadPlayerNetworkState(this NetworkReader r)
        {
            var s = new PlayerNetworkState
            {
                Tick = r.ReadLong(), Time = r.ReadDouble(), HurtUntil = r.ReadDouble(), ShotTime = r.ReadDouble(),
                Motor = new PlayerMotorState { Position = r.ReadVector3(), Velocity = r.ReadVector3(), Yaw = r.ReadFloat(),
                    VerticalVelocity = r.ReadFloat(), TurnVelocity = r.ReadFloat(), Crouched = r.ReadBool() },
                ViewYaw = r.ReadFloat(), ViewPitch = r.ReadFloat(), Health = r.ReadFloat(), MaxHealth = r.ReadFloat(),
                ReloadPhase = r.ReadFloat(), ShotDuration = r.ReadFloat(), FadeDuration = r.ReadFloat(), ReloadDuration = r.ReadFloat(),
                Alive = r.ReadBool(), Aiming = r.ReadBool(), Reloading = r.ReadBool(), CanStand = r.ReadBool(), WeaponAvailable = r.ReadBool(),
                WeaponId = r.ReadULong(), ShotSequence = r.ReadULong()
            };
            s.Plan = new LocomotionPlan(r.ReadBool(), r.ReadBool(), r.ReadBool(), r.ReadVector2(),
                r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
            s.Demand = new UpperBodyDemand(r.ReadBool(), r.ReadBool(), r.ReadBool(), r.ReadBool(),
                r.ReadFloat(), r.ReadFloat(), r.ReadBool());
            s.Action = new WeaponActionSnapshot(r.ReadBool(), r.ReadBool(), r.ReadBool(), r.ReadBool(), r.ReadBool(), r.ReadFloat());
            return s;
        }
    }
}
