using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Complete immutable execution contract; positions are local to the equipment carrier, never cached world targets.</summary>
    public readonly struct CharacterIKFrame
    {
        public readonly uint Version, ResetVersion, EquipmentVersion;
        public readonly int RenderFrame;
        public readonly float DeltaTime;
        public readonly ulong EquipmentIdentity;
        public readonly CharacterActionIdentity ActionIdentity;
        public readonly bool ImmediateView, BodyValid, AimValid, ShotReferenceValid, WeaponValid, SupportHandValid;
        public readonly float ViewYaw, BodyYaw, RenderYawOffset, Pitch;
        public readonly float BodyWeight, FollowWeight, AimWeight, ShotReferenceWeight, SupportHandWeight;
        public readonly Vector2 PitchRange;
        public readonly float PitchSmoothTime;
        public readonly float YawOffset, WaistPitchOffset, WaistFraction, YawLimit;
        public readonly Vector3 SocketWeights, GripCarrierLocalPosition;
        public readonly Quaternion GripCarrierLocalRotation;

        internal CharacterIKFrame(uint version, uint resetVersion, in CharacterIKState state, CharacterActionIdentity actionIdentity,
            float bodyYaw, float deltaTime, int renderFrame, float bodyWeight, float followWeight, float aimWeight, float referenceWeight,
            float supportWeight, float yawOffset, float waistOffset, float waistFraction, float yawLimit,
            Vector2 pitchRange, float pitchSmoothTime, Vector3 sockets, CharacterIKModules modules)
        {
            Version = version; ResetVersion = resetVersion; ImmediateView = state.ImmediateView;
            DeltaTime = deltaTime; RenderFrame = renderFrame;
            PitchRange = pitchRange; PitchSmoothTime = pitchSmoothTime; EquipmentIdentity = state.EquipmentIdentity; EquipmentVersion = state.EquipmentVersion;
            ActionIdentity = actionIdentity; ViewYaw = state.ViewYaw; BodyYaw = bodyYaw; RenderYawOffset = state.RenderYawOffset; Pitch = state.ViewPitch;
            BodyValid = (modules & CharacterIKModules.BodyAim) != 0 && bodyWeight > 0f;
            AimValid = (modules & CharacterIKModules.BodyAim) != 0 && aimWeight > 0f;
            ShotReferenceValid = (modules & CharacterIKModules.ShotPose) != 0 && referenceWeight > 0f;
            WeaponValid = (modules & CharacterIKModules.Weapon) != 0 && state.Active && state.Alive && state.HasEquipment;
            SupportHandValid = (modules & CharacterIKModules.SupportHand) != 0 && state.Active && state.Alive
                && state.HasEquipment && state.HasGripPose && supportWeight > 0f;
            BodyWeight = bodyWeight; FollowWeight = followWeight; AimWeight = aimWeight; ShotReferenceWeight = referenceWeight;
            SupportHandWeight = SupportHandValid ? supportWeight : 0f;
            YawOffset = yawOffset; WaistPitchOffset = waistOffset; WaistFraction = waistFraction; YawLimit = yawLimit;
            SocketWeights = WeaponValid ? sockets : Vector3.right;
            GripCarrierLocalPosition = SupportHandValid ? state.GripCarrierLocalPosition : Vector3.zero;
            GripCarrierLocalRotation = SupportHandValid ? state.GripCarrierLocalRotation : Quaternion.identity;
        }
    }
}
