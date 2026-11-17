using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace RPG4.CharacterAnimation
{
    [Serializable]
    public struct PlayerSupportHandTargetData : IAnimationJobData
    {
        public Transform carrier, target;
        [SyncSceneToStream] public Vector3 localPosition;
        [SyncSceneToStream] public Quaternion localRotation;
        [SyncSceneToStream] public Vector3 carrierScale;
        [SyncSceneToStream, Range(0f, 1f)] public float activeWeight;
        public bool IsValid() => carrier != null && target != null && carrier != target;
        public void SetDefaultValues()
        {
            carrier = target = null; localPosition = Vector3.zero; localRotation = Quaternion.identity;
            carrierScale = Vector3.one; activeWeight = 0f;
        }
    }

    public struct PlayerSupportHandTargetJob : IWeightedAnimationJob
    {
        public ReadOnlyTransformHandle carrier;
        public ReadWriteTransformHandle target;
        public Vector3Property localPosition, carrierScale;
        public Vector4Property localRotation;
        public FloatProperty activeWeight;
        public FloatProperty jobWeight { get; set; }
        public void ProcessRootMotion(AnimationStream stream) { }
        public void ProcessAnimation(AnimationStream stream)
        {
            if (activeWeight.Get(stream) * jobWeight.Get(stream) <= 0f)
            { AnimationRuntimeUtils.PassThrough(stream, target); return; }
            Vector4 q = localRotation.Get(stream);
            Quaternion rotation = new Quaternion(q.x, q.y, q.z, q.w);
            // The Weapon layer has already solved the carrier for this animation evaluation.
            // Reconstruct the grip here, before TwoBoneIK, rather than copying last frame's scene position.
            Quaternion carrierRotation = carrier.GetRotation(stream);
            target.SetPosition(stream, carrier.GetPosition(stream)
                + carrierRotation * Vector3.Scale(localPosition.Get(stream), carrierScale.Get(stream)));
            target.SetRotation(stream, carrierRotation * rotation);
        }
    }

    public sealed class PlayerSupportHandTargetBinder : AnimationJobBinder<PlayerSupportHandTargetJob, PlayerSupportHandTargetData>
    {
        public override PlayerSupportHandTargetJob Create(Animator animator, ref PlayerSupportHandTargetData data, Component component)
            => new PlayerSupportHandTargetJob
            {
                carrier = ReadOnlyTransformHandle.Bind(animator, data.carrier),
                target = ReadWriteTransformHandle.Bind(animator, data.target),
                localPosition = Vector3Property.Bind(animator, component, Property(nameof(data.localPosition))),
                localRotation = Vector4Property.Bind(animator, component, Property(nameof(data.localRotation))),
                carrierScale = Vector3Property.Bind(animator, component, Property(nameof(data.carrierScale))),
                activeWeight = FloatProperty.Bind(animator, component, Property(nameof(data.activeWeight)))
            };
        private static string Property(string field) => ConstraintsUtils.ConstructConstraintDataPropertyName(field);
        public override void Destroy(PlayerSupportHandTargetJob job) { }
    }

    /// <summary>Equipment carrier-local target reconstruction immediately before the existing left-hand solve.</summary>
    [DisallowMultipleComponent, AddComponentMenu("RPG4/Player Support Hand Target Constraint")]
    public sealed class PlayerSupportHandTargetConstraint
        : RigConstraint<PlayerSupportHandTargetJob, PlayerSupportHandTargetData, PlayerSupportHandTargetBinder> { }
}
