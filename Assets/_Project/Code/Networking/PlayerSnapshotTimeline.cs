using System.Collections.Generic;
using Mirror;
using RPG4.PlayerIntent;
using RPG4.PlayerMovement;
using UnityEngine;

namespace RPG4.Networking
{
    /// <summary>Interpolation in the server simulation clock; wall time only measures local delivery.</summary>
    internal sealed class PlayerSnapshotTimeline
    {
        private struct Entry : Snapshot
        {
            public double remoteTime { get; set; }
            public double localTime { get; set; }
            internal PlayerNetworkState State;
        }
        private readonly SortedList<double, Entry> buffer = new SortedList<double, Entry>(32);
        private double timeline, timescale = 1d;
        private ExponentialMovingAverage drift = new ExponentialMovingAverage(50);
        private ExponentialMovingAverage delivery = new ExponentialMovingAverage(50);
        internal int Count => buffer.Count;
        internal double SampleTime { get; private set; }

        internal void Insert(PlayerNetworkState state, float interval, float delay)
        {
            SnapshotInterpolation.InsertAndAdjust(buffer, 32,
                new Entry { remoteTime = state.Time, localTime = Time.realtimeSinceStartupAsDouble, State = state },
                ref timeline, ref timescale, interval, delay, 0.02d, 0.04d,
                ref drift, -1f, 1f, ref delivery);
        }

        internal PlayerNetworkState Step(float deltaTime)
        {
            SnapshotInterpolation.StepTime(deltaTime, ref timeline, timescale);
            SnapshotInterpolation.StepInterpolation(buffer, timeline, out Entry from, out Entry to, out double ratio);
            float t = (float)ratio;
            PlayerNetworkState a = from.State, b = to.State;
            PlayerNetworkState result = t < 1f ? a : b;
            PlayerMotorState motor = result.Motor;
            motor.Position = Vector3.Lerp(a.Motor.Position, b.Motor.Position, t);
            motor.Velocity = Vector3.Lerp(a.Motor.Velocity, b.Motor.Velocity, t);
            motor.Yaw = Mathf.LerpAngle(a.Motor.Yaw, b.Motor.Yaw, t);
            result.Motor = motor;
            result.ViewYaw = Mathf.LerpAngle(a.ViewYaw, b.ViewYaw, t);
            result.ViewPitch = Mathf.Lerp(a.ViewPitch, b.ViewPitch, t);
            result.Time = a.Time + (b.Time - a.Time) * t;
            if (a.WeaponId == b.WeaponId)
            {
                if (a.Reloading && b.Reloading)
                    result.ReloadPhase = Mathf.Lerp(a.ReloadPhase, b.ReloadPhase, t);
                if (a.ShotSequence == b.ShotSequence && a.Action.ShotActive && b.Action.ShotActive)
                {
                    WeaponActionSnapshot action = result.Action;
                    result.Action = new WeaponActionSnapshot(action.ShotStarted, action.FadeStarted,
                        action.ShotActive, action.FadingOut, action.IsAutomatic,
                        Mathf.Lerp(a.Action.ShotPhase, b.Action.ShotPhase, t));
                }
            }
            SampleTime = result.Time;
            return result;
        }

        internal void Clear()
        {
            buffer.Clear(); timeline = SampleTime = 0d; timescale = 1d;
            drift = new ExponentialMovingAverage(50);
            delivery = new ExponentialMovingAverage(50);
        }
    }
}
