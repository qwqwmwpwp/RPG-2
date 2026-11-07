using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace RPG4.PlayerMovement
{
    [Serializable]
    public struct PlayerShotPoseData : IAnimationJobData
    {
        public Transform animatorRoot;
        public Transform reference;
        public Transform spine;
        [SyncSceneToStream, Range(0f, 1f)] public float activeWeight;

        public bool IsValid() => animatorRoot != null && reference != null && spine != null
            && reference.parent == animatorRoot && spine.IsChildOf(animatorRoot);

        public void SetDefaultValues()
        {
            animatorRoot = reference = spine = null;
            activeWeight = 0f;
        }
    }

    public struct PlayerShotPoseJob : IWeightedAnimationJob
    {
        public ReadOnlyTransformHandle animatorRoot, reference;
        public ReadWriteTransformHandle spine;
        public FloatProperty activeWeight;
        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float weight = Mathf.Clamp01(activeWeight.Get(stream)) * jobWeight.Get(stream);
            if (weight <= 0f)
            {
                AnimationRuntimeUtils.PassThrough(stream, spine);
                return;
            }
            Vector3 scale = reference.GetLocalScale(stream);
            weight *= Mathf.Clamp01(scale.x);
            Vector3 xyz = reference.GetLocalPosition(stream);
            float normSquared = xyz.sqrMagnitude + scale.y * scale.y;
            if (weight <= 0f || normSquared < 1e-10f)
            {
                AnimationRuntimeUtils.PassThrough(stream, spine);
                return;
            }

            // Normalize the blended float channels so partial reference influence
            // does not pull the target orientation toward an identity quaternion.
            float inverseNorm = 1f / Mathf.Sqrt(normSquared);
            Quaternion localTarget = new Quaternion(xyz.x * inverseNorm, xyz.y * inverseNorm,
                xyz.z * inverseNorm, scale.y * inverseNorm);
            Quaternion target = animatorRoot.GetRotation(stream) * localTarget;
            spine.SetRotation(stream, Quaternion.Slerp(spine.GetRotation(stream), target, weight));
        }
    }

    public sealed class PlayerShotPoseBinder : AnimationJobBinder<PlayerShotPoseJob, PlayerShotPoseData>
    {
        public override PlayerShotPoseJob Create(Animator animator, ref PlayerShotPoseData data,
            Component component) => new PlayerShotPoseJob
        {
            animatorRoot = ReadOnlyTransformHandle.Bind(animator, data.animatorRoot),
            reference = ReadOnlyTransformHandle.Bind(animator, data.reference),
            spine = ReadWriteTransformHandle.Bind(animator, data.spine),
            activeWeight = FloatProperty.Bind(animator, component,
                ConstraintsUtils.ConstructConstraintDataPropertyName(nameof(data.activeWeight)))
        };

        public override void Destroy(PlayerShotPoseJob job) { }
    }

    /// <summary>Restore source shooting posture before BodyAim while preserving base-layer hips and legs.</summary>
    [DisallowMultipleComponent, AddComponentMenu("RPG4/Player Shot Pose Constraint")]
    public sealed class PlayerShotPoseConstraint
        : RigConstraint<PlayerShotPoseJob, PlayerShotPoseData, PlayerShotPoseBinder> { }
}

