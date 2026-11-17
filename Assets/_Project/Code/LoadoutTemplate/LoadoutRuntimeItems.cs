using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>物品实例身份和模板签名共用入口，不持有旧网格库存。</summary>
    public static class LoadoutRuntimeItems
    {
        public static ItemIdGenerator SharedItemIds { get; } = new ItemIdGenerator();
        private static readonly HashSet<ItemId> reserved = new HashSet<ItemId>();
        internal static void Reserve(ItemId id) => reserved.Add(id);
        internal static void Release(ItemId id) => reserved.Remove(id);
        internal static bool IsReserved(RuntimeItem item)
        {
            if (item == null) return false;
            foreach (RuntimeItem node in ItemAssemblyTraversal.Enumerate(item))
                if (reserved.Contains(node.ItemId)) return true;
            return false;
        }
        internal static bool IsReserved(ItemDisplayData item)
        {
            if (item == null) return false;
            if (IsReserved(item.RuntimeItem)) return true;
            if (item.Storage != null) foreach (ItemDisplayData child in item.Storage.Items)
                if (IsReserved(child)) return true;
            return false;
        }
        internal static bool TryClone(RuntimeItem item, ItemManifestCatalog catalog, out RuntimeItem clone, out string error)
        {
            clone = null;
            if (item == null || catalog == null || !catalog.TryGet(item.ItemType, out ItemManifest manifest))
            { error = "运行物品缺少授权清单。"; return false; }
            if (!RuntimeItemSnapshotCodec.TryCapture(item, out RuntimeItemSnapshot snapshot, out error)
                || !manifest.TryRestoreRuntimeItem(snapshot, catalog, out clone, out error)) return false;
            var revisions = new Dictionary<ItemId, RuntimeItem>();
            foreach (RuntimeItem node in ItemAssemblyTraversal.Enumerate(item)) revisions.Add(node.ItemId, node);
            foreach (RuntimeItem node in ItemAssemblyTraversal.Enumerate(clone))
                if (revisions.TryGetValue(node.ItemId, out RuntimeItem old)) node.RestoreRevisions(old.Revision, old.StructureRevision);
            return true;
        }
        internal static string ManifestSignature(ItemManifest manifest)
        {
            if (manifest == null) return string.Empty;
            var text = new StringBuilder(manifest.ItemType.Name);
            string failurePath = manifest.ItemType.Name;
            try
            {
                for (int i = 0; i < manifest.Fragments.Count; i++)
                    AppendValue(text, manifest.Fragments[i], $"{manifest.ItemType.Name}.Fragments[{i}]", ref failurePath);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"物品清单签名生成失败（{failurePath}），{exception.Message}", exception);
            }
            return text.ToString();
        }
        // 只读取序列化配置，避免 JsonUtility 的进程内 instanceID 进入跨端配置签名。
        private static void AppendValue(StringBuilder text, object value, string path, ref string failurePath)
        {
            failurePath = path;
            // Unity 的失效对象仍可能持有托管引用，统一为空值后再写类型和名称。
            if (value == null || value is UnityEngine.Object unityObject && unityObject == null)
            {
                text.Append("null;");
                return;
            }
            Type type = value.GetType(); text.Append(type.FullName).Append(':');
            if (value is UnityEngine.Object asset)
            {
                string assetName = asset.name;
                text.Append(assetName.Length).Append(':').Append(assetName).Append(';');
                return;
            }
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal)
            { string scalar = Convert.ToString(value, CultureInfo.InvariantCulture); text.Append(scalar.Length).Append(':').Append(scalar).Append(';'); return; }
            if (value is IEnumerable list)
            {
                int index = 0;
                foreach (object child in list)
                {
                    AppendValue(text, child, $"{path}[{index}]", ref failurePath);
                    failurePath = path;
                    index++;
                }
                text.Append("end;");
                return;
            }
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                FieldInfo[] fields = current.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
                foreach (FieldInfo field in fields)
                {
                    if (field.IsNotSerialized || !field.IsPublic && !Attribute.IsDefined(field, typeof(SerializeField))
                        && !Attribute.IsDefined(field, typeof(SerializeReference))) continue;
                    failurePath = path + "." + field.Name;
                    text.Append(field.Name).Append('=');
                    AppendValue(text, field.GetValue(value), failurePath, ref failurePath);
                    failurePath = path;
                }
            }
            text.Append("end;");
        }
    }

    /// <summary>效果在全部库存候选准备完成后提交；失败也须可以回滚，Publish 不得重入提交。</summary>
    public interface IReservedConsumeEffect
    {
        bool TryCommit(out string error);
        void Rollback();
        void Publish();
    }
}
