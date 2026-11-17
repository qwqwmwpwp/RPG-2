using System;
using UnityEditor;
using UnityEngine;

namespace RPG4.CharacterAnimation.Editor
{
    /// <summary>Independent IK validation and combined presentation binding validation.</summary>
    public static class CharacterIKAuthoring
    {
        private const string ProfileFolder = "Assets/_Project/Code/CharacterAnimation";

        [MenuItem("RPG4/Animation/Validate Character IK")]
        public static void ValidateAll()
        {
            ValidateAllProfiles();
            CharacterAnimationAuthoring.ValidateBoundary();
            Debug.Log("Character IK profiles and module boundaries are valid.");
        }

        internal static void ValidateAllProfiles()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:CharacterIKProfile", new[] { ProfileFolder }))
                ValidateProfile(AssetDatabase.LoadAssetAtPath<CharacterIKProfile>(AssetDatabase.GUIDToAssetPath(guid)));
        }

        public static void ValidateProfile(CharacterIKProfile profile)
        {
            if (profile == null) throw new InvalidOperationException("需要 IK 配置资产。");
            string error = profile.ValidateConfiguration();
            if (error != null) throw new InvalidOperationException(profile.name + ": " + error);
        }

        [MenuItem("RPG4/Animation/Validate Character Presentation")]
        public static void ValidatePresentation()
        {
            CharacterAnimationAuthoring.ValidateAll();
            ValidateAllProfiles();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/Prefabs" }))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var system in prefab.GetComponentsInChildren<CharacterPresentationSystem>(true))
                    ValidateBindings(system);
            }
            Debug.Log("Character animation, IK and combined presentation bindings are valid.");
        }

        public static void ValidateBindings(CharacterPresentationSystem system)
        {
            string error = system.ValidateBindings();
            if (error != null) throw new InvalidOperationException(system.name + ": " + error);
        }
    }

    [CustomEditor(typeof(CharacterIKProfile))]
    internal sealed class CharacterIKProfileInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("独立 IK 配置。选择参与的 IK 模块；阶段按归一化进度递增，描述武器挂点和左手倍率。与动画配合时，由角色表现总系统校验动作映射和绑定。求值顺序：ShotPose → BodyAim → Weapon → SupportHand。", MessageType.Info);
            DrawDefaultInspector();
            if (GUILayout.Button("验证 IK 配置与阶段"))
            {
                CharacterIKAuthoring.ValidateProfile((CharacterIKProfile)target);
                Debug.Log("独立 IK 配置有效。", target);
            }
        }
    }

    [CustomEditor(typeof(CharacterPresentationSystem))]
    internal sealed class CharacterPresentationSystemInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("组合入口分别绑定动画配置与 IK 配置，并统一管理动画图生命周期。动画和 IK 可通过各自独立系统使用；组合后的动作映射在此校验。", MessageType.Info);
            DrawDefaultInspector();
            if (GUILayout.Button("验证动画、IK 与组合绑定"))
            {
                CharacterIKAuthoring.ValidateBindings((CharacterPresentationSystem)target);
                Debug.Log("角色表现组合绑定有效。", target);
            }
        }
    }
}
