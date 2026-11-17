using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace RPG4.CharacterAnimation
{
    /// <summary>将桥接目标提交给 BodyAim、Weapon、SupportHand 三段 Rig。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator), typeof(RigBuilder))]
    public sealed class PlayerAimPose : MonoBehaviour
    {
        [SerializeField] private RigBuilder rigBuilder;
        [SerializeField] private PlayerBodyAimConstraint bodyAim;
        [SerializeField] private PlayerShotPoseConstraint shotPose;
        [SerializeField] private PlayerWeaponRig weaponRig;
        private AimPoseAnimationConfiguration settings;

        private bool initialized;
        private Animator animator;
        private float pitch;
        private float pitchVelocity;
        private int preparedFrame = -1;
        private bool wasPosing;

        internal bool HasActivePose => shotPose != null && shotPose.data.activeWeight > 0f || bodyAim != null
            && (bodyAim.data.bodyWeight > 0f || bodyAim.data.followWeight > 0f || bodyAim.data.pitchWeight > 0f)
            || weaponRig != null && weaponRig.HasGrip;

        internal void Initialize(Transform ownerRoot, AimPoseAnimationConfiguration settings)
        {
            this.settings = settings;
            animator = GetComponent<Animator>();
            if (rigBuilder == null) rigBuilder = GetComponent<RigBuilder>();
            if (shotPose == null) shotPose = GetComponentInChildren<PlayerShotPoseConstraint>(true);
            if (bodyAim == null || !bodyAim.IsValid() || weaponRig == null || rigBuilder == null
                || bodyAim.data.characterRoot != transform
                || transform != ownerRoot && !transform.IsChildOf(ownerRoot))
            {
                Debug.LogError("PlayerAimPose 需要完整的 BodyAim、Weapon、SupportHand Rig 和角色引用。", this);
                enabled = false;
                return;
            }
            weaponRig.Initialize();
            initialized = true;
        }

        internal void ApplyPresentation(AnimationPoseTarget target, float deltaTime, int frameId, float? bodyYaw = null)
        {
            if (!initialized || bodyAim == null || rigBuilder == null) return;
            bool posing = isActiveAndEnabled && target.Enabled;
            if (!posing)
            {
                pitch = pitchVelocity = 0f;
            }
            else if (target.ImmediateView || preparedFrame != frameId || !wasPosing)
            {
                float targetPitch = Mathf.Clamp(target.Pitch, settings.pitchRange.x, settings.pitchRange.y);
                if (target.ImmediateView || settings.pitchSmoothTime <= 0f) pitch = targetPitch;
                else if (deltaTime > 0f)
                    pitch = Mathf.SmoothDamp(pitch, targetPitch, ref pitchVelocity,
                        settings.pitchSmoothTime, Mathf.Infinity, deltaTime);
                pitch = Mathf.Clamp(pitch, settings.pitchRange.x, settings.pitchRange.y);
            }
            if (deltaTime > 0f) preparedFrame = frameId;
            wasPosing = posing;
            bodyAim.data.viewYaw = target.ViewYaw;
            bodyAim.data.bodyYaw = bodyYaw ?? target.ViewYaw;
            bodyAim.data.bodyWeight = isActiveAndEnabled && target.BodyEnabled && (bodyYaw.HasValue || target.FollowView) ? 1f : 0f;
            bodyAim.data.yawLimit = settings.yawLimit;
            bodyAim.data.pitch = pitch;
            bodyAim.data.waistFraction = settings.waistFraction;
            bodyAim.data.yawOffset = posing ? target.YawOffset : 0f;
            bodyAim.data.waistPitchOffset = posing ? target.WaistPitchOffset : 0f;
            bodyAim.data.followWeight = isActiveAndEnabled && target.FollowView ? target.PoseWeight : 0f;
            bodyAim.data.pitchWeight = posing ? target.PoseWeight : 0f;
            // A disabled Animator layer can retain its last generic reference value.
            if (shotPose != null) shotPose.data.activeWeight = isActiveAndEnabled
                ? target.LayerWeight : 0f;
            if (weaponRig != null)
            {
                if (isActiveAndEnabled) weaponRig.ApplyPresentation(target, deltaTime, frameId);
                else weaponRig.ReleaseGrip();
            }
            // Player.Update 晚于 RigBuilder.Update，显式提交本帧权重，避免沿用上一帧输入。
            rigBuilder.SyncLayers();
        }

        internal void Reevaluate()
        {
            if (rigBuilder != null && rigBuilder.graph.IsValid() && animator != null
                && animator.isActiveAndEnabled && gameObject.activeInHierarchy)
                rigBuilder.Evaluate(0f);
        }

        internal void ResetPose()
        {
            bool hadPose = HasActivePose;
            pitch = pitchVelocity = 0f;
            preparedFrame = -1;
            wasPosing = false;
            if (shotPose != null) shotPose.data.activeWeight = 0f;
            if (bodyAim != null)
            {
                bodyAim.data.bodyWeight = bodyAim.data.followWeight = bodyAim.data.pitchWeight = 0f;
                bodyAim.data.pitch = 0f;
                bodyAim.data.yawOffset = bodyAim.data.waistPitchOffset = 0f;
            }
            weaponRig?.ReleaseGrip();
            if (rigBuilder != null) rigBuilder.SyncLayers();
            // 暂停或同帧打开界面时也撤销已经求值的 Rig 姿态，不推进动画时间。
            if (hadPose) Reevaluate();
        }

        private void OnDisable()
        {
            ResetPose();
        }

        // Animator 位于表现根；根运动仍由逻辑侧 motor 独占，不能覆盖纠错偏移。
        private void OnAnimatorMove() { }

    }
}
