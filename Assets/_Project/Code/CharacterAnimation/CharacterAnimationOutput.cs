using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Immutable semantics of a plan actually presented, before any IK capability or graph result is known.</summary>
    public sealed class CharacterAnimationOutput
    {
        public int RenderFrame { get; }
        public int SourceFrame { get; }
        public uint ContinuityVersion { get; }
        public ulong EquipmentIdentity { get; }
        public uint EquipmentVersion { get; }
        public bool Active { get; }
        public bool Alive { get; }
        public bool CanLook { get; }
        public bool HasEquipment { get; }
        public bool HasGripPose { get; }
        public bool ImmediateView { get; }
        public float ViewYaw { get; }
        public float ViewPitch { get; }
        public float BodyYaw { get; }
        public float RenderYawOffset { get; }
        public float BodyWeight { get; }
        public float AimWeight { get; }
        public float FollowWeight { get; }
        public float YawOffset { get; }
        public bool BodyOrientationEnabled { get; }
        public float LocomotionYawOffset => BodyOrientationEnabled ? YawOffset * FollowWeight : 0f;
        public Vector3 GripCarrierLocalPosition { get; }
        public Quaternion GripCarrierLocalRotation { get; }
        public IReadOnlyList<CharacterActionContribution> Actions { get; }
        internal CharacterAnimationOutput(CharacterAnimationPlan plan, int renderFrame,
            float bodyWeight, float aimWeight, float followWeight, float yawOffset, bool bodyOrientationEnabled)
        {
            var state = plan.State;
            RenderFrame = renderFrame; SourceFrame = plan.FrameId; ContinuityVersion = plan.ContinuityVersion;
            EquipmentIdentity = state.EquipmentIdentity; EquipmentVersion = state.EquipmentVersion;
            Active = state.Active; Alive = state.Alive; CanLook = state.CanLook;
            HasEquipment = state.HasEquipment; HasGripPose = state.HasGripPose; ImmediateView = state.ImmediateView;
            ViewYaw = state.ViewYaw; ViewPitch = state.ViewPitch; BodyYaw = plan.BodyYaw; RenderYawOffset = state.RenderYawOffset;
            BodyWeight = bodyWeight; AimWeight = aimWeight; FollowWeight = followWeight; YawOffset = yawOffset;
            BodyOrientationEnabled = bodyOrientationEnabled;
            GripCarrierLocalPosition = state.GripCarrierLocalPosition; GripCarrierLocalRotation = state.GripCarrierLocalRotation;
            Actions = plan.Actions;
        }
    }
}
