using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using Object = UnityEngine.Object;

namespace RPG4.PlayerMovement.Editor
{
    /// <summary>Bake source shooting reference curves and install the independent shot-pose Rig.</summary>
    public static class PlayerShotPoseAuthoring
    {
        public const string ReferenceName = "ShotPoseReference";

        /// <summary>Explicit authoring entry point; changes only the listed assets and prefab.</summary>
        public static void BakeAndInstall(string prefabPath, string controllerPath, string maskPath,
            AnimationClip singleSource, AnimationClip autoSource, string outputFolder,
            float samplesPerSecond = 120f)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play Mode before authoring.");
            if (singleSource == null || singleSource.name != "AimIdle_Shoot" || autoSource == null)
                throw new ArgumentException("Pass the actual AimIdle_Shoot subclip and automatic source clip.");
            if (AssetDatabase.GetAssetPath(singleSource).EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
                || AssetDatabase.GetAssetPath(autoSource).EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pass original imported clips, not previously baked output.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
            if (prefab == null || controller == null || mask == null)
                throw new ArgumentException("Need an existing player prefab, controller and mask.");
            if (prefab.GetComponent<Player>() == null)
                throw new InvalidOperationException("Player component must be on the prefab root.");
            if (mask.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Root))
                throw new InvalidOperationException("Shooting mask Root must remain disabled.");
            Animator template = prefab.GetComponentInChildren<Animator>(true);
            if (template == null || template.avatar == null || !template.avatar.isHuman)
                throw new InvalidOperationException("Player prefab needs a Humanoid Animator.");
            AnimatorControllerLayer upper = controller.layers.Single(layer => layer.name == "UpperBody Shooting");
            if (upper.avatarMask != mask)
                throw new InvalidOperationException("The specified mask must belong to the shooting layer.");
            string[] names = { "HipFire", "ADSFire", "HipAutoFire", "ADSAutoFire", "Empty", "Reload", "CrouchReload" };
            AnimatorState[] states = names.Select(name => upper.stateMachine.states.Single(s => s.state.name == name).state).ToArray();
            if (states.Any(state => !state.writeDefaultValues))
                throw new InvalidOperationException("All shooting states must write defaults to clear unused reference curves.");
            string singlePath = OutputPath(outputFolder, "ShotPoseSingle.anim");
            string autoPath = OutputPath(outputFolder, "ShotPoseAutomatic.anim");
            string singleMarker = SourceMarker(singleSource);
            string autoMarker = SourceMarker(autoSource);
            CheckOutput(singlePath, singleMarker);
            CheckOutput(autoPath, autoMarker);

            // Copy only transforms plus Avatar into the sampling scene. Player and
            // presentation scripts never run while source reference curves are baked.
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject sampleRoot = null;
            AnimationClip single = null, automatic = null;
            try
            {
                sampleRoot = new GameObject(template.name);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(sampleRoot, scene);
                CopyHierarchy(template.transform, sampleRoot.transform);
                Animator sampler = sampleRoot.AddComponent<Animator>();
                sampler.avatar = template.avatar;
                sampler.applyRootMotion = false;
                sampler.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                Transform reference = sampler.transform.Find(ReferenceName) ?? CreateReference(sampler);
                reference.localPosition = reference.localScale = Vector3.zero;
                reference.localRotation = Quaternion.identity;
                single = CreateClip(sampler, singleSource, reference, samplesPerSecond);
                automatic = CreateClip(sampler, autoSource, reference, samplesPerSecond);
                AssertSourceCurvesUnchanged(singleSource, single);
                AssertSourceCurvesUnchanged(autoSource, automatic);

                // Sampling is complete before loading the editable prefab contents.
                single = SaveClip(single, singlePath, singleMarker);
                automatic = SaveClip(automatic, autoPath, autoMarker);
                GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    Animator animator = contents.GetComponentInChildren<Animator>(true);
                    RigBuilder builder = animator.GetComponent<RigBuilder>();
                    if (builder == null) throw new InvalidOperationException("Player needs its existing RigBuilder.");
                    builder.Clear();
                    Transform helper = animator.transform.Find(ReferenceName);
                    if (helper == null)
                    {
                        helper = new GameObject(ReferenceName).transform;
                        helper.SetParent(animator.transform, false);
                    }
                    helper.localPosition = helper.localScale = Vector3.zero;
                    helper.localRotation = Quaternion.identity;
                    if (helper.childCount != 0 || helper.localPosition != Vector3.zero || helper.localScale != Vector3.zero)
                        throw new InvalidOperationException("ShotPoseReference must be an empty zero-payload Transform.");
                    Transform rigTransform = animator.transform.Find("ShotPoseRig");
                    if (rigTransform == null)
                    {
                        rigTransform = new GameObject("ShotPoseRig").transform;
                        rigTransform.SetParent(animator.transform, false);
                    }
                    Rig rig = rigTransform.GetComponent<Rig>() ?? rigTransform.gameObject.AddComponent<Rig>();
                    PlayerShotPoseConstraint constraint = rigTransform.GetComponent<PlayerShotPoseConstraint>()
                        ?? rigTransform.gameObject.AddComponent<PlayerShotPoseConstraint>();
                    constraint.data.animatorRoot = animator.transform;
                    constraint.data.reference = helper;
                    constraint.data.spine = animator.GetBoneTransform(HumanBodyBones.Spine);
                    constraint.data.activeWeight = 0f;
                    constraint.weight = rig.weight = 1f;
                    BindShotPoseHost(animator, constraint);
                    builder.layers.RemoveAll(layer => layer.rig == rig);
                    builder.layers.Insert(0, new RigLayer(rig));
                    if (!constraint.IsValid() || builder.layers[0].rig != rig)
                        throw new InvalidOperationException("ShotPose Rig binding/order is invalid.");
                    var player = new SerializedObject(contents.GetComponent<Player>());
                    player.FindProperty("stateData.attack.shotClip").objectReferenceValue = single;
                    player.FindProperty("stateData.attack.autoShotClip").objectReferenceValue = automatic;
                    player.ApplyModifiedPropertiesWithoutUndo();
                    animator.runtimeAnimatorController = controller;
                    PrefabUtility.SaveAsPrefabAsset(contents, prefabPath, out bool savedPrefab);
                    if (!savedPrefab) throw new InvalidOperationException("Could not save player prefab.");
                }
                finally { PrefabUtility.UnloadPrefabContents(contents); }

