using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RPG4.CharacterAnimation.Editor
{
    /// <summary>Profiles author policy/resources; controller assets are the checked execution mapping.</summary>
    public static class CharacterAnimationAuthoring
    {
        private static readonly string[] FixedParameters = { "MoveSpeed", "AimMoveX", "AimMoveY", "AimLocomotionSpeed", "TurnSpeed" };
        [MenuItem("RPG4/Animation/Validate Character Animation")]
        public static void ValidateAll()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:CharacterAnimationProfile", new[] { "Assets/_Project/Code/CharacterAnimation" }))
                ValidateProfile(AssetDatabase.LoadAssetAtPath<CharacterAnimationProfile>(AssetDatabase.GUIDToAssetPath(guid)));
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/Prefabs" }))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var system in prefab.GetComponentsInChildren<CharacterAnimationSystem>(true))
                {
                    string error = system.ValidateBindings();
                    if (error != null) throw new InvalidOperationException(prefab.name + ": " + error);
                }
            }
            ValidateBoundary();
            Debug.Log("Character animation profiles, controller mappings, prefab bindings and source boundary are valid.");
        }

        public static void ValidateProfile(CharacterAnimationProfile profile)
        {
            string error = profile.ValidateConfiguration();
            if (error != null) throw new InvalidOperationException(profile.name + ": " + error);
            var controller = profile.Controller as AnimatorController;
            if (controller == null) throw new InvalidOperationException(profile.name + ": this backend requires an authored AnimatorController.");
            if (controller.layers.Length != 3) throw new InvalidOperationException(profile.name + ": fixed backend requires exactly base, upper and reaction layers.");
            var layerIndices = new Dictionary<AnimationChannel, int>();
            foreach (var channel in profile.Channels)
            {
                int index = Array.FindIndex(controller.layers, layer => layer.name == channel.layerName);
                if (index < 0 || layerIndices.ContainsKey(channel.channel)) throw new InvalidOperationException(profile.name + ": missing/duplicate channel " + channel.channel);
                var layer = controller.layers[index];
                if (layer.avatarMask != channel.mask || (layer.blendingMode == AnimatorLayerBlendingMode.Additive) != channel.additive)
                    throw new InvalidOperationException(profile.name + ": channel mask/blend mode differs from controller; synchronize the profile.");
                if (channel.channel == AnimationChannel.Base && index != 0) throw new InvalidOperationException("Base channel must be the first layer.");
                if (!string.IsNullOrEmpty(channel.emptyState) && FindState(controller, channel.emptyState) == null)
                    throw new InvalidOperationException(profile.name + ": empty state is missing: " + channel.emptyState);
                layerIndices.Add(channel.channel, index);
            }
            foreach (string parameter in FixedParameters) RequireParameter(controller, parameter);
            foreach (var action in profile.Actions)
            {
                if (action.channel == AnimationChannel.Pose) continue;
                ValidateState(controller, profile, action, false);
                if (!string.IsNullOrWhiteSpace(action.crouchState)) ValidateState(controller, profile, action, true);
                if (action.additive)
                {
                    var clip = action.motion as AnimationClip;
                    if (clip == null || !AnimationUtility.GetAnimationClipSettings(clip).hasAdditiveReferencePose)
                        throw new InvalidOperationException(action.id + ": additive resource needs an authored reference pose.");
                    if (action.crouchMotion is AnimationClip crouch && !AnimationUtility.GetAnimationClipSettings(crouch).hasAdditiveReferencePose)
                        throw new InvalidOperationException(action.id + ": crouch additive resource needs an authored reference pose.");
                    foreach (var basis in profile.Actions)
                    {
                        if (basis.channel == AnimationChannel.Pose || basis.additive || (basis.occupiedBody & action.occupiedBody) == 0
                            || !CharacterAnimationArbitrator.Compatible(action, basis)) continue;
                        if (layerIndices[action.channel] <= layerIndices[basis.channel])
                            throw new InvalidOperationException(action.id + ": additive layer must evaluate after its compatible override basis.");
                    }
                }
            }
        }
        private static void ValidateState(AnimatorController controller, CharacterAnimationProfile profile, AnimationActionConfiguration action, bool crouched)
        {
            string path = action.State(crouched);
            var binding = profile.Channel(action.channel);
            var state = FindState(controller, path);
            if (!path.StartsWith(binding.layerName + ".", StringComparison.Ordinal) || state == null)
                throw new InvalidOperationException(action.id + ": state is outside its configured channel: " + path);
            if (state.motion != action.Motion(crouched)) throw new InvalidOperationException(action.id + ": motion differs from profile; synchronize: " + path);
            if (action.playback != AnimationPlayback.Locomotion && !(action.Motion(crouched) is AnimationClip))
                throw new InvalidOperationException(action.id + ": timed/turn/life playback requires an AnimationClip.");
            bool speed = !string.IsNullOrWhiteSpace(action.speedParameter);
            if (state.speedParameterActive != speed || speed && state.speedParameter != action.speedParameter)
                throw new InvalidOperationException(action.id + ": speed mapping differs; synchronize: " + path);
            if (speed) RequireParameter(controller, action.speedParameter);
        }
        private static void RequireParameter(AnimatorController controller, string name)
        {
            if (!controller.parameters.Any(parameter => parameter.name == name && parameter.type == AnimatorControllerParameterType.Float))
                throw new InvalidOperationException(controller.name + ": missing float parameter " + name);
        }
        private static AnimatorState FindState(AnimatorController controller, string path)
        {
            foreach (var layer in controller.layers)
            {
                var found = FindState(layer.stateMachine, layer.name + ".", path);
                if (found != null) return found;
            }
            return null;
        }
        private static AnimatorState FindState(AnimatorStateMachine machine, string prefix, string path)
        {
            foreach (var state in machine.states) if (prefix + state.state.name == path) return state.state;
            foreach (var child in machine.stateMachines)
            {
                var found = FindState(child.stateMachine, prefix + child.stateMachine.name + ".", path);
                if (found != null) return found;
            }
            return null;
        }

        public static void Synchronize(CharacterAnimationProfile profile)
        {
            string error = profile.ValidateConfiguration();
            if (error != null) throw new InvalidOperationException(error);
            var controller = profile.Controller as AnimatorController;
            if (controller == null) throw new InvalidOperationException("Synchronizing requires an AnimatorController.");
            Undo.RecordObject(controller, "Synchronize character animation");
            var layers = controller.layers;
            foreach (var channel in profile.Channels)
            {
                int index = Array.FindIndex(layers, layer => layer.name == channel.layerName);
                if (index < 0) throw new InvalidOperationException("Create the fixed channel before synchronization: " + channel.layerName);
                layers[index].avatarMask = channel.mask;
                layers[index].blendingMode = channel.additive ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            }
            controller.layers = layers;
            foreach (var action in profile.Actions)
            {
                if (action.channel == AnimationChannel.Pose) continue;
                SynchronizeState(controller, action, false);
                if (!string.IsNullOrWhiteSpace(action.crouchState)) SynchronizeState(controller, action, true);
            }
            EditorUtility.SetDirty(controller);
            ValidateProfile(profile);
            AssetDatabase.SaveAssetIfDirty(controller);
        }
        private static void SynchronizeState(AnimatorController controller, AnimationActionConfiguration action, bool crouched)
        {
            string path = action.State(crouched);
            var state = FindState(controller, path);
            if (state == null)
            {
                int separator = path.IndexOf('.');
                if (separator <= 0) throw new InvalidOperationException("State needs a channel-qualified path: " + path);
                string layerName = path.Substring(0, separator);
                var layer = controller.layers.FirstOrDefault(candidate => candidate.name == layerName);
                if (layer == null) throw new InvalidOperationException("Unknown fixed channel: " + layerName);
                string[] segments = path.Substring(separator + 1).Split('.');
                if (segments.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Invalid state path: " + path);
                var machine = layer.stateMachine;
                for (int i = 0; i < segments.Length - 1; i++)
                {
                    var child = machine.stateMachines.FirstOrDefault(candidate => candidate.stateMachine.name == segments[i]).stateMachine;
                    machine = child != null ? child : machine.AddStateMachine(segments[i]);
                }
                state = machine.AddState(segments[segments.Length - 1]);
                state.writeDefaultValues = true;
            }
            Undo.RecordObject(state, "Synchronize character animation state");
            state.motion = action.Motion(crouched);
            bool speed = !string.IsNullOrWhiteSpace(action.speedParameter);
            if (speed && !controller.parameters.Any(parameter => parameter.name == action.speedParameter))
                controller.AddParameter(action.speedParameter, AnimatorControllerParameterType.Float);
            state.speedParameterActive = speed; state.speedParameter = speed ? action.speedParameter : "";
            EditorUtility.SetDirty(state);
        }

        /// <summary>Source guard complements private handles/assembly boundaries; it does not claim to sandbox Unity reflection.</summary>
        public static void ValidateBoundary()
        {
            const string independent = "Assets/_Project/Code/CharacterAnimation";
            foreach (string path in Directory.EnumerateFiles(independent, "*.cs", SearchOption.AllDirectories))
            {
                string source = StripComments(File.ReadAllText(path));
                if (Regex.IsMatch(source, @"\b(?:using\s+RPG4\.(?!CharacterAnimation\b)|PlayerBlackboard\b|PlayerInput\b|GetComponent(?:InParent|InChildren)?\s*<\s*Player\s*>|Mirror\.)"))
                    throw new InvalidOperationException(path + ": animation system reads business/input/network dependencies.");
            }
            foreach (string path in Directory.EnumerateFiles("Assets/_Project/Code", "*.cs", SearchOption.AllDirectories))
            {
                string normalized = path.Replace('\\', '/');
                if (normalized.StartsWith(independent + "/", StringComparison.Ordinal) || normalized.Contains("/Editor/")) continue;
                string source = StripComments(File.ReadAllText(path));
                if (Regex.IsMatch(source, @"\b(?:Animator|AnimationClip|PlayerAimPose|PlayerWeaponRig|PlayerBodyAimConstraint|PlayerShotPoseConstraint)\b")
                    || Regex.IsMatch(source, @"\.\s*(?:CrossFade(?:InFixedTime)?|SetLayerWeight|GetCurrentAnimatorStateInfo|GetNextAnimatorStateInfo)\s*\("))
                    throw new InvalidOperationException(path + ": external runtime code accesses character animation backend/resources.");
            }
        }
        private static string StripComments(string source) => Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
    }

    [CustomEditor(typeof(CharacterAnimationProfile))]
    internal sealed class CharacterAnimationProfileInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Edit action policy/resources here. Base uses AimLocomotionSpeed, turn uses TurnSpeed; other motion parameters belong to the fixed backend. Synchronize creates state slots and updates motions/masks, then validate. Other profiles sharing this controller must keep the same resource mapping.", MessageType.Info);
            DrawDefaultInspector();
            if (GUILayout.Button("Synchronize controller from profile")) CharacterAnimationAuthoring.Synchronize((CharacterAnimationProfile)target);
            if (GUILayout.Button("Validate profile and controller")) CharacterAnimationAuthoring.ValidateProfile((CharacterAnimationProfile)target);
        }
    }
    internal sealed class CharacterAnimationBuildValidation : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;
        public void OnPreprocessBuild(BuildReport report)
        {
            try { CharacterAnimationAuthoring.ValidateAll(); }
            catch (Exception exception) { throw new BuildFailedException(exception.Message); }
        }
    }
}
