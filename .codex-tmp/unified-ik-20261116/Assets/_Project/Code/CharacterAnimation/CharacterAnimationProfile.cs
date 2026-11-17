using System;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    [Flags] internal enum AnimationBody { None = 0, Lower = 1, Upper = 2, Full = Lower | Upper }
    [Flags] internal enum AnimationGroup
    { None = 0, Locomotion = 1, Turn = 2, Aim = 4, Shot = 8, Reload = 16, Consumable = 32, Hurt = 64, Life = 128, All = 255 }
    internal enum AnimationChannel { Base, Upper, Reaction, Pose }
    internal enum AnimationPlayback { Locomotion, Turn, Pose, Shot, Reload, Consumable, Hurt, Life }
    internal enum AnimationProgress { Restart, PreservePhase, FollowAction }

    [Serializable]
    internal sealed class AnimationChannelConfiguration
    {
        [SerializeField] internal AnimationChannel channel;
        [SerializeField] internal string layerName;
        [SerializeField] internal AvatarMask mask;
        [SerializeField] internal bool additive;
        [SerializeField] internal string emptyState;
    }

    [Serializable]
    internal sealed class AnimationActionConfiguration
    {
        [SerializeField] internal string id;
        [SerializeField] internal AnimationPlayback playback;
        [SerializeField] internal AnimationChannel channel;
        [SerializeField] internal AnimationGroup group;
        [SerializeField] internal AnimationFacts requireAll, requireAny, exclude;
        [SerializeField] internal int priority;
        [Tooltip("Logical control, not the base layer's full-body fallback pose.")]
        [SerializeField] internal AnimationBody occupiedBody;
        [Tooltip("Body regions where other compatible actions may participate.")]
        [SerializeField] internal AnimationBody participatingBody;
        [Tooltip("Body regions where an already participating, still valid action may be replaced.")]
        [SerializeField] internal AnimationBody changeableBody;
        [SerializeField] internal AnimationGroup compatibleGroups;
        [SerializeField] internal bool additive;
        [SerializeField] internal string state, crouchState;
        [SerializeField] internal Motion motion, crouchMotion;
        [SerializeField] internal string speedParameter;
        [SerializeField] internal AnimationProgress progress = AnimationProgress.PreservePhase;
        [SerializeField, Min(0f)] internal float fadeIn = 0.08f, fadeOut = 0.08f;
        [SerializeField, Range(0f, 1f)] internal float weight = 1f;
        [SerializeField] internal bool allowAim, allowSupportHand, allowBodyTurn = true;
        [SerializeField] internal float yawOffset, waistPitchOffset;
        internal string State(bool crouched) => crouched && !string.IsNullOrEmpty(crouchState) ? crouchState : state;
        internal Motion Motion(bool crouched) => crouched && crouchMotion != null ? crouchMotion : motion;
        internal bool Matches(AnimationFacts facts) => (facts & requireAll) == requireAll
            && (requireAny == AnimationFacts.None || (facts & requireAny) != 0) && (facts & exclude) == 0;
        internal int StateHash(bool crouched) => Animator.StringToHash(State(crouched));
    }

    [Serializable]
    internal sealed class LocomotionAnimationConfiguration
    {
        [SerializeField, Min(0f)] internal float parameterSmoothTime = 0.1f;
        [SerializeField, Min(0f)] internal float freeTurnSmoothTime = 0.12f, strafeTurnSmoothTime = 0.08f;
        [SerializeField, Min(0.1f)] internal float freeReferenceSpeed = 5.130632f, strafeReferenceSpeed = 2.5f, crouchReferenceSpeed = 2f;
        [SerializeField, Range(1f, 180f)] internal float turnThreshold = 60f;
        [SerializeField, Range(0f, 90f)] internal float turnExitThreshold = 20f;
        [SerializeField, Range(0f, 1f)] internal float turnMoveThreshold = 0.15f;
        [SerializeField, Min(0f)] internal float postureTransition = 0.2f;
    }

    [Serializable]
    internal sealed class AimPoseAnimationConfiguration
    {
        [SerializeField] internal Vector2 pitchRange = new Vector2(-30f, 45f);
        [SerializeField, Min(0f)] internal float pitchSmoothTime = 0.1f;
        [SerializeField, Range(0f, 1f)] internal float waistFraction = 0.8f;
        [SerializeField, Range(0f, 180f)] internal float yawLimit = 80f;
    }

    /// <summary>The sole editable animation policy/resource source; controller mappings are synchronized and validated in the editor.</summary>
    [CreateAssetMenu(menuName = "RPG4/Character Animation Profile")]
    public sealed class CharacterAnimationProfile : ScriptableObject
    {
        [SerializeField] private RuntimeAnimatorController controller;
        [SerializeField] private AnimationChannelConfiguration[] channels = Array.Empty<AnimationChannelConfiguration>();
        [SerializeField] private AnimationActionConfiguration[] actions = Array.Empty<AnimationActionConfiguration>();
        [SerializeField] private LocomotionAnimationConfiguration locomotion = new LocomotionAnimationConfiguration();
        [SerializeField] private AimPoseAnimationConfiguration aimPose = new AimPoseAnimationConfiguration();
        [SerializeField] private float yawOffset, waistPitchOffset;
        internal RuntimeAnimatorController Controller => controller;
        internal AnimationChannelConfiguration[] Channels => channels;
        internal AnimationActionConfiguration[] Actions => actions;
        internal LocomotionAnimationConfiguration Locomotion => locomotion;
        internal AimPoseAnimationConfiguration AimPose => aimPose;
        internal float YawOffset => yawOffset;
        internal float WaistPitchOffset => waistPitchOffset;
        internal AnimationChannelConfiguration Channel(AnimationChannel channel)
        {
            foreach (var entry in channels) if (entry != null && entry.channel == channel) return entry;
            return null;
        }

        public string ValidateConfiguration()
        {
            if (controller == null) return "Animation profile has no controller.";
            if (actions == null || actions.Length == 0) return "Animation profile has no actions.";
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var action in actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.id) || !ids.Add(action.id)) return "Animation action ids must be unique and nonempty.";
                if ((action.requireAll & action.exclude) != 0) return action.id + ": required facts are excluded.";
                if (action.group == AnimationGroup.None) return action.id + ": action group is required.";
                AnimationChannel expected = action.playback == AnimationPlayback.Pose ? AnimationChannel.Pose
                    : action.playback == AnimationPlayback.Hurt ? AnimationChannel.Reaction
                    : action.playback == AnimationPlayback.Locomotion || action.playback == AnimationPlayback.Turn || action.playback == AnimationPlayback.Life
                        ? AnimationChannel.Base : AnimationChannel.Upper;
                if (action.channel != expected) return action.id + ": playback kind is not supported by this fixed backend channel.";
                string fixedSpeed = action.playback == AnimationPlayback.Locomotion ? "AimLocomotionSpeed"
                    : action.playback == AnimationPlayback.Turn ? "TurnSpeed" : null;
                if (fixedSpeed != null && action.speedParameter != fixedSpeed) return action.id + ": this locomotion backend requires speed parameter " + fixedSpeed + ".";
                if (action.playback == AnimationPlayback.Life && !string.IsNullOrWhiteSpace(action.speedParameter))
                    return action.id + ": life fallback playback has no action timeline and cannot use a speed parameter.";
                if (action.channel == AnimationChannel.Pose)
                {
                    if (action.playback != AnimationPlayback.Pose) return action.id + ": pose channel only accepts procedural poses.";
                    continue;
                }
                var binding = Channel(action.channel);
                if (binding == null || string.IsNullOrWhiteSpace(binding.layerName)) return action.id + ": channel is not configured.";
                if (action.additive && (!binding.additive || !(action.motion is AnimationClip)))
                    return action.id + ": additive actions require an additive channel and an authored clip.";
                if (binding.additive != action.additive) return action.id + ": action and channel blend modes differ.";
                if (string.IsNullOrWhiteSpace(action.state) || action.motion == null) return action.id + ": state and motion are required.";
                if (!string.IsNullOrWhiteSpace(action.crouchState) && action.crouchMotion == null) return action.id + ": crouch state needs its own motion.";
                if (action.fadeIn < 0f || action.fadeOut < 0f || action.weight < 0f || action.weight > 1f) return action.id + ": invalid transition or weight.";
            }
            for (int i = 0; i < 3; i++)
            {
                var binding = Channel((AnimationChannel)i);
                if (binding == null || string.IsNullOrWhiteSpace(binding.layerName)) return "Base, upper and reaction channels must all be configured.";
                if (i > 0 && binding.mask == null) return "Overlay channels require a fixed avatar mask.";
                if (i == 0 && (binding.mask != null || binding.additive)) return "Base channel must provide an unmasked override fallback pose.";
            }
            return null;
        }
    }
}