                states[0].motion = states[1].motion = single;
                states[2].motion = states[3].motion = automatic;
                // A generic-only clip does not behave like a transparent Humanoid
                // state: it pulls the masked upper body toward a default pose.
                states[4].motion = null;
                foreach (AnimatorState state in states.Take(5)) EditorUtility.SetDirty(state);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssetIfDirty(controller);
                string referencePath = ReferenceName;
                int maskIndex = -1;
                for (int i = 0; i < mask.transformCount; i++)
                    if (mask.GetTransformPath(i) == referencePath) { maskIndex = i; break; }
                if (maskIndex < 0) { maskIndex = mask.transformCount; mask.transformCount++; }
                mask.SetTransformPath(maskIndex, referencePath);
                mask.SetTransformActive(maskIndex, true);
                EditorUtility.SetDirty(mask);
                AssetDatabase.SaveAssetIfDirty(mask);
                DeleteUnusedNeutral(outputFolder);
            }
            finally
            {
                foreach (AnimationClip clip in new[] { single, automatic })
                    if (clip != null && !EditorUtility.IsPersistent(clip)) Object.DestroyImmediate(clip);
                if (sampleRoot != null) Object.DestroyImmediate(sampleRoot);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>Update only the host binding on an already installed prefab.</summary>
        public static void BindInstalledPrefab(string prefabPath)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play Mode before authoring.");
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Animator animator = contents.GetComponentInChildren<Animator>(true);
                PlayerShotPoseConstraint constraint = animator.GetComponentInChildren<PlayerShotPoseConstraint>(true);
                if (constraint == null) throw new InvalidOperationException("Install ShotPoseRig first.");
                constraint.data.activeWeight = 0f;
                BindShotPoseHost(animator, constraint);
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath, out bool saved);
                if (!saved) throw new InvalidOperationException("Could not save player prefab.");
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        /// <summary>Repair host/Empty bindings without rebaking either source clip.</summary>
        public static void RepairInstalledAssets(string prefabPath, string controllerPath, string outputFolder)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Exit Play Mode before authoring.");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null) throw new ArgumentException("Need the installed player controller.");
            AnimatorState empty = controller.layers.Single(layer => layer.name == "UpperBody Shooting")
                .stateMachine.states.Single(child => child.state.name == "Empty").state;
            if (!empty.writeDefaultValues)
                throw new InvalidOperationException("Empty must write defaults to clear unused reference curves.");
            BindInstalledPrefab(prefabPath);
            empty.motion = null;
            EditorUtility.SetDirty(empty);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);
            DeleteUnusedNeutral(outputFolder);
        }

        private static void DeleteUnusedNeutral(string outputFolder)
        {
            string path = OutputPath(outputFolder, "ShotPoseNeutral.anim");
            AnimationClip neutral = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (neutral == null) return;
            if (AssetImporter.GetAtPath(path).userData != "PlayerShotPose:v1|neutral")
                throw new InvalidOperationException("Refusing to delete an unowned neutral clip: " + path);
            string[] otherAssets = AssetDatabase.GetAllAssetPaths().Where(assetPath => assetPath != path
                && assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                && !AssetDatabase.IsValidFolder(assetPath)).ToArray();
            if (AssetDatabase.GetDependencies(otherAssets, false).Contains(path))
                throw new InvalidOperationException("Neutral clip is still referenced; retained: " + path);
            if (!AssetDatabase.DeleteAsset(path))
                throw new InvalidOperationException("Could not delete unused neutral clip: " + path);
        }

        private static void BindShotPoseHost(Animator animator, PlayerShotPoseConstraint constraint)
        {
            PlayerAimPose host = animator.GetComponent<PlayerAimPose>();
            if (host == null) throw new InvalidOperationException("Animator needs its PlayerAimPose host.");
            var serialized = new SerializedObject(host);
            serialized.FindProperty("shotPose").objectReferenceValue = constraint;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CopyHierarchy(Transform source, Transform target)
        {
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;
            foreach (Transform child in source)
            {
                var copy = new GameObject(child.name).transform;
                copy.SetParent(target, false);
                CopyHierarchy(child, copy);
            }
        }

        private static string OutputPath(string folder, string fileName)
        {
            folder = folder == null ? "" : folder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal)
                || folder.Split('/').Any(segment => segment == "." || segment == ".." || segment.Length == 0))
                throw new ArgumentException("Output folder must be under Assets without relative traversal.");
            return folder + "/" + fileName;
        }

        private static string SourceMarker(AnimationClip source)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId))
                throw new ArgumentException("Need a persistent original source clip.");
            return "PlayerShotPose:v1|source=" + guid + ":" + localId;
        }

        private static void CheckOutput(string path, string marker)
        {
            Object existing = AssetDatabase.LoadMainAssetAtPath(path);
            if ((existing != null || System.IO.File.Exists(path))
                && (!(existing is AnimationClip) || AssetImporter.GetAtPath(path).userData != marker))
                throw new InvalidOperationException("Refusing to overwrite an unrelated asset: " + path);
        }

        private static AnimationClip SaveClip(AnimationClip clip, string path, string marker)
        {
            string[] segments = path.Substring(0, path.LastIndexOf('/')).Split('/');
            string folder = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                if (!AssetDatabase.IsValidFolder(folder + "/" + segments[i])) AssetDatabase.CreateFolder(folder, segments[i]);
                folder += "/" + segments[i];
            }
            clip.hideFlags = HideFlags.None;
            clip.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AnimationClip saved = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (saved == null) { AssetDatabase.CreateAsset(clip, path); saved = clip; }
            else
            {
                EditorUtility.CopySerialized(clip, saved);
                EditorUtility.SetDirty(saved);
                Object.DestroyImmediate(clip);
            }
            AssetImporter importer = AssetImporter.GetAtPath(path);
            importer.userData = marker;
            AssetDatabase.WriteImportSettingsIfDirty(path);
            AssetDatabase.SaveAssetIfDirty(saved);
            return saved;
        }

        private static void AssertSourceCurvesUnchanged(AnimationClip source, AnimationClip result)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(source))
            {
                AnimationCurve original = AnimationUtility.GetEditorCurve(source, binding);
                AnimationCurve baked = AnimationUtility.GetEditorCurve(result, binding);
                if (baked == null || original.preWrapMode != baked.preWrapMode
                    || original.postWrapMode != baked.postWrapMode || !original.keys.SequenceEqual(baked.keys))
                    throw new InvalidOperationException("Baking modified source curve: " + binding.propertyName);
            }
        }

        public static Transform CreateReference(Animator animator)
        {
            RequirePreview(animator);
            if (animator.transform.Find(ReferenceName) != null)
                throw new InvalidOperationException("Source-pose reference already exists.");
            var reference = new GameObject(ReferenceName).transform;
            reference.SetParent(animator.transform, false);
            reference.localPosition = Vector3.zero;
            reference.localRotation = Quaternion.identity;
            reference.localScale = Vector3.zero;
            return reference;
        }

        // All clips start in the identity hemisphere, then keep adjacent sample signs
        // continuous, so the rifle source clips can blend their four float channels.
        public static AnimationClip CreateClip(Animator animator, AnimationClip source,
            Transform reference, float samplesPerSecond = 120f)
        {
            RequireReference(animator, reference);
            if (source == null || source.length <= 0f) throw new ArgumentException("Need a nonempty source clip.");
            if (float.IsNaN(samplesPerSecond) || float.IsInfinity(samplesPerSecond)
                || samplesPerSecond < 1f || samplesPerSecond > 240f)
                throw new ArgumentOutOfRangeException(nameof(samplesPerSecond));
            RigBuilder builder = animator.GetComponent<RigBuilder>();
            if (builder != null && builder.graph.IsValid())
                throw new InvalidOperationException("Clear the PreviewScene RigBuilder before sampling full source poses.");

            RuntimeAnimatorController originalController = animator.runtimeAnimatorController;
            bool originalEvents = animator.fireEvents;
            float originalSpeed = animator.speed;
            Transform[] transforms = animator.GetComponentsInChildren<Transform>(true);
            var positions = new Vector3[transforms.Length];
            var rotations = new Quaternion[transforms.Length];
            var scales = new Vector3[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
            {
                positions[i] = transforms[i].localPosition;
                rotations[i] = transforms[i].localRotation;
                scales[i] = transforms[i].localScale;
            }

            var controller = new AnimatorController { hideFlags = HideFlags.HideAndDontSave };
            controller.AddLayer("Source");
            AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
            AnimatorState state = stateMachine.AddState("Source");
            state.motion = source;
            state.writeDefaultValues = true;
            stateMachine.defaultState = state;
            AnimationClip candidate = null;
            try
            {
                animator.runtimeAnimatorController = controller;
                animator.fireEvents = false;
                animator.speed = 1f;
                int intervals = Mathf.Max(1, Mathf.CeilToInt(source.length * samplesPerSecond));
                var keys = new Keyframe[4][];
                for (int channel = 0; channel < 4; channel++) keys[channel] = new Keyframe[intervals + 1];
                Quaternion previous = Quaternion.identity;
                for (int sample = 0; sample <= intervals; sample++)
                {
                    float phase = sample / (float)intervals;
                    animator.Rebind();
                    animator.Play("Source.Source", 0, Mathf.Min(phase, 0.999999f));
                    animator.Update(0f);
                    Quaternion rotation = Quaternion.Inverse(animator.transform.rotation)
                        * animator.GetBoneTransform(HumanBodyBones.Spine).rotation;
                    if (Quaternion.Dot(previous, rotation) < 0f)
                        rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                    previous = rotation;
                    float time = phase * source.length;
                    for (int channel = 0; channel < 4; channel++)
                        keys[channel][sample] = new Keyframe(time, rotation[channel]);
                }

                candidate = Object.Instantiate(source);
                candidate.name = source.name + " (Shot Pose)";
                candidate.hideFlags = HideFlags.HideAndDontSave;
                AnimationUtility.SetAnimationClipSettings(candidate, AnimationUtility.GetAnimationClipSettings(source));
                string path = AnimationUtility.CalculateTransformPath(reference, animator.transform);
                string[] channels = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z", "m_LocalScale.y" };
                for (int channel = 0; channel < channels.Length; channel++)
                {
                    var curve = new AnimationCurve(keys[channel]);
                    for (int key = 0; key < curve.length; key++)
                    {
                        AnimationUtility.SetKeyLeftTangentMode(curve, key, AnimationUtility.TangentMode.Linear);
                        AnimationUtility.SetKeyRightTangentMode(curve, key, AnimationUtility.TangentMode.Linear);
                    }
                    AnimationUtility.SetEditorCurve(candidate,
                        EditorCurveBinding.FloatCurve(path, typeof(Transform), channels[channel]), curve);
                }
                AnimationUtility.SetEditorCurve(candidate,
                    EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalScale.x"),
                    AnimationCurve.Constant(0f, source.length, 1f));
                return candidate;
            }
            catch
            {
                if (candidate != null) Object.DestroyImmediate(candidate);
                throw;
            }
            finally
            {
                animator.runtimeAnimatorController = originalController;
                animator.fireEvents = originalEvents;
                animator.speed = originalSpeed;
                animator.Rebind();
                for (int i = 0; i < transforms.Length; i++)
                {
                    if (transforms[i] == null) continue;
                    transforms[i].localPosition = positions[i];
                    transforms[i].localRotation = rotations[i];
                    transforms[i].localScale = scales[i];
                }
                Object.DestroyImmediate(state);
                Object.DestroyImmediate(stateMachine);
                Object.DestroyImmediate(controller);
            }
        }

        private static void RequireReference(Animator animator, Transform reference)
        {
            RequirePreview(animator);
            if (reference == null || reference.parent != animator.transform)
                throw new ArgumentException("Reference must be a direct child of this Animator.");
        }

        private static void RequirePreview(Animator animator)
        {
            if (animator == null || !animator.isHuman)
                throw new ArgumentException("Need a Humanoid Animator.");
            if (Application.isPlaying || !EditorSceneManager.IsPreviewScene(animator.gameObject.scene))
                throw new InvalidOperationException("Only non-playing PreviewScene Animators are allowed.");
        }
    }
}

