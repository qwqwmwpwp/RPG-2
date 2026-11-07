using System;
using UnityEngine;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>网格占位片段：定义物品在库存网格中的占位尺寸与留白。</summary>
    [Serializable]
    public sealed class GridFragment : ItemFragment
    {
        [SerializeField, Min(1)] private int width = 1; // 占位宽度（格）。
        [SerializeField, Min(1)] private int height = 1; // 占位高度（格）。
        [SerializeField, Min(0f)] private float padding; // 占位留白，仅表现层使用。

        /// <summary>以默认 1×1 尺寸构造。</summary>
        public GridFragment() : base(FragmentTags.GridFragment) { }

        /// <summary>以宽度、高度与留白构造；尺寸非正或留白非有限非负时抛出异常。</summary>
        public GridFragment(int width, int height, float padding = 0f) : base(FragmentTags.GridFragment)
        {
            if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));
            if (float.IsNaN(padding) || float.IsInfinity(padding) || padding < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(padding));
            }

            this.width = width;
            this.height = height;
            this.padding = padding;
        }

        public int Width => width; // 占位宽度（格）。

        public int Height => height; // 占位高度（格）。

        public float Padding => padding; // 占位留白。

        public Vector2Int Size => new Vector2Int(width, height); // 占位尺寸。

        /// <inheritdoc/>
        public override ItemFragment DeepCopy() => new GridFragment(width, height, padding);

        /// <inheritdoc/>
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.GridFragment, path);
            if (width < 1) throw new InvalidOperationException($"{path}.Width 必须大于 0。");
            if (height < 1) throw new InvalidOperationException($"{path}.Height 必须大于 0。");
            if (float.IsNaN(padding) || float.IsInfinity(padding) || padding < 0f)
            {
                throw new InvalidOperationException($"{path}.Padding 必须是非负有限数。");
            }
        }
    }

    /// <summary>图像片段：定义物品的图标与图标显示尺寸。</summary>
    [Serializable]
    public sealed class ImageFragment : InventoryItemFragment
    {
        [SerializeField] private Sprite icon; // 物品图标。
        [SerializeField] private Vector2 iconDimensions = new Vector2(44f, 44f); // 图标显示尺寸。

        /// <summary>以默认图标尺寸构造。</summary>
        public ImageFragment() : base(FragmentTags.IconFragment) { }

        /// <summary>以图标与图标尺寸构造；尺寸含非有限数或负数时抛出异常。</summary>
        public ImageFragment(Sprite icon, Vector2 iconDimensions) : base(FragmentTags.IconFragment)
        {
            if (float.IsNaN(iconDimensions.x) || float.IsInfinity(iconDimensions.x)
                || float.IsNaN(iconDimensions.y) || float.IsInfinity(iconDimensions.y)
                || iconDimensions.x < 0f || iconDimensions.y < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(iconDimensions));
            }

            this.icon = icon;
            this.iconDimensions = iconDimensions;
        }

        public Sprite Icon => icon; // 物品图标。

        public Vector2 IconDimensions => iconDimensions; // 图标显示尺寸。

        /// <inheritdoc/>
        public override ItemFragment DeepCopy() => new ImageFragment(icon, iconDimensions);

        /// <inheritdoc/>
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.IconFragment, path);
            if (float.IsNaN(iconDimensions.x) || float.IsInfinity(iconDimensions.x)
                || float.IsNaN(iconDimensions.y) || float.IsInfinity(iconDimensions.y)
                || iconDimensions.x < 0f || iconDimensions.y < 0f)
            {
                throw new InvalidOperationException($"{path}.IconDimensions 必须由非负有限数组成。");
            }
        }
    }

    /// <summary>文本片段：以自定义标签承载一段说明文本。</summary>
    [Serializable]
    public sealed class TextFragment : InventoryItemFragment
    {
        [SerializeField] private string text; // 说明文本。

        /// <summary>供 Unity 反序列化使用的默认构造。</summary>
        public TextFragment() { }

        /// <summary>以自定义片段标签与文本构造。</summary>
        public TextFragment(BackpackTag fragmentTag, string text) : base(fragmentTag)
        {
            this.text = text ?? string.Empty;
        }

        public string Text => text ?? string.Empty; // 说明文本，空值按空串处理。

        /// <inheritdoc/>
        public override ItemFragment DeepCopy() => new TextFragment(FragmentTag, Text);
    }

    /// <summary>带标签数值片段：可随机化的数值配置，支持显示标签、折叠与小数位控制，是消耗与装备数值项的基类。</summary>
    [Serializable]
    public class LabeledNumberFragment : InventoryItemFragment
    {
        [SerializeField] private string label; // 显示标签。
        [SerializeField] private float value; // 当前数值（运行时随机化结果）。
        [SerializeField] private float min; // 数值下限。
        [SerializeField] private float max; // 数值上限。
        [SerializeField] private bool randomizeOnManifest = true; // 是否在创建运行时实例时随机化。
        [SerializeField] private bool collapseLabel; // 显示时是否折叠标签。
        [SerializeField] private bool collapseValue; // 显示时是否折叠数值。
        [SerializeField, Min(0)] private int minFractionalDigits = 1; // 最小小数位数。
        [SerializeField, Min(0)] private int maxFractionalDigits = 1; // 最大小数位数。

        /// <summary>供 Unity 反序列化使用的默认构造。</summary>
        public LabeledNumberFragment() { }

        /// <summary>以片段标签、显示标签与数值范围构造；范围非有限或最大值小于最小值时抛出异常。</summary>
        public LabeledNumberFragment(BackpackTag fragmentTag, string label, float min, float max, bool randomizeOnManifest = true)
            : base(fragmentTag)
        {
            if (!IsFinite(min)) throw new ArgumentOutOfRangeException(nameof(min));
            if (!IsFinite(max)) throw new ArgumentOutOfRangeException(nameof(max));
            if (max < min) throw new ArgumentException("最大值不能小于最小值。", nameof(max));

            this.label = label ?? string.Empty;
            this.min = min;
            this.max = max;
            this.value = min;
            this.randomizeOnManifest = randomizeOnManifest;
        }

        /// <summary>拷贝构造，复制源片段的全部数值与显示配置。</summary>
        protected LabeledNumberFragment(LabeledNumberFragment source) : base(source.FragmentTag)
        {
            label = source.label;
            value = source.value;
            min = source.min;
            max = source.max;
            randomizeOnManifest = source.randomizeOnManifest;
            collapseLabel = source.collapseLabel;
            collapseValue = source.collapseValue;
            minFractionalDigits = source.minFractionalDigits;
            maxFractionalDigits = source.maxFractionalDigits;
        }

        public string Label => label ?? string.Empty; // 显示标签，空值按空串处理。

        public float Value => value; // 当前数值。

        public float Min => min; // 数值下限。

        public float Max => max; // 数值上限。

        public bool RandomizeOnManifest => randomizeOnManifest; // 是否仍待随机化。

        public bool CollapseLabel => collapseLabel; // 显示时是否折叠标签。

        public bool CollapseValue => collapseValue; // 显示时是否折叠数值。

        public int MinFractionalDigits => minFractionalDigits; // 最小小数位数。

        public int MaxFractionalDigits => maxFractionalDigits; // 最大小数位数。

        /// <inheritdoc/>
        public override ItemFragment DeepCopy() => new LabeledNumberFragment(this);

        /// <inheritdoc/>
        internal override void ValidateConfiguration(string path)
        {
            base.ValidateConfiguration(path);
            if (!IsFinite(value) || !IsFinite(min) || !IsFinite(max))
            {
                throw new InvalidOperationException($"{path} 的 Value/Min/Max 必须是有限数。");
            }

            if (max < min)
            {
                throw new InvalidOperationException($"{path} 的 Max 不能小于 Min。");
            }

            if (minFractionalDigits < 0 || maxFractionalDigits < minFractionalDigits)
            {
                throw new InvalidOperationException($"{path} 的小数位范围无效。");
            }
        }

        /// <inheritdoc/>
        internal override void InitializeRuntime(IItemRandomSource randomSource)
        {
            if (!randomizeOnManifest) return;
            if (randomSource == null) throw new ArgumentNullException(nameof(randomSource));

            float generatedValue = randomSource.Range(min, max);
            if (!IsFinite(generatedValue) || generatedValue < min || generatedValue > max)
            {
                throw new InvalidOperationException(
                    $"随机源为 Fragment '{FragmentTag}' 返回了范围外数值 {generatedValue}，期望范围为 {min}..{max}。");
            }

            value = generatedValue;
            randomizeOnManifest = false;
        }

        /// <summary>用权威数值覆盖运行时数值：数值须落在模板 Min..Max 内，越界或非有限数按可诊断失败处理、不做静默裁剪，写入后清除「待随机化」标记。</summary>
        internal bool TryApplyRuntimeNumber(float candidate, string path, out string error)
        {
            if (!IsFinite(candidate))
            {
                error = $"{path} 的运行时数值 {candidate} 不是有限数。";
                return false;
            }

            if (candidate < min || candidate > max)
            {
                error = $"{path} 的运行时数值 {candidate} 超出模板范围 {min}..{max}。";
                return false;
            }

            value = candidate;
            randomizeOnManifest = false;
            error = null;
            return true;
        }

        /// <summary>数值是否为有限数（非 NaN、非无穷大）。</summary>
        private static bool IsFinite(float candidate)
        {
            return !float.IsNaN(candidate) && !float.IsInfinity(candidate);
        }
    }

    /// <summary>可堆叠片段：定义物品单堆的最大数量。</summary>
    [Serializable]
    public sealed class StackableFragment : ItemFragment
    {
        [SerializeField, Min(1)] private int maxStackSize = 1; // 单堆最大数量。

        /// <summary>以默认单堆上限 1 构造。</summary>
        public StackableFragment() : base(FragmentTags.StackableFragment) { }

        /// <summary>以单堆最大数量构造；小于 1 时抛出异常。</summary>
        public StackableFragment(int maxStackSize) : base(FragmentTags.StackableFragment)
        {
            if (maxStackSize < 1) throw new ArgumentOutOfRangeException(nameof(maxStackSize));
            this.maxStackSize = maxStackSize;
        }

        public int MaxStackSize => maxStackSize; // 单堆最大数量。

        /// <inheritdoc/>
        public override ItemFragment DeepCopy() => new StackableFragment(maxStackSize);

        /// <inheritdoc/>
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.StackableFragment, path);
            if (maxStackSize < 1)
            {
                throw new InvalidOperationException($"{path}.MaxStackSize 必须大于 0。");
            }
        }
    }
}
