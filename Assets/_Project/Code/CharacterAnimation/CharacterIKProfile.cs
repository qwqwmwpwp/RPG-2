using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    [Flags]
    public enum CharacterIKModules { None = 0, ShotPose = 1, BodyAim = 2, Weapon = 4, SupportHand = 8, All = 15 }

    /// <summary>Numerical solver settings only. Semantic action mappings belong to the intent profile.</summary>
    [CreateAssetMenu(menuName = "RPG4/Character IK Solver Profile")]
    public sealed class CharacterIKProfile : ScriptableObject
    {
        public const int CurrentSchemaVersion = 1;
        [SerializeField] private int schemaVersion;
        [SerializeField] private CharacterIKModules modules = CharacterIKModules.All;
        public CharacterIKModules Modules => modules;
        [SerializeField] internal Vector2 pitchRange = new Vector2(-30f, 45f);
        [SerializeField, Min(0f)] internal float pitchSmoothTime = 0.1f;
        [SerializeField, Range(0f, 1f)] internal float waistFraction = 0.8f;
        [SerializeField, Range(0f, 180f)] internal float yawLimit = 80f;

#if UNITY_EDITOR
        // Retained only for explicit editor migration. Runtime never reads legacy action policy.
        [SerializeField, HideInInspector] internal float yawOffset, waistPitchOffset;
        [SerializeField, HideInInspector] internal CharacterIKActionConfiguration[] actions = Array.Empty<CharacterIKActionConfiguration>();
#endif

        public string ValidateConfiguration()
        {
            if (schemaVersion != CurrentSchemaVersion) return "IK solver profile requires editor migration.";
            if ((modules & ~CharacterIKModules.All) != 0) return "IK profile has unsupported modules.";
            if (!Finite(pitchRange.x) || !Finite(pitchRange.y) || pitchRange.x > pitchRange.y
                || !Finite(pitchSmoothTime) || pitchSmoothTime < 0f || !Unit(waistFraction)
                || !Finite(yawLimit) || yawLimit < 0f || yawLimit > 180f)
                return "IK profile has invalid pitch, waist, yaw or smoothing settings.";
            return null;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Unit(float value) => Finite(value) && value >= 0f && value <= 1f;
    }

#if UNITY_EDITOR
    [Serializable]
    internal sealed class CharacterIKActionConfiguration
    {
        [SerializeField] internal string id;
        [SerializeField] internal bool allowAim, allowSupportHand, allowBodyTurn = true;
        [SerializeField] internal float yawOffset, waistPitchOffset;
        [SerializeField] internal bool shotReference;
        [SerializeField] internal CharacterIKStage[] stages = Array.Empty<CharacterIKStage>();
    }
#endif
}
