using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace RPG4.CharacterAnimation
{
    /// <summary>Animation-side socket/hand execution. Equipment geometry arrives as value data.</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerWeaponRig : MonoBehaviour
    {
        [SerializeField] private MultiParentConstraint weaponSocket;
        [SerializeField] private TwoBoneIKConstraint supportHand;
        private AnimationPoseTarget poseTarget;
        private bool wasHolding, wasReloading, hasGripPose;
        private float eventGripWeight = 1f;
        private int gripFrame = -1, reloadState, crouchReloadState;
        private Vector3 gripPosition;
        private Quaternion gripRotation = Quaternion.identity;
        internal bool HasGrip => supportHand != null && supportHand.weight > 0f;
        internal void Initialize() => SelectSocket(0);
        internal void SetGripPose(in CharacterAnimationState state)
        {
            hasGripPose = state.HasGripPose;
            gripPosition = state.GripPosition;
            gripRotation = state.GripRotation;
        }
        internal void RestoreReloadPose(AnimationClip clip, float phase, AnimationActionConfiguration action)
        {
            RestoreSocket();
            reloadState = action.StateHash(false); crouchReloadState = action.StateHash(true);
            if (clip == null) return;
            float elapsed = Mathf.Clamp01(phase) * clip.length;
            foreach (AnimationEvent entry in clip.events)
                if (entry.time <= elapsed && (entry.functionName == "SwitchSocket" || entry.functionName == "SwitchSocketByString"))
                    SwitchSocketByString(entry.stringParameter);
        }
        internal void RestoreSocket() => SwitchSocketByString("To_Hand_R_Socket,IK_ON_Left_Handle");
        internal void ApplyPresentation(AnimationPoseTarget target, float deltaTime, int frameId)
        { poseTarget = target; ApplyGrip(deltaTime, frameId); }
        private void ApplyGrip(float deltaTime, int frameId)
        {
            if (hasGripPose && supportHand != null && supportHand.data.target != null)
                supportHand.data.target.SetPositionAndRotation(gripPosition, gripRotation);
            bool holding = poseTarget.SupportHandWeight > 0f && hasGripPose;
            bool reloading = poseTarget.Reloading;
            if (holding) SelectSocket(0);
            if (holding && (!wasHolding || wasReloading)) eventGripWeight = 1f;
            wasHolding = holding; wasReloading = reloading;
            if (supportHand == null) return;
            float target = isActiveAndEnabled && holding ? poseTarget.SupportHandWeight * eventGripWeight : 0f;
            if (target <= 0f) { supportHand.weight = 0f; gripFrame = -1; }
            else if (gripFrame != frameId)
            {
                supportHand.weight = Mathf.Lerp(supportHand.weight, target, Mathf.Clamp01(deltaTime * 15f));
                if (deltaTime > 0f) gripFrame = frameId;
            }
        }
        // Unity animation events stay private; external callers submit semantic actions.
        private void SwitchSocket(AnimationEvent animationEvent)
        {
            if (animationEvent == null) return;
            if (animationEvent.isFiredByAnimator)
            {
                int source = animationEvent.animatorStateInfo.fullPathHash;
                bool reloadEvent = source == reloadState || source == crouchReloadState;
                if (poseTarget.Reloading != reloadEvent) return;
            }
            SwitchSocketByString(animationEvent.stringParameter);
        }
        private void SwitchSocketByString(string commands)
        {
            if (string.IsNullOrEmpty(commands)) return;
            foreach (string raw in commands.Split(','))
                switch (raw.Trim())
                {
                    case "To_Hand_R_Socket": SelectSocket(0); break;
                    case "To_Put_Socket_Rifle": SelectSocket(1); break;
                    case "To_add_weapon_r": SelectSocket(2); break;
                    case "IK_OFF_Left_Handle": eventGripWeight = 0f; break;
                    case "IK_ON_Left_Handle": eventGripWeight = 1f; break;
                }
            ApplyGrip(Time.deltaTime, Time.frameCount);
        }
        private void SelectSocket(int selected)
        {
            if (weaponSocket == null) return;
            var sources = weaponSocket.data.sourceObjects;
            if (selected < 0 || selected >= sources.Count) return;
            for (int i = 0; i < sources.Count; i++) sources.SetWeight(i, i == selected ? 1f : 0f);
            weaponSocket.data.sourceObjects = sources;
        }
        internal void ReleaseGrip()
        {
            poseTarget = default;
            if (supportHand != null) supportHand.weight = 0f;
            wasHolding = wasReloading = false; eventGripWeight = 1f; gripFrame = -1;
        }
        private void OnDisable() => ReleaseGrip();
    }
}
