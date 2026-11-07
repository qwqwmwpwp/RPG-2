using System;
using UnityEngine;

namespace RPG4.Backpack.Core
{
    /// <summary>层级标签：内部只持一个字符串，按 <see cref="Separator"/>（<c>.</c>）分段；值语义、可序列化、不依赖全局注册表。
    /// 整串相等用 <see cref="MatchesTagExact"/>、层级包含用 <see cref="MatchesTag"/>，二者不可混用；
    /// 匹配 API 遇空标签一律 <c>false</c>，而相等契约仍视「空 == 空」为真。</summary>
    [Serializable]
    public struct BackpackTag : IEquatable<BackpackTag>
    {
        public const char Separator = '.'; // 层级分隔符。

        public static readonly BackpackTag Empty = default; // 空标签；匹配 API 遇到它一律返回 false，相等运算仍视两个空标签相等。

        // 字段名固定为 name：Editor 侧 BackpackTagDrawer 依赖 FindPropertyRelative("name") 读写，改名会断掉 Inspector 下拉。
        [SerializeField]
        private string name;

        /// <summary>以原始字符串构造。不做 trim / 大小写转换，空引用等价于 <see cref="Empty"/>。</summary>
        public BackpackTag(string name)
        {
            this.name = name;
        }

        public string Name => name ?? string.Empty; // 标签全名；空标签返回空字符串（非 null，便于拼接与比较）。

        public bool IsEmpty => string.IsNullOrEmpty(name); // 是否为空标签。

        public bool IsValid => IsWellFormed(name, out _); // 是否为格式合法的非空标签。

        /// <summary>按 <see cref="Separator"/> 切分出的层级分段；空标签返回空数组（不返回 null），不做缓存，热点调用点请自行持有结果。</summary>
        public string[] GetSegments()
        {
            return IsEmpty ? Array.Empty<string>() : name.Split(Separator);
        }

        /// <summary>精确匹配：整串序数相等，不做任何层级展开；任一侧为空标签时返回 <c>false</c>。</summary>
        public bool MatchesTagExact(BackpackTag other)
        {
            if (IsEmpty || other.IsEmpty) return false;

            return string.Equals(name, other.name, StringComparison.Ordinal);
        }

        /// <summary>层级匹配：自身等于 parent 或为 parent 的后代时为真（如 <c>GameItems.Equipment.Weapons.Sword</c> 对 <c>GameItems.Equipment.Weapons</c>）；任一侧为空时返回 <c>false</c>。</summary>
        public bool MatchesTag(BackpackTag parent)
        {
            if (parent.IsEmpty || IsEmpty) return false;

            int parentLength = parent.name.Length;
            if (name.Length == parentLength) return string.Equals(name, parent.name, StringComparison.Ordinal);
            if (name.Length < parentLength) return false;
            if (name[parentLength] != Separator) return false;

            // 零分配前缀比较：先过长度与分隔符位，再做定长序数比较。
            return string.CompareOrdinal(name, 0, parent.name, 0, parentLength) == 0;
        }

        /// <summary>格式校验（供 Editor 校验入口使用，运行时构造路径不调用）：非空、不以分隔符开头或结尾、无连续分隔符、分段只允许 ASCII 字母、数字与下划线；失败时给出原因。</summary>
        public static bool IsWellFormed(string rawName, out string reason)
        {
            if (string.IsNullOrEmpty(rawName))
            {
                reason = "标签为空（EmptyTag 不参与注册）";
                return false;
            }

            int segmentStart = 0;
            for (int i = 0; i <= rawName.Length; i++)
            {
                bool atEnd = i == rawName.Length;
                if (!atEnd && rawName[i] != Separator)
                {
                    if (!IsSegmentChar(rawName[i]))
                    {
                        reason = $"第 {i} 位字符 '{rawName[i]}' 非法：分段只允许字母、数字与下划线";
                        return false;
                    }

                    continue;
                }

                if (i == segmentStart)
                {
                    reason = i == 0
                        ? "标签不得以分隔符开头"
                        : "标签不得以分隔符结尾，也不得出现连续分隔符";
                    return false;
                }

                segmentStart = i + 1;
            }

            reason = null;
            return true;
        }

        /// <summary>格式合法则构造标签，否则返回 <see cref="Empty"/> 并给出原因。</summary>
        public static bool TryParse(string rawName, out BackpackTag tag, out string reason)
        {
            if (IsWellFormed(rawName, out reason))
            {
                tag = new BackpackTag(rawName);
                return true;
            }

            tag = Empty;
            return false;
        }

        /// <summary>分段字符是否为允许的 ASCII 字母、数字或下划线。</summary>
        private static bool IsSegmentChar(char value)
        {
            return (value >= 'A' && value <= 'Z')
                || (value >= 'a' && value <= 'z')
                || (value >= '0' && value <= '9')
                || value == '_';
        }

        /// <summary>值相等（C# 契约，自反）：仅比较标签名，空标签与空标签相等；这不是玩法匹配，匹配请用 <see cref="MatchesTagExact"/>。</summary>
        public bool Equals(BackpackTag other)
        {
            return string.Equals(Name, other.Name, StringComparison.Ordinal);
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is BackpackTag other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            // 显式序数哈希，避免区域性差异；与 Equals 口径一致。
            return StringComparer.Ordinal.GetHashCode(Name);
        }

        /// <summary>值相等运算符（C# 契约），语义同 <see cref="Equals(BackpackTag)"/>。</summary>
        public static bool operator ==(BackpackTag left, BackpackTag right)
        {
            return left.Equals(right);
        }

        /// <summary>值不等运算符（C# 契约），语义同 <see cref="Equals(BackpackTag)"/>。</summary>
        public static bool operator !=(BackpackTag left, BackpackTag right)
        {
            return !left.Equals(right);
        }

        /// <summary>输出标签全名；空标签输出空字符串（非 "None"，便于日志里直接拼接）。</summary>
        public override string ToString()
        {
            return Name;
        }
    }
}
