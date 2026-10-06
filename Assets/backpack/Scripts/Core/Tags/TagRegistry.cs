using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.Backpack.Core
{
    /// <summary>标签注册表（ScriptableObject）：只做「合法性来源 + 下拉候选 + 层级枚举」，不是标签数据来源——注册表缺席时匹配照常工作，权威字符串始终是代码常量与资产字段；父级节点无需登记。资产规范位置 <c>Assets/backpack/Data/TagRegistry.asset</c>。</summary>
    [CreateAssetMenu(fileName = "TagRegistry", menuName = "RPG4/Backpack/Tag Registry", order = 1)]
    public class TagRegistry : ScriptableObject
    {
        public const string ResourceName = "TagRegistry"; // 供 Resources.Load 兜底查找的资产名（资产需位于任一 Resources/ 目录下才生效）。

        [Tooltip("全部合法标签（不含父级节点，层级由 '.' 分段推导）。")]
        [SerializeField]
        private List<string> tags = new List<string>();

        private static TagRegistry current;

        private HashSet<string> lookup;
        private List<BackpackTag> allTags;
        private int version;

        /// <summary>当前注册表：显式注入优先（推荐由启动流程赋值 <c>Data/TagRegistry.asset</c>），未注入时尝试 <c>Resources.Load</c>，仍失败则退化为空表；赋值 <c>null</c> 同样落到运行时默认空表。</summary>
        public static TagRegistry Current
        {
            get
            {
                if (current == null)
                {
                    current = ResolveCurrent();
                }

                return current;
            }
            set => current = value != null ? value : CreateRuntimeDefault();
        }

        public int Version => version; // 内容版本号：资产被修改（OnValidate）或显式失效时自增，供 Editor 下拉缓存判新旧。

        /// <summary>全部已登记标签（资产填写顺序，不排序）。</summary>
        public IReadOnlyList<BackpackTag> AllTags
        {
            get
            {
                EnsureCache();
                return allTags;
            }
        }

        /// <summary>标签是否已登记（序数比较，O(1)）；空标签恒为 false。</summary>
        public bool Contains(BackpackTag tag)
        {
            return !tag.IsEmpty && Contains(tag.Name);
        }

        /// <summary>标签名是否已登记（序数比较，O(1)）。</summary>
        public bool Contains(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return false;

            EnsureCache();
            return lookup.Contains(tagName);
        }

        /// <summary>枚举某节点的直接子节点：父节点自身无需登记，无匹配后代时返回空列表；parent 为空标签时返回全部一级分段（顶层命名空间）。</summary>
        public IReadOnlyList<BackpackTag> GetChildren(BackpackTag parent)
        {
            EnsureCache();

            List<BackpackTag> result = new List<BackpackTag>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            if (parent.IsEmpty)
            {
                for (int i = 0; i < allTags.Count; i++)
                {
                    string candidate = FirstSegment(allTags[i].Name);
                    if (candidate != null && seen.Add(candidate))
                    {
                        result.Add(new BackpackTag(candidate));
                    }
                }

                return result;
            }

            string prefix = parent.Name + BackpackTag.Separator;
            for (int i = 0; i < allTags.Count; i++)
            {
                BackpackTag tag = allTags[i];

                // 自身不算是自己的子节点；只有真正的后代才继续拆下一段。
                if (tag.MatchesTagExact(parent) || !tag.MatchesTag(parent)) continue;

                string candidate = NextSegment(tag.Name, prefix.Length);
                if (candidate == null) continue;

                string childName = prefix + candidate;
                if (seen.Add(childName))
                {
                    result.Add(new BackpackTag(childName));
                }
            }

            return result;
        }

        /// <summary>体检注册表自身：空条目 / 重复登记 / 非法格式。</summary>
        public IReadOnlyList<TagValidationIssue> Validate()
        {
            List<TagValidationIssue> issues = new List<TagValidationIssue>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < tags.Count; i++)
            {
                string raw = tags[i];
                string context = $"TagRegistry[{i}]";

                if (string.IsNullOrEmpty(raw))
                {
                    issues.Add(new TagValidationIssue(context, "空条目：请删除或补全该行"));
                    continue;
                }

                if (!seen.Add(raw))
                {
                    issues.Add(new TagValidationIssue(context, $"重复登记：'{raw}' 已在前面出现过"));
                    continue;
                }

                if (!BackpackTag.IsWellFormed(raw, out string reason))
                {
                    issues.Add(new TagValidationIssue(context, $"'{raw}' 格式非法：{reason}"));
                }
            }

            return issues;
        }

        /// <summary>清空并重建内部索引；Editor 侧批量改写资产后调用（运行时通常由 <c>OnValidate</c> 自动触发）。</summary>
        public void InvalidateCache()
        {
            lookup = null;
            allTags = null;
            version++;
        }

        /// <summary>生成一个不落盘的运行时默认实例（空表），用于未注入资产时的兜底。</summary>
        public static TagRegistry CreateRuntimeDefault()
        {
            TagRegistry registry = CreateInstance<TagRegistry>();
            registry.name = "TagRegistry (RuntimeDefault)";
            registry.hideFlags = HideFlags.HideAndDontSave;
            return registry;
        }

        /// <summary>解析当前注册表：先 <c>Resources.Load</c>，缺失则退回运行时默认空表。</summary>
        private static TagRegistry ResolveCurrent()
        {
            TagRegistry fromResources = Resources.Load<TagRegistry>(ResourceName);
            return fromResources != null ? fromResources : CreateRuntimeDefault();
        }

        /// <summary>按需重建查询索引与标签列表（空条目跳过）。</summary>
        private void EnsureCache()
        {
            if (lookup != null) return;

            int capacity = Mathf.Max(tags.Count, 1);
            lookup = new HashSet<string>(StringComparer.Ordinal);
            allTags = new List<BackpackTag>(tags.Count);

            for (int i = 0; i < tags.Count; i++)
            {
                string raw = tags[i];
                if (string.IsNullOrEmpty(raw)) continue;

                lookup.Add(raw);
                allTags.Add(new BackpackTag(raw));
            }

            BackpackLog.Debug(LogModule.Inventory,
                () => $"TagRegistry | 索引重建 | 条目={tags.Count} | 有效={allTags.Count} | 版本={version}");
        }

        /// <summary>取标签名的首段（无分隔符时即全名）。</summary>
        private static string FirstSegment(string tagName)
        {
            int index = tagName.IndexOf(BackpackTag.Separator);
            return index > 0 ? tagName.Substring(0, index) : tagName;
        }

        /// <summary>取从指定偏移开始的下一段标签名；已到末尾返回 <c>null</c>。</summary>
        private static string NextSegment(string tagName, int startIndex)
        {
            if (startIndex >= tagName.Length) return null;

            int index = tagName.IndexOf(BackpackTag.Separator, startIndex);
            return index > startIndex ? tagName.Substring(startIndex, index - startIndex) : tagName.Substring(startIndex);
        }

        /// <summary>资产在 Inspector 被修改时使缓存失效。</summary>
        private void OnValidate()
        {
            InvalidateCache();
        }

        /// <summary>关闭 Domain Reload 时静态字段不会重置，此处显式复位，避免上一局游戏的注册表残留。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStateOnSubsystemRegistration()
        {
            current = null;
        }
    }

    /// <summary>标签体检问题条目：<see cref="Context"/> 指出问题位置（如 <c>TagRegistry[3]</c> 或资产路径），<see cref="Message"/> 为可读的原因与修法。</summary>
    public readonly struct TagValidationIssue
    {
        /// <summary>记录问题位置与原因／修法。</summary>
        public TagValidationIssue(string context, string message)
        {
            Context = context;
            Message = message;
        }

        public string Context { get; } // 问题位置。

        public string Message { get; } // 问题描述。

        /// <summary>调试文本：「位置 | 问题」。</summary>
        public override string ToString()
        {
            return $"{Context} | {Message}";
        }
    }
}
