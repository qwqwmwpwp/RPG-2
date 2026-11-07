using System;
using UnityEngine;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>物品片段基类：以标签标识的可序列化配置块，运行时按需深拷贝并初始化随机数值。</summary>
    [Serializable]
    public abstract class ItemFragment
    {
        [SerializeField] private BackpackTag fragmentTag; // 片段类型标签，决定片段在物品上的身份。

        /// <summary>供 Unity 反序列化使用的默认构造。</summary>
        protected ItemFragment() { }

        /// <summary>以片段标签构造。</summary>
        protected ItemFragment(BackpackTag fragmentTag) { this.fragmentTag = fragmentTag; }

        public BackpackTag FragmentTag => fragmentTag; // 片段类型标签。

        /// <summary>深拷贝出一个独立的片段实例。</summary>
        public abstract ItemFragment DeepCopy();

        /// <summary>校验片段配置，路径用于生成可诊断的错误信息。</summary>
        internal virtual void ValidateConfiguration(string path)
        {
            ValidateTag(path);
        }

        /// <summary>运行时初始化（默认无操作，派生片段可覆写以生成随机数值）。</summary>
        internal virtual void InitializeRuntime(IItemRandomSource randomSource) { }

        /// <summary>校验片段标签与期望标签精确一致，不一致时抛出异常。</summary>
        protected void ValidateExpectedTag(BackpackTag expectedTag, string path)
        {
            ValidateTag(path);
            if (!fragmentTag.MatchesTagExact(expectedTag))
            {
                throw new InvalidOperationException(
                    $"{ResolvePath(path)} 的 FragmentTag 应为 '{expectedTag}'，实际为 '{fragmentTag}'。");
            }
        }

        /// <summary>校验片段标签非空且格式合法。</summary>
        private void ValidateTag(string path)
        {
            string resolvedPath = ResolvePath(path);
            if (fragmentTag.IsEmpty)
            {
                throw new InvalidOperationException($"{resolvedPath} 的 FragmentTag 不能为空。");
            }

            if (!fragmentTag.IsValid)
            {
                throw new InvalidOperationException($"{resolvedPath} 的 FragmentTag '{fragmentTag}' 格式无效。");
            }
        }

        /// <summary>路径为空时回退为类型名，用于可诊断错误信息。</summary>
        private string ResolvePath(string path)
        {
            return string.IsNullOrEmpty(path) ? GetType().Name : path;
        }
    }

    /// <summary>库存物品片段基类：所有需要随物品进入库存的片段均继承它。</summary>
    [Serializable]
    public abstract class InventoryItemFragment : ItemFragment
    {
        /// <summary>供 Unity 反序列化使用的默认构造。</summary>
        protected InventoryItemFragment() { }

        /// <summary>以片段标签构造。</summary>
        protected InventoryItemFragment(BackpackTag fragmentTag) : base(fragmentTag) { }
    }
}
