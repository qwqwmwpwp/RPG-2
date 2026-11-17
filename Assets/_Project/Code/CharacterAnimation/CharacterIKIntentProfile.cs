using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    internal enum CharacterIKSocket { Hand, Stowed, Reload }

    [Serializable]
    internal sealed class CharacterIKStage
    {
        [SerializeField, Range(0f, 1f)] internal float phase;
        [SerializeField] internal CharacterIKSocket socket;
        [SerializeField, Range(0f, 1f)] internal float supportHandMultiplier = 1f;
    }

    [Serializable]
    internal sealed class CharacterIKIntentActionConfiguration
    {
        [SerializeField] internal string id;
        [SerializeField] internal bool allowSupportHand;
        [SerializeField] internal float waistPitchOffset;
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

    /// <summary>One-way translation from executed animation semantics to explicit constraint demands.</summary>
    [CreateAssetMenu(menuName = "RPG4/Character IK Intent Profile")]
    public sealed class CharacterIKIntentProfile : ScriptableObject
    {
        public const int CurrentSchemaVersion = 1;
        [SerializeField] private int schemaVersion;
        [SerializeField] internal float waistPitchOffset;
        [SerializeField] internal CharacterIKIntentActionConfiguration[] actions = Array.Empty<CharacterIKIntentActionConfiguration>();
        internal CharacterIKIntentActionConfiguration Action(string id)
        {
            foreach (var action in actions) if (action.id == id) return action;
            return null;
        }
        public string ValidateConfiguration()
        {
            if (schemaVersion != CurrentSchemaVersion) return "IK intent profile requires editor migration.";
            if (!Finite(waistPitchOffset) || actions == null) return "IK intent profile has invalid offsets or action mappings.";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.id) || !ids.Add(action.id))
                    return "IK intent action ids must be nonempty and unique.";
                if (!Finite(action.waistPitchOffset) || action.stages == null) return action.id + ": invalid IK intent offsets or stages.";
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
