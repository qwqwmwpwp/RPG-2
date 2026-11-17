using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Independent constraint input. External animation or procedural pose providers can supply this directly.</summary>
    public struct CharacterIKState
    {
        public bool Active, Alive, CanLook, Strafe, ImmediateView, HasEquipment, HasGripPose;
        public float ViewYaw, ViewPitch, RenderYawOffset;
        public ulong EquipmentIdentity;
        public uint EquipmentVersion;
        public Vector3 GripCarrierLocalPosition;
        public Quaternion GripCarrierLocalRotation;
    }
}
