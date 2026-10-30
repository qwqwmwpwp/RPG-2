using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace RPG4.PlayerMovement
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
        [Tooltip("上半身总俯仰范围；负数向上，正数向下。")]
        [SerializeField] private Vector2 pitchRange = new Vector2(-30f, 45f);
        [SerializeField, Min(0f)] private float pitchSmoothTime = 0.1f;
        [SerializeField, Range(0f, 1f)] private float waistFraction = 0.8f;

        private bool initialized;
        private Animator animator;
        private float pitch;
        private float pitchVelocity;
        private int preparedFrame = -1;
        private bool wasPosing;

        internal bool HasActivePose => shotPose != null && shotPose.data.activeWeight > 0f || bodyAim != null
            && (bodyAim.data.followWeight > 0f || bodyAim.data.pitchWeight > 0f)
            || weaponRig != null && weaponRig.HasGrip;

        internal void Initialize(Transform ownerRoot)
        {
            animator = GetComponent<Animator>();
            if (rigBuilder == null) rigBuilder = GetComponent<RigBuilder>();
            if (shotPose == null) shotPose = GetComponentInChildren<PlayerShotPoseConstraint>(true);
            if (bodyAim == null || !bodyAim.IsValid() || weaponRig == null || rigBuilder == null
                || bodyAim.data.characterRoot != ownerRoot)
            {
                Debug.LogError("PlayerAimPose 需要完整的 BodyAim、Weapon、SupportHand Rig 和角色引用。", this);
                enabled = false;
                return;
            }
            weaponRig.Initialize();
            initialized = true;
        }

        internal void ApplyPresentation(PlayerPoseTarget target, float deltaTime, int frameId)
        {
            if (!initialized || bodyAim == null || rigBuilder == null) return;
            bool posing = isActiveAndEnabled && target.Enabled;
            if (!posing)
            {
                pitch = pitchVelocity = 0f;
            }
            else if (preparedFrame != frameId || !wasPosing)
            {
                float targetPitch = Mathf.Clamp(target.Pitch, pitchRange.x, pitchRange.y);
                if (pitchSmoothTime <= 0f) pitch = targetPitch;
                else if (deltaTime > 0f)
                    pitch = Mathf.SmoothDamp(pitch, targetPitch, ref pitchVelocity,
                        pitchSmoothTime, Mathf.Infinity, deltaTime);
                pitch = Mathf.Clamp(pitch, pitchRange.x, pitchRange.y);
            }
            if (deltaTime > 0f) preparedFrame = frameId;
            wasPosing = posing;
            bodyAim.data.viewYaw = target.ViewYaw;
            bodyAim.data.pitch = pitch;
            bodyAim.data.waistFraction = waistFraction;
            bodyAim.data.yawOffset = posing ? target.YawOffset : 0f;
            bodyAim.data.waistPitchOffset = posing ? target.WaistPitchOffset : 0f;
            bodyAim.data.followWeight = isActiveAndEnabled && target.FollowView ? 1f : 0f;
            bodyAim.data.pitchWeight = posing ? 1f : 0f;
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
                bodyAim.data.followWeight = bodyAim.data.pitchWeight = 0f;
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

        private void OnValidate()
        {
            pitchRange.x = Mathf.Clamp(pitchRange.x, -60f, 0f);
            pitchRange.y = Mathf.Clamp(pitchRange.y, 0f, 60f);
            pitchSmoothTime = Mathf.Max(0f, pitchSmoothTime);
            waistFraction = Mathf.Clamp01(waistFraction);
        }
    }
}
