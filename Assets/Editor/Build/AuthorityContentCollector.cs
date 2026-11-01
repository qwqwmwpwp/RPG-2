using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Mirror;
using RPG4.Backpack.Items;
using RPG4.Networking;
using RPG4.Networking.Contracts;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace RPG4.Editor.Build
{
    /// <summary>Target-independent authored projections, never a raw scene/dependency/import hash.</summary>
    internal sealed class AuthorityContentCollector
    {
        [Serializable]
        private sealed class AssemblyReferenceInput { public string reference = string.Empty; }
        private readonly List<AuthorityRecord> records = new List<AuthorityRecord>();
        private readonly HashSet<Object> visited = new HashSet<Object>();
        private readonly HashSet<GameObject> visitedNodes = new HashSet<GameObject>();
        private readonly HashSet<string> collectedFiles = new HashSet<string>(StringComparer.Ordinal);
        private IReadOnlyDictionary<long, ulong> sceneIds;
        internal static AuthoritySnapshot Calculate()
        {
            var collector = new AuthorityContentCollector();
            collector.sceneIds = EntrySceneValidation.ReadAuthoredSceneIds(EntryProtocol.GameplayScene);
            EntrySceneValidation.ReadScene(EntryProtocol.GameplayScene, scene =>
            { foreach (GameObject root in scene.GetRootGameObjects()) collector.CollectRoot(root); });
            foreach (string path in new[] { "Assets/player/OnlinePlayer.prefab", "Assets/player/OnlineNetworkManager.prefab" })
            {
                GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null) throw new BuildFailedException("Authority prefab is unavailable: " + path);
                collector.CollectRoot(root);
            }
            foreach (string path in new[] { "ProjectSettings/TimeManager.asset", "ProjectSettings/DynamicsManager.asset", "ProjectSettings/TagManager.asset" })
            {
                Object settings = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault();
                if (settings == null) throw new BuildFailedException("Authority project settings are unavailable: " + path);
                collector.Project(settings, "settings:" + path, path, Path.GetFileNameWithoutExtension(path));
            }
            collector.CollectSources();
            return new AuthoritySnapshot(collector.records);
        }

        private void CollectRoot(GameObject root)
        {
            foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
            {
                if (!visitedNodes.Add(node.gameObject)) continue;
                Component[] components = node.GetComponents<Component>();
                foreach (Component component in components)
                {
                    if (component == null) throw new BuildFailedException("Missing script in authority scene/prefab.");
                    if (component is Transform) continue;
                    if (IsAuthority(component)) CollectComponent(component);
                    else if (!AuthorityContentRules.IsPresentation(component))
                        throw new BuildFailedException("Unclassified authority/presentation component: " + component.GetType().FullName);
                }
            }
        }
        private static bool IsAuthority(Component component) => component is Rigidbody || component is BoxCollider
            || component is SphereCollider || component is CapsuleCollider || component is MeshCollider
            || AuthorityContentRules.Components.Contains(component.GetType().FullName ?? string.Empty);

        private void CollectComponent(Component component)
        {
            if (!visited.Add(component)) return;
            CollectTransform(component.transform);
            Project(component, Key(component), Source(component), component.GetType().FullName);
            if (component is NetworkIdentity identity)
            {
                NetworkBehaviour[] network = identity.GetComponentsInChildren<NetworkBehaviour>(true);
                if (network.Length > 64) throw new BuildFailedException("Authority NetworkIdentity exceeds Mirror's 64-behaviour mask limit.");
                AddRecord("network-order:" + Key(identity), Source(identity), "MirrorIdentityBehaviourOrder",
                    network.Select((behaviour, index) => new AuthorityField(index.ToString("D4", CultureInfo.InvariantCulture), "Component", Key(behaviour))).ToArray());
            }
        }
        private void CollectTransform(Transform transform)
        {
            if (transform == null || !visited.Add(transform)) return;
            if (transform is RectTransform) throw new BuildFailedException("Authority object cannot depend on a UI transform.");
            if (transform.parent != null) CollectTransform(transform.parent);
            Project(transform, Key(transform), Source(transform), typeof(Transform).FullName);
            AddRecord(Key(transform.gameObject), Source(transform), "AuthorityObject",
                new[] { new AuthorityField("activeSelf", "Boolean", transform.gameObject.activeSelf ? "true" : "false"),
                    new AuthorityField("layer", "Integer", transform.gameObject.layer.ToString(CultureInfo.InvariantCulture)),
                    new AuthorityField("tag", "String", transform.gameObject.tag) });
        }

        private void Project(Object value, string key, string source, string owner)
        {
            var fields = new List<AuthorityField>();
            using (var serialized = new SerializedObject(value))
            {
                SerializedProperty property = serialized.GetIterator();
                bool enter = true;
                while (property.Next(enter))
                {
                    enter = false;
                    if (AuthorityContentRules.Exclude(owner, property.propertyPath)) continue;
                    switch (property.propertyType)
                    {
                        case SerializedPropertyType.Generic: enter = true; continue;
                        case SerializedPropertyType.ManagedReference:
                            string full = property.managedReferenceFullTypename;
                            string type = full.Substring(full.IndexOf(' ') + 1);
                            if (!type.StartsWith("RPG4.Backpack.Items.", StringComparison.Ordinal)
                                || !AuthorityContentRules.ManagedTypes.Contains(type.Substring("RPG4.Backpack.Items.".Length)))
                                throw new BuildFailedException("Unsupported authority item fragment: " + type);
                            fields.Add(new AuthorityField(property.propertyPath, "ManagedType", type)); enter = true; continue;
                        case SerializedPropertyType.ObjectReference:
                            fields.Add(new AuthorityField(property.propertyPath, "Object", Reference(property.objectReferenceValue))); continue;
                        default:
                            fields.Add(new AuthorityField(property.propertyPath, property.propertyType.ToString(), Scalar(property))); continue;
                    }
                }
            }
            if (value is NetworkIdentity identity && !EditorUtility.IsPersistent(identity))
            {
                long fileId = checked((long)GlobalObjectId.GetGlobalObjectIdSlow(identity).targetObjectId);
                if (!sceneIds.TryGetValue(fileId, out ulong sceneId)) throw new BuildFailedException("Authored NetworkIdentity scene ID mapping is unavailable.");
                fields.Add(new AuthorityField("sceneId", "UInt64", sceneId.ToString(CultureInfo.InvariantCulture)));
            }
            AddRecord(key, source, owner, fields.ToArray());
        }

        private string Reference(Object value)
        {
            if (value == null) return "null";
            if (value is Transform transform) CollectTransform(transform);
            else if (value is Component component)
            {
                if (!IsAuthority(component)) throw new BuildFailedException("Unsupported authority component reference: " + component.GetType().FullName);
                CollectComponent(component);
            }
            else if (value is GameObject root)
            {
                // An explicitly referenced empty spawn/authority anchor still contributes its pose and object state.
                CollectTransform(root.transform);
                CollectRoot(root);
            }
            else if (value is ItemManifestCatalog catalog)
            {
                if (visited.Add(catalog))
                {
                    var tags = new HashSet<string>(StringComparer.Ordinal);
                    foreach (ItemManifest item in catalog.Manifests)
                        if (item == null || item.ItemType.IsEmpty || !item.ItemType.IsValid || !tags.Add(item.ItemType.Name))
                            throw new BuildFailedException("Authority item catalog contains null/invalid/duplicate tags.");
                    Project(catalog, Key(catalog), Source(catalog), catalog.GetType().FullName);
                }
            }
            else if (value is ItemManifest item)
            {
                if (visited.Add(item))
                {
                    item.ValidateConfiguration();
                    Project(item, Key(item), Source(item), item.GetType().FullName);
                }
            }
            else if (value is PhysicMaterial material)
            { if (visited.Add(material)) Project(material, Key(material), Source(material), material.GetType().FullName); }
            else if (value is Mesh)
            {
                string path = AssetDatabase.GetAssetPath(value);
                if (path != "Library/unity default resources" && path != "Resources/unity_builtin_extra")
                    throw new BuildFailedException("External collision mesh requires an explicit geometry projection rule: " + path);
            }
            else throw new BuildFailedException("Unsupported authority asset reference: " + value.GetType().FullName);
            return Key(value);
        }

        private static string Scalar(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.ArraySize: case SerializedPropertyType.Character:
                    return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum: case SerializedPropertyType.LayerMask:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return AuthorityContentCanonical.Number(property.doubleValue);
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Vector2: return Join(property.vector2Value.x, property.vector2Value.y);
                case SerializedPropertyType.Vector3: return Join(property.vector3Value.x, property.vector3Value.y, property.vector3Value.z);
                case SerializedPropertyType.Vector4: return Join(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                case SerializedPropertyType.Quaternion: return Join(property.quaternionValue.x, property.quaternionValue.y, property.quaternionValue.z, property.quaternionValue.w);
                case SerializedPropertyType.Vector2Int: return Join(property.vector2IntValue.x, property.vector2IntValue.y);
                case SerializedPropertyType.Vector3Int: return Join(property.vector3IntValue.x, property.vector3IntValue.y, property.vector3IntValue.z);
                case SerializedPropertyType.Bounds:
                    Bounds bounds = property.boundsValue;
                    return Join(bounds.center.x, bounds.center.y, bounds.center.z, bounds.extents.x, bounds.extents.y, bounds.extents.z);
                default: throw new BuildFailedException("Unsupported authority serialized property: " + property.propertyPath + " (" + property.propertyType + ")");
            }
        }
        private static string Join(params double[] values) => string.Join(",", values.Select(AuthorityContentCanonical.Number));
        private static string Key(Object value)
        {
            if (!EditorUtility.IsPersistent(value))
            {
                GlobalObjectId scene = GlobalObjectId.GetGlobalObjectIdSlow(value);
                if (scene.targetObjectId == 0 || scene.assetGUID.ToString() == new string('0', 32))
                    throw new BuildFailedException("Authority scene reference has no persistent source identity.");
                return scene.ToString(); // Includes targetPrefabId, keeping distinct prefab instances distinct.
            }
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id) && !string.IsNullOrEmpty(guid) && id != 0)
                return "asset:" + guid + ":" + id.ToString(CultureInfo.InvariantCulture);
            GlobalObjectId global = GlobalObjectId.GetGlobalObjectIdSlow(value);
            if (global.targetObjectId == 0 || global.assetGUID.ToString() == new string('0', 32))
                throw new BuildFailedException("Authority reference has no persistent source identity.");
            return global.ToString();
        }
        private static string Source(Object value)
        {
            if (!EditorUtility.IsPersistent(value) && value is Component component && component.gameObject.scene.IsValid()) return component.gameObject.scene.path.Replace('\\', '/');
            return AssetDatabase.GetAssetPath(value).Replace('\\', '/');
        }
        private void AddRecord(string key, string source, string type, AuthorityField[] fields) =>
            records.Add(new AuthorityRecord { key = key, source = source, type = type, fields = fields });

        private void CollectSources()
        {
            foreach (string root in AuthorityContentRules.SourceRoots)
            {
                if (!Directory.Exists(root)) throw new BuildFailedException("Authority source root is missing: " + root);
                foreach (string source in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string path = source.Replace('\\', '/');
                    if (AuthorityContentRules.ExcludeSourcePath(path) || AuthorityContentRules.ExcludedSourceNames.Contains(Path.GetFileName(path))) continue;
                    CollectSource(path);
                }
                foreach (string pattern in new[] { "*.asmdef", "*.asmref" })
                    foreach (string source in Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
                    {
                        string path = source.Replace('\\', '/');
                        if (!AuthorityContentRules.ExcludeSourcePath(path)) CollectAssemblyInput(path);
                    }
            }
            foreach (string path in new[] { "Assets/Scripts/Input/PlayerInput.cs" })
                CollectSource(path);
            foreach (string path in AuthorityContentRules.AdditionalAssemblyInputs) CollectAssemblyInput(path);
            foreach (string path in AuthorityContentRules.SharedBinaries)
                AddRecord("binary:" + path, path, "SharedCompilationDependency",
                    new[] { new AuthorityField("sha256", "Bytes", AuthorityContentCanonical.Hash(File.ReadAllBytes(path))),
                        new AuthorityField("importerSha256", "UTF8-LF", AuthorityContentCanonical.TextHash(File.ReadAllText(path + ".meta", new UTF8Encoding(false, true)))) });
        }

        private void CollectAssemblyInput(string path)
        {
            if (!collectedFiles.Add(path)) return;
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) throw new BuildFailedException("Authority assembly metadata is unavailable: " + path);
            string text = File.ReadAllText(path, new UTF8Encoding(false, true));
            AddRecord("assembly:" + path, path, "SharedAssemblyDefinition", new[] {
                new AuthorityField("sha256", "UTF8-LF", AuthorityContentCanonical.TextHash(text)),
                new AuthorityField("assetGuid", "GUID", guid) });
            if (path.EndsWith(".asmref", StringComparison.Ordinal))
            {
                AssemblyReferenceInput input = JsonUtility.FromJson<AssemblyReferenceInput>(text);
                if (input == null || input.reference == null || !input.reference.StartsWith("GUID:", StringComparison.Ordinal))
                    throw new BuildFailedException("Authority assembly references must use a stable GUID: " + path);
                string target = AssetDatabase.GUIDToAssetPath(input.reference.Substring(5)).Replace('\\', '/');
                if (!target.EndsWith(".asmdef", StringComparison.Ordinal))
                    throw new BuildFailedException("Authority assembly reference target is unavailable: " + path);
                CollectAssemblyInput(target);
            }
        }

        private void CollectSource(string path)
        {
            if (!collectedFiles.Add(path)) return;
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (script == null) throw new BuildFailedException("Authority source/import metadata is unavailable: " + path);
            foreach (string line in File.ReadAllLines(path + ".meta"))
                if (line.TrimStart().StartsWith("defaultReferences:", StringComparison.Ordinal) && line.Trim() != "defaultReferences: []")
                    throw new BuildFailedException("Nonempty authority script default references require an explicit projection rule: " + path);
            AddRecord("source:" + path, path, "SharedCompatibilitySource", new[] {
                new AuthorityField("sha256", "UTF8-LF", AuthorityContentCanonical.TextHash(File.ReadAllText(path, new UTF8Encoding(false, true)))),
                new AuthorityField("scriptGuid", "GUID", AssetDatabase.AssetPathToGUID(path)),
                new AuthorityField("executionOrder", "Integer", MonoImporter.GetExecutionOrder(script).ToString(CultureInfo.InvariantCulture)) });
        }
    }
}
