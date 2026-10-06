using System;
using System.Collections.Generic;
using UnityEngine;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>物品清单目录：把物品类型标签映射到清单，供运行时按类型恢复物品；匹配为标签整串序数相等、不做层级展开，同一标签只保留第一条并给出警告，目录以资产形式显式登记、只读查询，不依赖场景或 Resources 查找。</summary>
    [CreateAssetMenu(fileName = "ItemManifestCatalog", menuName = "RPG4/Backpack/Item Manifest Catalog", order = 11)]
    public sealed class ItemManifestCatalog : ScriptableObject
    {
        [Tooltip("按物品类型标签登记的清单；同一标签只取第一条。")]
        [SerializeField]
        private List<ItemManifest> manifests = new List<ItemManifest>();

        [NonSerialized] private Dictionary<string, ItemManifest> lookup; // 标签 → 清单 的惰性查找表。
        [NonSerialized] private bool lookupDirty = true; // 查找表是否需要重建。

        public int Count => manifests == null ? 0 : manifests.Count; // 已登记的清单数量。

        /// <summary>已登记的清单（只读；枚举顺序即登记顺序）。</summary>
        public IReadOnlyList<ItemManifest> Manifests
        {
            get
            {
                if (manifests == null) return Array.Empty<ItemManifest>();
                return manifests;
            }
        }

        /// <summary>按物品类型标签查找清单；空标签或非法标签一律返回 false。</summary>
        public bool TryGet(BackpackTag itemType, out ItemManifest manifest)
        {
            manifest = null;
            if (itemType.IsEmpty) return false;
            if (!itemType.IsValid) return false;

            BuildLookupIfNeeded();
            return lookup.TryGetValue(itemType.Name, out manifest);
        }

        /// <summary>运行时登记清单（测试与编辑器工具用，正式流程应通过 Inspector 配置资产）。</summary>
        public void Register(ItemManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            if (manifests == null) manifests = new List<ItemManifest>();
            manifests.Add(manifest);
            lookupDirty = true;
        }

        /// <summary>清空全部登记（测试用）。</summary>
        public void Clear()
        {
            if (manifests == null) manifests = new List<ItemManifest>();
            manifests.Clear();
            lookupDirty = true;
        }

        /// <summary>校验登记项，空项与标签非法项会被跳过并记警告、保留其余项可用；全部合法返回 true，否则返回 false 并给出被跳过的项数。</summary>
        public bool TryValidate(out int report)
        {
            report = 0;
            if (manifests == null) return true;

            for (int i = 0; i < manifests.Count; i++)
            {
                ItemManifest manifest = manifests[i];
                if (manifest == null)
                {
                    report++;
                    BackpackLog.Warn(LogModule.Inventory, $"物品清单目录 Manifests[{i}] 为空，已跳过。");
                    continue;
                }

                if (manifest.ItemType.IsEmpty || !manifest.ItemType.IsValid)
                {
                    report++;
                    BackpackLog.Warn(LogModule.Inventory, $"物品清单目录 Manifests[{i}] 的类型标签非法，已跳过。");
                }
            }

            return report == 0;
        }

        /// <summary>按需重建标签查找表，仅保留每个标签的第一条有效登记。</summary>
        private void BuildLookupIfNeeded()
        {
            if (!lookupDirty && lookup != null) return;

            lookup = new Dictionary<string, ItemManifest>(StringComparer.Ordinal);
            if (manifests != null)
            {
                for (int i = 0; i < manifests.Count; i++)
                {
                    ItemManifest manifest = manifests[i];
                    if (manifest == null) continue;
                    if (manifest.ItemType.IsEmpty || !manifest.ItemType.IsValid) continue;

                    string key = manifest.ItemType.Name;
                    if (lookup.ContainsKey(key))
                    {
                        BackpackLog.Warn(
                            LogModule.Inventory,
                            $"物品清单目录中标签 '{key}' 重复登记（Manifests[{i}]），只保留第一条。");
                        continue;
                    }

                    lookup.Add(key, manifest);
                }
            }

            lookupDirty = false;
        }
    }
}
