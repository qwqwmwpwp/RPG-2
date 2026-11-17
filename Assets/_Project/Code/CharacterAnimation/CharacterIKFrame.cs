using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Solved immutable execution parameters; downstream components perform no policy or smoothing.</summary>
    public readonly struct CharacterIKFrame
    {
        public readonly uint Version, ResetVersion, EquipmentVersion;
        public readonly int RenderFrame, SourceFrame;
        public readonly ulong EquipmentIdentity;
        public readonly bool BodyValid, AimValid, ShotReferenceValid, WeaponValid, SupportHandValid;
        public readonly float ViewYaw, BodyYaw, RenderYawOffset, Pitch;
        public readonly float BodyWeight, FollowWeight, AimWeight, ShotReferenceWeight, SupportHandWeight;
        public readonly float YawOffset, WaistPitchOffset, WaistFraction, YawLimit;
        public readonly Vector3 SocketWeights, GripCarrierLocalPosition;
        public readonly Quaternion GripCarrierLocalRotation;

        internal CharacterIKFrame(uint version, uint resetVersion, in CharacterIKIntent intent, float pitch,
            int renderFrame, float waistFraction, float yawLimit, CharacterIKModules modules)
        {
            Version = version; ResetVersion = resetVersion; RenderFrame = renderFrame; SourceFrame = intent.SourceFrame;
            EquipmentIdentity = intent.EquipmentIdentity; EquipmentVersion = intent.EquipmentVersion;
            ViewYaw = intent.ViewYaw; BodyYaw = intent.BodyYaw; RenderYawOffset = intent.RenderYawOffset; Pitch = pitch;
            BodyValid = (modules & CharacterIKModules.BodyAim) != 0 && intent.BodyValid && intent.BodyWeight > 0f;
            AimValid = (modules & CharacterIKModules.BodyAim) != 0 && intent.AimValid && intent.AimWeight > 0f;
            ShotReferenceValid = (modules & CharacterIKModules.ShotPose) != 0 && intent.ShotReferenceValid && intent.ShotReferenceWeight > 0f;
            WeaponValid = (modules & CharacterIKModules.Weapon) != 0 && intent.WeaponValid;
            SupportHandValid = (modules & CharacterIKModules.SupportHand) != 0 && intent.SupportHandValid && intent.SupportHandWeight > 0f;
            BodyWeight = BodyValid ? Mathf.Clamp01(intent.BodyWeight) : 0f;
            FollowWeight = AimValid ? Mathf.Clamp01(intent.FollowWeight) : 0f;
            AimWeight = AimValid ? Mathf.Clamp01(intent.AimWeight) : 0f;
            ShotReferenceWeight = ShotReferenceValid ? Mathf.Clamp01(intent.ShotReferenceWeight) : 0f;
            SupportHandWeight = SupportHandValid ? Mathf.Clamp01(intent.SupportHandWeight) : 0f;
            YawOffset = intent.YawOffset; WaistPitchOffset = intent.WaistPitchOffset; WaistFraction = waistFraction; YawLimit = yawLimit;
            Vector3 sockets = new Vector3(Mathf.Max(0f, intent.SocketWeights.x), Mathf.Max(0f, intent.SocketWeights.y), Mathf.Max(0f, intent.SocketWeights.z));
            float largest = Mathf.Max(sockets.x, Mathf.Max(sockets.y, sockets.z));
            if (largest > 0f) sockets /= largest;
            float total = sockets.x + sockets.y + sockets.z;
            SocketWeights = WeaponValid && total > 0f ? sockets / total : Vector3.right;
            GripCarrierLocalPosition = SupportHandValid ? intent.GripCarrierLocalPosition : Vector3.zero;
            GripCarrierLocalRotation = SupportHandValid ? Quaternion.Normalize(intent.GripCarrierLocalRotation) : Quaternion.identity;
        }
    }
}
