using System;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Numerical intent solver. Only Commit advances pitch/equipment continuity.</summary>
    public sealed class CharacterIKSystem
    {
        private readonly CharacterIKProfile profile;
        private int preparedFrame = -1, committedFrame = -1;
        private uint version, resetVersion, continuityVersion;
        private ulong equipmentIdentity;
        private uint equipmentVersion;
        private bool hasContinuity, wasPosing, nextPosing;
        private float pitch, pitchVelocity, nextPitch, nextPitchVelocity;
        private CharacterIKIntent pendingIntent;
        public CharacterIKFrame Frame { get; private set; }

        public CharacterIKSystem(CharacterIKProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            string error = profile.ValidateConfiguration();
            if (error != null) throw new ArgumentException(error, nameof(profile));
            this.profile = profile;
        }
        /// <summary>Stages a solved frame once. Repeated calls cannot advance smoothing or replace the frozen input.</summary>
        public CharacterIKFrame Prepare(in CharacterIKIntent intent, float deltaTime, int renderFrame)
        {
            if (renderFrame <= preparedFrame || preparedFrame > committedFrame) return Frame;
            ValidateIntent(intent, deltaTime, renderFrame);
            preparedFrame = renderFrame; pendingIntent = intent;
            float step = Mathf.Max(0f, deltaTime);
            bool reset = !hasContinuity || continuityVersion != intent.ContinuityVersion
                || equipmentIdentity != intent.EquipmentIdentity || equipmentVersion != intent.EquipmentVersion;
            bool posing = (profile.Modules & CharacterIKModules.BodyAim) != 0 && intent.AimValid && intent.AimWeight > 0f;
            nextPosing = posing; nextPitchVelocity = reset ? 0f : pitchVelocity;
            if (!posing) nextPitch = nextPitchVelocity = 0f;
            else
            {
                float desired = Mathf.Clamp(intent.Pitch, profile.pitchRange.x, profile.pitchRange.y);
                float start = reset ? 0f : pitch;
                nextPitch = intent.ImmediateView || profile.pitchSmoothTime <= 0f || reset || !wasPosing ? desired
                    : step > 0f ? Mathf.SmoothDamp(start, desired, ref nextPitchVelocity,
                        profile.pitchSmoothTime, Mathf.Infinity, step) : start;
                nextPitch = Mathf.Clamp(nextPitch, profile.pitchRange.x, profile.pitchRange.y);
            }
            Frame = new CharacterIKFrame(version + 1, resetVersion + (reset ? 1u : 0u), intent, nextPitch,
                renderFrame, profile.waistFraction, profile.yawLimit, profile.Modules);
            return Frame;
        }
        public void Commit(int renderFrame)
        {
            if (renderFrame != preparedFrame || committedFrame == renderFrame) return;
            pitch = nextPitch; pitchVelocity = nextPitchVelocity; wasPosing = nextPosing;
            equipmentIdentity = pendingIntent.EquipmentIdentity; equipmentVersion = pendingIntent.EquipmentVersion;
            continuityVersion = pendingIntent.ContinuityVersion; hasContinuity = true;
            version = Frame.Version; resetVersion = Frame.ResetVersion; committedFrame = renderFrame;
        }
        /// <summary>Invalidate future continuity without changing the current solved frame.</summary>
        public void ResetContinuity() { hasContinuity = false; }
        public void Reset()
        {
            hasContinuity = wasPosing = nextPosing = false;
            pitch = pitchVelocity = nextPitch = nextPitchVelocity = 0f;
            pendingIntent = default; Frame = default; resetVersion++;
            preparedFrame = committedFrame = -1;
        }
        private static void ValidateIntent(in CharacterIKIntent intent, float deltaTime, int renderFrame)
        {
            if (intent.RenderFrame != renderFrame) throw new ArgumentException("IK intent belongs to another render frame.", nameof(intent));
            if (!Finite(deltaTime) || !Finite(intent.ViewYaw) || !Finite(intent.BodyYaw) || !Finite(intent.RenderYawOffset)
                || !Finite(intent.Pitch) || !Finite(intent.YawOffset) || !Finite(intent.WaistPitchOffset)
                || !Finite(intent.BodyWeight) || !Finite(intent.FollowWeight) || !Finite(intent.AimWeight)
                || !Finite(intent.ShotReferenceWeight) || !Finite(intent.SupportHandWeight) || !Finite(intent.SocketWeights))
                throw new ArgumentException("IK intent and delta time must contain finite solver values.", nameof(intent));
            if (!intent.SupportHandValid) return;
            var rotation = intent.GripCarrierLocalRotation;
            float magnitude = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            if (!Finite(intent.GripCarrierLocalPosition) || !Finite(magnitude) || magnitude < 0.000001f)
                throw new ArgumentException("Support-hand intent requires a finite grip position and nonzero rotation.", nameof(intent));
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
