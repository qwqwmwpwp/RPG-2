using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>消耗数值项：带标签的数值片段，作为消耗效果处理器的解析键。</summary>
    [Serializable]
    public class ConsumeModifier : LabeledNumberFragment
    {
        /// <summary>供 Unity 反序列化使用的默认构造。</summary>
        public ConsumeModifier() { }

        /// <summary>以片段标签、显示标签与数值范围构造。</summary>
        public ConsumeModifier(BackpackTag fragmentTag, string label, float min, float max, bool randomizeOnManifest = true)
            : base(fragmentTag, label, min, max, randomizeOnManifest) { }

        /// <summary>拷贝构造。</summary>
        protected ConsumeModifier(ConsumeModifier source) : base(source) { }

        /// <inheritdoc/>
        public override ItemFragment DeepCopy() => new ConsumeModifier(this);
    }

    /// <summary>装备数值项：带标签的数值片段，供装备属性计算使用。</summary>
    [Serializable]
    public class EquipModifier : LabeledNumberFragment
    {
        /// <summary>供 Unity 反序列化使用的默认构造。</summary>
        public EquipModifier() { }

        /// <summary>以片段标签、显示标签与数值范围构造。</summary>
        public EquipModifier(BackpackTag fragmentTag, string label, float min, float max, bool randomizeOnManifest = true)
            : base(fragmentTag, label, min, max, randomizeOnManifest) { }

        /// <summary>拷贝构造。</summary>
        protected EquipModifier(EquipModifier source) : base(source) { }

        /// <inheritdoc/>
        public override ItemFragment DeepCopy() => new EquipModifier(this);
    }

    /// <summary>消耗片段：持有消耗数值项列表，定义物品被消耗时的效果来源。</summary>
    [Serializable]
    public sealed class ConsumableFragment : InventoryItemFragment
    {
        [SerializeReference] private List<ConsumeModifier> modifiers = new List<ConsumeModifier>(); // 消耗数值项列表。
        [NonSerialized] private ReadOnlyCollection<ConsumeModifier> readOnlyModifiers; // Modifiers 的只读包装缓存，首次访问时惰性创建。

        /// <summary>以空列表构造，标签固定为消耗片段。</summary>
        public ConsumableFragment() : base(FragmentTags.ConsumableFragment) { }

        /// <summary>以消耗数值项集合构造，集合会被逐个深拷贝入列表。</summary>
        public ConsumableFragment(IEnumerable<ConsumeModifier> modifiers) : base(FragmentTags.ConsumableFragment)
        {
            if (modifiers == null) throw new ArgumentNullException(nameof(modifiers));
            this.modifiers = CopyModifiers(modifiers);
        }

        /// <summary>消耗数值项的只读视图；无数据时返回空列表。</summary>
        public IReadOnlyList<ConsumeModifier> Modifiers
        {
            get
            {
                if (modifiers == null) return Array.Empty<ConsumeModifier>();
                if (readOnlyModifiers == null) readOnlyModifiers = modifiers.AsReadOnly();
                return readOnlyModifiers;
            }
        }

        /// <inheritdoc/>
        public override ItemFragment DeepCopy() => new ConsumableFragment(modifiers);

        /// <inheritdoc/>
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.ConsumableFragment, path);
            if (modifiers == null) throw new InvalidOperationException($"{path}.Modifiers 不能为空。");

            for (int i = 0; i < modifiers.Count; i++)
            {
                if (modifiers[i] == null) throw new InvalidOperationException($"{path}.Modifiers[{i}] 为空。");
                modifiers[i].ValidateConfiguration($"{path}.Modifiers[{i}]");
            }
        }

        /// <inheritdoc/>
        internal override void InitializeRuntime(IItemRandomSource randomSource)
        {
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (modifiers[i] == null) throw new InvalidOperationException($"ConsumableFragment.Modifiers[{i}] 为空。");
                modifiers[i].InitializeRuntime(randomSource);
            }
        }

        /// <summary>逐个深拷贝数值项，并校验拷贝结果是独立且同类型的新实例。</summary>
        private static List<ConsumeModifier> CopyModifiers(IEnumerable<ConsumeModifier> source)
        {
            List<ConsumeModifier> result = new List<ConsumeModifier>();
            foreach (ConsumeModifier modifier in source)
            {
                if (modifier == null) throw new ArgumentException("消耗 Modifier 不能为 null。", nameof(source));

                ItemFragment copiedFragment = modifier.DeepCopy();
                if (copiedFragment == null)
                {
                    throw new InvalidOperationException("消耗 Modifier 的 DeepCopy 返回了 null。");
                }

                if (ReferenceEquals(modifier, copiedFragment))
                {
                    throw new InvalidOperationException("消耗 Modifier 的 DeepCopy 返回了源实例。");
                }

                if (copiedFragment.GetType() != modifier.GetType())
                {
                    throw new InvalidOperationException(
                        $"消耗 Modifier 深拷贝类型不一致：{modifier.GetType().Name} -> {copiedFragment.GetType().Name}。");
                }

                result.Add((ConsumeModifier)copiedFragment);
            }

            return result;
        }
    }

    /// <summary>装备片段：持有装备类型、预制体、挂点与装备数值项列表，描述物品被装备时的表现与属性。</summary>
    [Serializable]
    public sealed class EquipmentFragment : InventoryItemFragment
    {
        [SerializeField] private BackpackTag equipmentType; // 装备类型标签。
        [SerializeField] private GameObject equipPrefab; // 装备时实例化的预制体。
        [SerializeField] private string socketAttachPoint; // 装备挂点名称，可为空串。
        [SerializeReference] private List<EquipModifier> modifiers = new List<EquipModifier>(); // 装备数值项列表。
        [NonSerialized] private ReadOnlyCollection<EquipModifier> readOnlyModifiers; // Modifiers 的只读包装缓存，首次访问时惰性创建。

        /// <summary>以默认空列表构造，标签固定为装备片段。</summary>
        public EquipmentFragment() : base(FragmentTags.EquipmentFragment) { }

        /// <summary>以装备类型、预制体、挂点与数值项集合构造，数值项会被逐个深拷贝。</summary>
        public EquipmentFragment(BackpackTag equipmentType, GameObject equipPrefab, string socketAttachPoint,
            IEnumerable<EquipModifier> modifiers) : base(FragmentTags.EquipmentFragment)
        {
            if (modifiers == null) throw new ArgumentNullException(nameof(modifiers));
            this.equipmentType = equipmentType;
            this.equipPrefab = equipPrefab;
            this.socketAttachPoint = socketAttachPoint ?? string.Empty;
            this.modifiers = CopyModifiers(modifiers);
        }

        public BackpackTag EquipmentType => equipmentType; // 装备类型标签。

        public GameObject EquipPrefab => equipPrefab; // 装备时实例化的预制体。

        public string SocketAttachPoint => socketAttachPoint ?? string.Empty; // 装备挂点名称，空值按空串处理。

        /// <summary>装备数值项的只读视图；无数据时返回空列表。</summary>
        public IReadOnlyList<EquipModifier> Modifiers
        {
            get
            {
                if (modifiers == null) return Array.Empty<EquipModifier>();
                if (readOnlyModifiers == null) readOnlyModifiers = modifiers.AsReadOnly();
                return readOnlyModifiers;
            }
        }

        /// <inheritdoc/>
        public override ItemFragment DeepCopy()
        {
            return new EquipmentFragment(equipmentType, equipPrefab, socketAttachPoint, modifiers);
        }

        /// <inheritdoc/>
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.EquipmentFragment, path);
            if (equipmentType.IsEmpty || !equipmentType.IsValid)
            {
                throw new InvalidOperationException($"{path}.EquipmentType 必须是格式合法的非空标签。");
            }

            if (modifiers == null) throw new InvalidOperationException($"{path}.Modifiers 不能为空。");
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (modifiers[i] == null) throw new InvalidOperationException($"{path}.Modifiers[{i}] 为空。");
                modifiers[i].ValidateConfiguration($"{path}.Modifiers[{i}]");
            }
        }

        /// <inheritdoc/>
        internal override void InitializeRuntime(IItemRandomSource randomSource)
        {
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (modifiers[i] == null) throw new InvalidOperationException($"EquipmentFragment.Modifiers[{i}] 为空。");
                modifiers[i].InitializeRuntime(randomSource);
            }
        }

        /// <summary>逐个深拷贝数值项，并校验拷贝结果是独立且同类型的新实例。</summary>
        private static List<EquipModifier> CopyModifiers(IEnumerable<EquipModifier> source)
        {
            List<EquipModifier> result = new List<EquipModifier>();
            foreach (EquipModifier modifier in source)
            {
                if (modifier == null) throw new ArgumentException("装备 Modifier 不能为 null。", nameof(source));

                ItemFragment copiedFragment = modifier.DeepCopy();
                if (copiedFragment == null)
                {
                    throw new InvalidOperationException("装备 Modifier 的 DeepCopy 返回了 null。");
                }

                if (ReferenceEquals(modifier, copiedFragment))
                {
                    throw new InvalidOperationException("装备 Modifier 的 DeepCopy 返回了源实例。");
                }

                if (copiedFragment.GetType() != modifier.GetType())
                {
                    throw new InvalidOperationException(
                        $"装备 Modifier 深拷贝类型不一致：{modifier.GetType().Name} -> {copiedFragment.GetType().Name}。");
                }

                result.Add((EquipModifier)copiedFragment);
            }

            return result;
        }
    }
}
