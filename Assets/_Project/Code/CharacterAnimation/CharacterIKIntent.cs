using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Explicit immutable solver input. It contains no animation actions, phases or policy tables.</summary>
    public readonly struct CharacterIKIntent
    {
        public readonly int RenderFrame, SourceFrame;
        public readonly uint ContinuityVersion, EquipmentVersion;
        public readonly ulong EquipmentIdentity;
        public readonly bool ImmediateView, BodyValid, AimValid, ShotReferenceValid, WeaponValid, SupportHandValid;
        public readonly float ViewYaw, BodyYaw, RenderYawOffset, Pitch;
        public readonly float BodyWeight, FollowWeight, AimWeight, ShotReferenceWeight, SupportHandWeight;
        public readonly float YawOffset, WaistPitchOffset;
        public readonly Vector3 SocketWeights, GripCarrierLocalPosition;
        public readonly Quaternion GripCarrierLocalRotation;

        public CharacterIKIntent(int renderFrame, int sourceFrame, uint continuityVersion, ulong equipmentIdentity, uint equipmentVersion,
            bool immediateView, bool bodyValid, bool aimValid, bool shotReferenceValid, bool weaponValid, bool supportHandValid,
            float viewYaw, float bodyYaw, float renderYawOffset, float pitch, float bodyWeight, float followWeight,
            float aimWeight, float shotReferenceWeight, float supportHandWeight, float yawOffset, float waistPitchOffset,
            Vector3 socketWeights, Vector3 gripCarrierLocalPosition, Quaternion gripCarrierLocalRotation)
        {
            RenderFrame = renderFrame; SourceFrame = sourceFrame; ContinuityVersion = continuityVersion; EquipmentIdentity = equipmentIdentity; EquipmentVersion = equipmentVersion;
            ImmediateView = immediateView; BodyValid = bodyValid; AimValid = aimValid; ShotReferenceValid = shotReferenceValid;
            WeaponValid = weaponValid; SupportHandValid = supportHandValid;
            ViewYaw = viewYaw; BodyYaw = bodyYaw; RenderYawOffset = renderYawOffset; Pitch = pitch;
            BodyWeight = bodyWeight; FollowWeight = followWeight; AimWeight = aimWeight; ShotReferenceWeight = shotReferenceWeight;
            SupportHandWeight = supportHandWeight; YawOffset = yawOffset; WaistPitchOffset = waistPitchOffset;
            SocketWeights = socketWeights; GripCarrierLocalPosition = gripCarrierLocalPosition; GripCarrierLocalRotation = gripCarrierLocalRotation;
        }
    }
}
