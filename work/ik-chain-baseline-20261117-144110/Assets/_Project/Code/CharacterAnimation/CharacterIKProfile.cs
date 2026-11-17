using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    [Flags]
    public enum CharacterIKModules { None = 0, ShotPose = 1, BodyAim = 2, Weapon = 4, SupportHand = 8, All = 15 }

    internal enum CharacterIKSocket { Hand, Stowed, Reload }

    [Serializable]
    internal sealed class CharacterIKStage
    {
        [SerializeField, Range(0f, 1f)] internal float phase;
        [SerializeField] internal CharacterIKSocket socket;
        [SerializeField, Range(0f, 1f)] internal float supportHandMultiplier = 1f;
    }

    [Serializable]
    internal sealed class CharacterIKActionConfiguration
    {
        [SerializeField] internal string id;
        [SerializeField] internal bool allowAim, allowSupportHand, allowBodyTurn = true;
        [SerializeField] internal float yawOffset, waistPitchOffset;
        [SerializeField] internal bool shotReference;
        [SerializeField] internal CharacterIKStage[] stages = Array.Empty<CharacterIKStage>();

        internal CharacterIKStage Stage(float phase)
        {
            CharacterIKStage result = null;
            foreach (var stage in stages)
            {
                if (stage.phase > phase) break;
                result = stage;
            }
            return result;
        }
    }

    /// <summary>IK policy only. Animation resources and action arbitration remain in the animation profile.</summary>
    [CreateAssetMenu(menuName = "RPG4/Character IK Profile")]
    public sealed class CharacterIKProfile : ScriptableObject
    {
        [SerializeField] private CharacterIKModules modules = CharacterIKModules.All;
        public CharacterIKModules Modules => modules;
        [SerializeField] internal Vector2 pitchRange = new Vector2(-30f, 45f);
        [SerializeField, Min(0f)] internal float pitchSmoothTime = 0.1f;
        [SerializeField, Range(0f, 1f)] internal float waistFraction = 0.8f;
        [SerializeField, Range(0f, 180f)] internal float yawLimit = 80f;
        [SerializeField] internal float yawOffset, waistPitchOffset;
        [SerializeField] internal CharacterIKActionConfiguration[] actions = Array.Empty<CharacterIKActionConfiguration>();

        internal CharacterIKActionConfiguration Action(string id)
        {
            foreach (var action in actions) if (action.id == id) return action;
            return null;
        }
        public string ValidateConfiguration()
        {
            if ((modules & ~CharacterIKModules.All) != 0) return "IK profile has unsupported modules.";
            if (!Finite(pitchRange.x) || !Finite(pitchRange.y) || pitchRange.x > pitchRange.y
                || !Finite(pitchSmoothTime) || pitchSmoothTime < 0f || !Unit(waistFraction)
                || !Finite(yawLimit) || yawLimit < 0f || yawLimit > 180f || !Finite(yawOffset) || !Finite(waistPitchOffset))
                return "IK profile has invalid pitch, waist, yaw or smoothing settings.";
            if (actions == null) return "IK profile has no action mapping collection.";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.id) || !ids.Add(action.id))
                    return "IK action ids must be nonempty and unique.";
                if (!Finite(action.yawOffset) || !Finite(action.waistPitchOffset) || action.stages == null)
                    return action.id + ": invalid IK offsets or stages.";
                float previous = -1f;
                foreach (var stage in action.stages)
                {
                    if (stage == null || !Unit(stage.phase) || stage.phase <= previous || !Unit(stage.supportHandMultiplier)
                        || stage.socket < CharacterIKSocket.Hand || stage.socket > CharacterIKSocket.Reload)
                        return action.id + ": IK stages must have ascending unique phases and valid socket/hand weights.";
                    previous = stage.phase;
                }
            }
            return null;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Unit(float value) => Finite(value) && value >= 0f && value <= 1f;
    }
}
