using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RPG4.CharacterAnimation.Editor
{
    /// <summary>Explicit, repeatable conversion; migrated policy is never overwritten from legacy data.</summary>
    public static class CharacterPresentationMigration
    {
        private const string ProfileFolder = "Assets/_Project/Code/CharacterAnimation/";

        [MenuItem("RPG4/Animation/Migrate Single Direction Presentation")]
        public static void MigrateProject()
        {
            // Include the two supplied pairs even when no currently loaded prefab references one.
            foreach (string prefix in new[] { "Default", "Network" })
                MigratePair(AssetDatabase.LoadAssetAtPath<CharacterAnimationProfile>(ProfileFolder + prefix + "CharacterAnimation.asset"),
                    AssetDatabase.LoadAssetAtPath<CharacterIKProfile>(ProfileFolder + prefix + "CharacterIK.asset"));

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/Prefabs" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    bool changed = false;
                    foreach (var system in root.GetComponentsInChildren<CharacterPresentationSystem>(true))
                    {
                        var serialized = new SerializedObject(system);
                        var animation = serialized.FindProperty("profile").objectReferenceValue as CharacterAnimationProfile;
                        var solver = serialized.FindProperty("ikProfile").objectReferenceValue as CharacterIKProfile;
                        var intentProperty = serialized.FindProperty("intentProfile");
                        var intent = intentProperty.objectReferenceValue as CharacterIKIntentProfile;
                        intent = MigratePair(animation, solver, intent);
                        if (intentProperty.objectReferenceValue == intent) continue;
                        intentProperty.objectReferenceValue = intent;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        changed = true;
                    }
                    if (changed) PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            CharacterIKAuthoring.ValidatePresentation();
            Debug.Log("Single direction presentation migration is complete; existing migrated policies were retained.");
        }

        private static int Version(UnityEngine.Object asset, string name)
            => new SerializedObject(asset).FindProperty(name).intValue;

        private static void SetVersion(UnityEngine.Object asset, string name, int value)
        {
            var serialized = new SerializedObject(asset);
            serialized.FindProperty(name).intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static CharacterIKIntentProfile MigratePair(CharacterAnimationProfile animation,
            CharacterIKProfile solver, CharacterIKIntentProfile intent = null)
        {
            if (animation == null || solver == null) throw new InvalidOperationException("Migration requires explicit animation and solver profiles.");
            int animationVersion = Version(animation, "posePolicyVersion"), solverVersion = Version(solver, "schemaVersion");
            if (animationVersion != 0 && animationVersion != CharacterAnimationProfile.CurrentPosePolicyVersion
                || solverVersion != 0 && solverVersion != CharacterIKProfile.CurrentSchemaVersion)
                throw new InvalidOperationException(animation.name + ": unsupported presentation schema version.");

            string solverPath = AssetDatabase.GetAssetPath(solver);
            string intentPath = Path.ChangeExtension(solverPath, null) + "Intent.asset";
            if (intent == null) intent = AssetDatabase.LoadAssetAtPath<CharacterIKIntentProfile>(intentPath);
            if (animationVersion == CharacterAnimationProfile.CurrentPosePolicyVersion
                && solverVersion == CharacterIKProfile.CurrentSchemaVersion)
            {
                if (intent == null) throw new InvalidOperationException(solver.name + ": migrated solver has no intent profile; restore its explicit intent asset.");
                RequireValid(animation, intent, solver);
                return intent;
            }

            // Reject incomplete legacy input before changing any member of this pair.
            if (solver.actions == null || solver.actions.Length == 0)
                throw new InvalidOperationException(solver.name + ": missing legacy policies; migration cannot infer lost data.");
            var legacy = new Dictionary<string, CharacterIKActionConfiguration>(StringComparer.Ordinal);
            foreach (var action in solver.actions)
            {
                if (action == null || string.IsNullOrWhiteSpace(action.id) || legacy.ContainsKey(action.id))
                    throw new InvalidOperationException(solver.name + ": legacy action ids must be unique and nonempty.");
                legacy.Add(action.id, action);
            }
            if (animation.Actions == null || animation.Actions.Length != legacy.Count
                || animation.Actions.Any(action => action == null || !legacy.ContainsKey(action.id)))
                throw new InvalidOperationException(animation.name + ": legacy IK and animation action mappings differ.");
            if (animationVersion == CharacterAnimationProfile.CurrentPosePolicyVersion && solverVersion == 0)
                RequireMatchingOrientation(animation, solver, legacy);
            if (intent != null && Version(intent, "schemaVersion") != CharacterIKIntentProfile.CurrentSchemaVersion)
                throw new InvalidOperationException(intent.name + ": existing intent asset has an unsupported schema; migration will not overwrite it.");

            // Temporary candidate validates all copied IK stages before publishing an asset or version marker.
            var candidate = ScriptableObject.CreateInstance<CharacterIKIntentProfile>();
            CharacterAnimationProfile animationCandidate = null;
            CharacterIKProfile solverCandidate = null;
            try
            {
                candidate.waistPitchOffset = solver.waistPitchOffset;
                candidate.actions = solver.actions.Select(action => new CharacterIKIntentActionConfiguration
                {
                    id = action.id, allowSupportHand = action.allowSupportHand,
                    waistPitchOffset = action.waistPitchOffset, shotReference = action.shotReference,
                    stages = action.stages == null ? null : action.stages.Select(stage => stage == null ? null : new CharacterIKStage
                    { phase = stage.phase, socket = stage.socket, supportHandMultiplier = stage.supportHandMultiplier }).ToArray()
                }).ToArray();
                SetVersion(candidate, "schemaVersion", CharacterIKIntentProfile.CurrentSchemaVersion);
                string error = candidate.ValidateConfiguration();
                if (error != null) throw new InvalidOperationException(solver.name + ": " + error);
                if (solver.actions.Any(action => !Finite(action.yawOffset)) || !Finite(solver.yawOffset))
                    throw new InvalidOperationException(solver.name + ": invalid legacy orientation offsets.");

                animationCandidate = UnityEngine.Object.Instantiate(animation);
                solverCandidate = UnityEngine.Object.Instantiate(solver);
                if (animationVersion == 0) CopyOrientation(animationCandidate, solver, legacy);
                SetVersion(solverCandidate, "schemaVersion", CharacterIKProfile.CurrentSchemaVersion);
                RequireValid(animationCandidate, intent != null ? intent : candidate, solverCandidate);

                if (intent == null)
                {
                    if (AssetDatabase.LoadMainAssetAtPath(intentPath) != null)
                        throw new InvalidOperationException("Migration destination is occupied: " + intentPath);
                    candidate.name = Path.GetFileNameWithoutExtension(intentPath);
                    AssetDatabase.CreateAsset(candidate, intentPath);
                    intent = candidate;
                    candidate = null;
                }
                if (animationVersion == 0)
                {
                    Undo.RecordObject(animation, "Migrate animation orientation policy");
                    CopyOrientation(animation, solver, legacy);
                    EditorUtility.SetDirty(animation);
                }
                SetVersion(solver, "schemaVersion", CharacterIKProfile.CurrentSchemaVersion);
                EditorUtility.SetDirty(solver);
                RequireValid(animation, intent, solver);
                AssetDatabase.SaveAssetIfDirty(animation);
                AssetDatabase.SaveAssetIfDirty(intent);
                AssetDatabase.SaveAssetIfDirty(solver);
                return intent;
            }
            finally
            {
                if (candidate != null) UnityEngine.Object.DestroyImmediate(candidate);
                if (animationCandidate != null) UnityEngine.Object.DestroyImmediate(animationCandidate);
                if (solverCandidate != null) UnityEngine.Object.DestroyImmediate(solverCandidate);
            }
        }

        private static void CopyOrientation(CharacterAnimationProfile animation, CharacterIKProfile solver,
            Dictionary<string, CharacterIKActionConfiguration> legacy)
        {
            foreach (var action in animation.Actions)
            {
                var old = legacy[action.id];
                action.allowAim = old.allowAim; action.allowBodyTurn = old.allowBodyTurn; action.yawOffset = old.yawOffset;
            }
            var serialized = new SerializedObject(animation);
            serialized.FindProperty("yawOffset").floatValue = solver.yawOffset;
            serialized.FindProperty("posePolicyVersion").intValue = CharacterAnimationProfile.CurrentPosePolicyVersion;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void RequireMatchingOrientation(CharacterAnimationProfile animation, CharacterIKProfile solver,
            Dictionary<string, CharacterIKActionConfiguration> legacy)
        {
            bool matches = new SerializedObject(animation).FindProperty("yawOffset").floatValue == solver.yawOffset;
            foreach (var action in animation.Actions)
            {
                var old = legacy[action.id];
                matches &= action.allowAim == old.allowAim && action.allowBodyTurn == old.allowBodyTurn
                    && action.yawOffset == old.yawOffset;
            }
            if (!matches)
                throw new InvalidOperationException(animation.name + ": migrated orientation differs from legacy solver " + solver.name
                    + ". Use a separate animation profile or explicitly reconcile its orientation before migration; existing policy was retained.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void RequireValid(CharacterAnimationProfile animation, CharacterIKIntentProfile intent, CharacterIKProfile solver)
        {
            string error = CharacterPresentationSystem.ValidateProfiles(animation, intent, solver);
            if (error != null) throw new InvalidOperationException(animation.name + ": " + error);
        }
    }
}
