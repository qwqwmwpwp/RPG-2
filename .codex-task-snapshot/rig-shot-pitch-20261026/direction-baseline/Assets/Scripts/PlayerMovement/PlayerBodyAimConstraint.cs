using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace RPG4.PlayerMovement
{
    [Serializable]
    public struct PlayerBodyAimData : IAnimationJobData
    {
        public Transform characterRoot;
        public Transform skeletonRoot;
        public Quaternion skeletonLocalRotation;
        public Transform spine;
        public Transform chest;
        [SyncSceneToStream] public float viewYaw;
        [SyncSceneToStream] public float pitch;
        [SyncSceneToStream, Range(0f, 1f)] public float followWeight;
        [SyncSceneToStream, Range(0f, 1f)] public float pitchWeight;
        [SyncSceneToStream, Range(0f, 1f)] public float waistFraction;

        public bool IsValid() => characterRoot != null && skeletonRoot != null && spine != null
            && chest != null && skeletonRoot.parent == characterRoot
            && spine.IsChildOf(skeletonRoot) && chest.IsChildOf(spine);
        public void SetDefaultValues()
        {
            characterRoot = skeletonRoot = spine = chest = null;
            skeletonLocalRotation = Quaternion.identity;
            viewYaw = pitch = followWeight = pitchWeight = 0f;
            waistFraction = 0.8f;
        }
    }

    public struct PlayerBodyAimJob : IWeightedAnimationJob
    {
        public TransformSceneHandle characterRoot;
        public ReadWriteTransformHandle skeletonRoot, spine, chest;
        public Quaternion skeletonLocalRotation;
        public FloatProperty viewYaw, pitch, followWeight, pitchWeight, waistFraction;
        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }
        public void ProcessAnimation(AnimationStream stream)
        {
            float weight = jobWeight.Get(stream);
            Quaternion characterRotation = characterRoot.GetRotation(stream);
            Vector3 up = characterRotation * Vector3.up;
            Vector3 forward = characterRotation * Vector3.forward;
            float characterYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            float yaw = Mathf.DeltaAngle(characterYaw, viewYaw.Get(stream))
                * Mathf.Clamp01(followWeight.Get(stream)) * weight;
            Quaternion yawRotation = Quaternion.AngleAxis(yaw, up);
            // 可见骨架根不由 Humanoid 动画驱动；从保存的中性旋转计算，避免逐帧累加偏航。
            Quaternion localYaw = Quaternion.Inverse(characterRotation) * yawRotation * characterRotation;
            skeletonRoot.SetLocalRotation(stream, localYaw * skeletonLocalRotation);

            float angle = pitch.Get(stream) * Mathf.Clamp01(pitchWeight.Get(stream)) * weight;
            if (weight <= 0f || Mathf.Abs(angle) <= 0.0001f)
            {
                AnimationRuntimeUtils.PassThrough(stream, spine);
                AnimationRuntimeUtils.PassThrough(stream, chest);
                return;
            }
            // 在本帧动画流中使用角色横轴，避免导入骨骼局部轴与角色轴不一致。
            Vector3 right = yawRotation * (characterRotation * Vector3.right);
            float waist = Mathf.Clamp01(waistFraction.Get(stream));
            spine.SetRotation(stream, Quaternion.AngleAxis(angle * waist, right) * spine.GetRotation(stream));
            chest.SetRotation(stream, Quaternion.AngleAxis(angle * (1f - waist), right) * chest.GetRotation(stream));
        }
    }

    public sealed class PlayerBodyAimBinder : AnimationJobBinder<PlayerBodyAimJob, PlayerBodyAimData>
    {
        public override PlayerBodyAimJob Create(Animator animator, ref PlayerBodyAimData data, Component component)
        {
            return new PlayerBodyAimJob
            {
                // 物理根只读场景姿态；Rig 的通用只读句柄会把 Animator 自身也绑定进动画流。
                characterRoot = animator.BindSceneTransform(data.characterRoot),
                skeletonRoot = ReadWriteTransformHandle.Bind(animator, data.skeletonRoot),
                skeletonLocalRotation = data.skeletonLocalRotation,
                spine = ReadWriteTransformHandle.Bind(animator, data.spine),
                chest = ReadWriteTransformHandle.Bind(animator, data.chest),
                viewYaw = Bind(animator, component, nameof(data.viewYaw)),
                pitch = Bind(animator, component, nameof(data.pitch)),
                followWeight = Bind(animator, component, nameof(data.followWeight)),
                pitchWeight = Bind(animator, component, nameof(data.pitchWeight)),
                waistFraction = Bind(animator, component, nameof(data.waistFraction))
            };
        }
        private static FloatProperty Bind(Animator animator, Component component, string field)
            => FloatProperty.Bind(animator, component, ConstraintsUtils.ConstructConstraintDataPropertyName(field));
        public override void Destroy(PlayerBodyAimJob job) { }
    }

    /// <summary>BodyAimRig 内的叠加旋转：先补偿水平朝向，再按腰胸比例分配俯仰。</summary>
    [DisallowMultipleComponent, AddComponentMenu("RPG4/Player Body Aim Constraint")]
    public sealed class PlayerBodyAimConstraint
        : RigConstraint<PlayerBodyAimJob, PlayerBodyAimData, PlayerBodyAimBinder> { }
}
