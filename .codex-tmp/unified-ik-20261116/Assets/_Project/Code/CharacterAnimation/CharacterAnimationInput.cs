using System;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Semantic identity after the caller has reconciled predicted and confirmed actions.</summary>
    public readonly struct CharacterActionIdentity : IEquatable<CharacterActionIdentity>
    {
        public readonly ulong Subject, Sequence;
        public readonly uint Scope;
        public CharacterActionIdentity(ulong subject, ulong sequence, uint scope = 0)
        { Subject = subject; Sequence = sequence; Scope = scope; }
        public bool Equals(CharacterActionIdentity other) => Subject == other.Subject && Sequence == other.Sequence && Scope == other.Scope;
        public override bool Equals(object obj) => obj is CharacterActionIdentity other && Equals(other);
        public override int GetHashCode() => unchecked((Subject.GetHashCode() * 397 ^ Sequence.GetHashCode()) * 397 ^ (int)Scope);
    }

    public readonly struct CharacterActionState
    {
        public readonly CharacterActionIdentity Identity;
        public readonly bool Active, FadingOut, Automatic;
        public readonly double StartedAt;
        public readonly float Duration, Phase, FadeDuration;
        public CharacterActionState(CharacterActionIdentity identity, bool active, double startedAt,
            float duration, float phase, bool fadingOut = false, bool automatic = false, float fadeDuration = 0f)
        {
            Identity = identity; Active = active; StartedAt = startedAt;
            Duration = Mathf.Max(0.01f, duration); Phase = Mathf.Clamp01(phase);
            FadingOut = fadingOut; Automatic = automatic; FadeDuration = Mathf.Max(0f, fadeDuration);
        }
        internal bool ValidAt(double time) => !double.IsNaN(StartedAt) && !double.IsInfinity(StartedAt)
            && !float.IsNaN(Duration) && !float.IsInfinity(Duration) && StartedAt <= time + 0.05d;
        internal float PhaseAt(double time) => Mathf.Clamp01(Mathf.Max(Phase, (float)((time - StartedAt) / Mathf.Max(0.01f, Duration))));
    }

    /// <summary>Value data only: no entity, input, network, animation-resource or component references.</summary>
    public struct CharacterAnimationState
    {
        public double Time;
        public bool Active, Alive, CanMove, CanLook, CanUseWeapon, Crouched, Strafe, Aiming;
        public bool HasMoveInput, ImmediateView, Preview, HasEquipment, HasGripPose;
        public Vector3 PlanarVelocity, GripPosition;
        public Quaternion GripRotation;
        public float BodyYaw, ViewYaw, ViewPitch, RenderYawOffset;
        public CharacterActionState Shot, Reload, Consumable, Hurt;
    }

    public enum CharacterAnimationEventKind { Reset, RebaseYaw, WeaponCancelled, Revived, MotionDiscontinuity }

    public readonly struct CharacterAnimationEvent
    {
        public readonly ulong Sequence;
        public readonly double Time;
        public readonly CharacterAnimationEventKind Kind;
        public readonly float Value;
        public CharacterAnimationEvent(ulong sequence, double time, CharacterAnimationEventKind kind, float value = 0f)
        { Sequence = sequence; Time = time; Kind = kind; Value = value; }
    }

    [Flags]
    internal enum AnimationFacts
    {
        None = 0, Active = 1, Alive = 2, Dead = 4, CanMove = 8, CanLook = 16,
        CanUseWeapon = 32, Crouched = 64, Strafe = 128, Aiming = 256,
        Shooting = 512, ShotFade = 1024, Automatic = 2048, Reloading = 4096,
        Consumable = 8192, Hurt = 16384, TurnLeft = 32768, TurnRight = 65536,
        Preview = 131072, HasEquipment = 262144
    }

    internal readonly struct AnimationPoseTarget
    {
        internal readonly bool Enabled, FollowView, Reloading, ImmediateView, BodyEnabled;
        internal readonly float ViewYaw, Pitch, YawOffset, WaistPitchOffset, SupportHandWeight, LayerWeight, PoseWeight;
        internal AnimationPoseTarget(bool enabled, bool followView, bool reloading, float yaw, float pitch,
            float yawOffset, float waistPitchOffset, float supportHandWeight, float layerWeight,
            bool immediateView = false, bool bodyEnabled = true, float poseWeight = 1f)
        {
            Enabled = enabled; FollowView = followView; Reloading = reloading; ViewYaw = yaw; Pitch = pitch;
            YawOffset = yawOffset; WaistPitchOffset = waistPitchOffset;
            SupportHandWeight = supportHandWeight; LayerWeight = layerWeight;
            ImmediateView = immediateView; BodyEnabled = bodyEnabled; PoseWeight = poseWeight;
        }
    }
}
