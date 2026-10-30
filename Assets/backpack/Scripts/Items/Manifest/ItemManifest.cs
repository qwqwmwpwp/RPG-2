using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Items
{
    /// <summary>物品清单资产：描述一种物品类型的片段模板，用于创建或恢复运行时物品实例。</summary>
    [CreateAssetMenu(fileName = "ItemManifest", menuName = "RPG4/Backpack/Item Manifest", order = 10)]
    public sealed class ItemManifest : ScriptableObject
    {
        [SerializeField] private BackpackTag itemType; // 物品类型标签。
        [SerializeReference] private List<ItemFragment> fragments = new List<ItemFragment>(); // 片段模板列表。
        [NonSerialized] private ReadOnlyCollection<ItemFragment> readOnlyFragments; // Fragments 的只读包装缓存，首次访问时惰性创建。

        public BackpackTag ItemType => itemType; // 物品类型标签。

        /// <summary>片段模板的只读视图；无数据时返回空列表。</summary>
        public IReadOnlyList<ItemFragment> Fragments
        {
            get
            {
                if (fragments == null) return Array.Empty<ItemFragment>();
                if (readOnlyFragments == null) readOnlyFragments = fragments.AsReadOnly();
                return readOnlyFragments;
            }
        }

        /// <summary>以物品类型与片段模板配置清单，模板会被深拷贝保存。</summary>
        public void Configure(BackpackTag newItemType, IEnumerable<ItemFragment> fragmentTemplates)
        {
            ValidateItemType(newItemType);
            if (fragmentTemplates == null) throw new ArgumentNullException(nameof(fragmentTemplates));

            List<ItemFragment> copies = new List<ItemFragment>();
            int index = 0;
            foreach (ItemFragment fragment in fragmentTemplates)
            {
                string path = $"Fragments[{index}]";
                copies.Add(CopyFragment(fragment, path));
                index++;
            }

            itemType = newItemType;
            fragments = copies;
            readOnlyFragments = null;
        }

        /// <summary>用默认随机源创建无标识的运行时实例。</summary>
        public RuntimeItem CreateRuntimeItem(int initialStackCount = 1)
        {
            return CreateRuntimeItem(UnityItemRandomSource.Instance, ItemId.None, initialStackCount);
        }

        /// <summary>用默认随机源创建带指定标识的运行时实例（服务端入口，标识由 ItemIdGenerator 发放）。</summary>
        public RuntimeItem CreateRuntimeItem(ItemId id, int initialStackCount = 1)
        {
            return CreateRuntimeItem(UnityItemRandomSource.Instance, id, initialStackCount);
        }

        /// <summary>用指定随机源创建无标识的运行时实例。</summary>
        public RuntimeItem CreateRuntimeItem(IItemRandomSource randomSource, int initialStackCount = 1)
        {
            return CreateRuntimeItem(randomSource, ItemId.None, initialStackCount);
        }

        /// <summary>用指定随机源与标识创建运行时实例，克隆片段并执行首次随机化。</summary>
        public RuntimeItem CreateRuntimeItem(IItemRandomSource randomSource, ItemId id, int initialStackCount = 1)
        {
            if (randomSource == null) throw new ArgumentNullException(nameof(randomSource));

            ValidateConfiguration();
            ValidateInitialStackCount(initialStackCount);
            return new RuntimeItem(id, itemType, CloneFragments(true, randomSource), initialStackCount);
        }

        /// <summary>创建无标识的运行时实例，失败时返回 false 并给出原因而不抛异常。</summary>
        public bool TryCreateRuntimeItem(out RuntimeItem item, out string error, int initialStackCount = 1)
        {
            return TryCreateRuntimeItem(UnityItemRandomSource.Instance, ItemId.None, out item, out error, initialStackCount);
        }

        /// <summary>创建带指定标识的运行时实例，失败时返回 false 并给出原因而不抛异常。</summary>
        public bool TryCreateRuntimeItem(ItemId id, out RuntimeItem item, out string error, int initialStackCount = 1)
        {
            return TryCreateRuntimeItem(UnityItemRandomSource.Instance, id, out item, out error, initialStackCount);
        }

        /// <summary>用指定随机源创建无标识的运行时实例，失败时返回 false 并给出原因而不抛异常。</summary>
        public bool TryCreateRuntimeItem(
            IItemRandomSource randomSource,
            out RuntimeItem item,
            out string error,
            int initialStackCount = 1)
        {
            return TryCreateRuntimeItem(randomSource, ItemId.None, out item, out error, initialStackCount);
        }

        /// <summary>用指定随机源与标识创建运行时实例，失败时返回 false 并给出原因而不抛异常。</summary>
        public bool TryCreateRuntimeItem(
            IItemRandomSource randomSource,
            ItemId id,
            out RuntimeItem item,
            out string error,
            int initialStackCount = 1)
        {
            try
            {
                item = CreateRuntimeItem(randomSource, id, initialStackCount);
                error = null;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
            {
                item = null;
                error = exception.Message;
                BackpackLog.Warn(LogModule.Inventory, $"物品清单 '{DisplayName}' 创建运行时实例失败：{error}");
                return false;
            }
        }

        /// <summary>用权威快照恢复运行时实例（客户端入口）：结构克隆自模板、数值来自快照且不执行任何随机化；快照数值须与模板节点一一对应并落在模板范围内，否则整体失败，不返回部分恢复的实例。</summary>
        public bool TryRestoreRuntimeItem(RuntimeItemSnapshot snapshot, out RuntimeItem item, out string error)
            => TryRestoreRuntimeItem(snapshot, null, out item, out error);

        public bool TryRestoreRuntimeItem(RuntimeItemSnapshot snapshot, ItemManifestCatalog catalog, out RuntimeItem item, out string error)
        {
            int count = 0;
            return TryRestoreTree(snapshot, catalog, 0, new HashSet<ulong>(), ref count, out item, out error);
        }

        internal bool TryRestoreTree(RuntimeItemSnapshot snapshot, ItemManifestCatalog catalog, int depth,
            HashSet<ulong> ids, ref int count, out RuntimeItem item, out string error)
        {
            item = null;
            if (depth > RuntimeItemSnapshot.MaxDepth || ++count > RuntimeItemSnapshot.MaxTreeItems
                || (snapshot.ItemId.IsValid && !ids.Add(snapshot.ItemId.Value)) || (depth > 0 && !snapshot.ItemId.IsValid))
            { error = "物品树超限或有重复/无效子标识。"; return false; }
            if (!snapshot.ItemType.MatchesTagExact(itemType))
            {
                error = $"快照物品类型 '{snapshot.ItemType}' 与清单类型 '{itemType}' 不一致。";
                BackpackLog.Warn(LogModule.Inventory, $"物品清单 '{DisplayName}' 恢复快照失败：{error}");
                return false;
            }

            try
            {
                ValidateConfiguration();
                item = new RuntimeItem(snapshot.ItemId, itemType, CloneFragments(false, null), snapshot.StackCount, false);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
            {
                item = null;
                error = exception.Message;
                BackpackLog.Warn(LogModule.Inventory, $"物品清单 '{DisplayName}' 恢复快照失败：{error}");
                return false;
            }

            if (!RuntimeItemSnapshotCodec.TryApplyNumbers(item, snapshot.Numbers, out error))
            {
                item = null;
                BackpackLog.Warn(LogModule.Inventory, $"物品清单 '{DisplayName}' 恢复快照失败：{error}");
                return false;
            }

            if (!TryRestoreItemState(item, snapshot, catalog, depth, ids, ref count, out error))
            { item = null; return false; }

            error = null;
            return true;
        }

        private static bool TryRestoreItemState(RuntimeItem item, RuntimeItemSnapshot snapshot, ItemManifestCatalog catalog,
            int depth, HashSet<ulong> ids, ref int count, out string error)
        {
            error = null;
            if (snapshot.Version == 1)
            {
                // v1首次引入耐久字段时采用默认满耐久；不加载模板初始弹药。v2严格保留记录值。
                return item.WeaponState == null || item.WeaponState.TryRestore(item.WeaponState.Definition.MaxDurability, default, 0UL, catalog, out error);
            }
            if ((item.WeaponState != null) != (snapshot.Weapon != null) || (item.MagazineState != null) != (snapshot.Magazine != null))
            { error = "快照状态类型与清单不一致。"; return false; }
            if (item.MagazineState != null && !item.MagazineState.TryRestore(snapshot.Magazine.Rounds, catalog, out error)) return false;
            if (item.WeaponState == null) return true;
            WeaponStateSnapshot state = snapshot.Weapon;
            if (!item.WeaponState.TryRestore(state.Durability, state.ChamberAmmo, state.ShotSequence, catalog, out error)) return false;
            if (state.Magazine.HasValue)
            {
                if (!TryRestoreChild(state.Magazine.Value, catalog, depth, ids, ref count, out RuntimeItem magazine, out error)) return false;
                if (!item.WeaponState.IsCompatibleMagazine(magazine, catalog)) { error = "不兼容弹匣。"; return false; }
                item.WeaponState.SetMagazine(magazine);
            }
            foreach (AttachmentStateSnapshot attachment in state.Attachments)
            {
                if (!TryRestoreChild(attachment.Item, catalog, depth, ids, ref count, out RuntimeItem child, out error)) return false;
                if (!item.WeaponState.IsCompatibleAttachment(attachment.Slot, child)) { error = "不兼容配件。"; return false; }
                item.WeaponState.SetAttachment(attachment.Slot, child);
            }
            return true;
        }

        private static bool TryRestoreChild(RuntimeItemSnapshot snapshot, ItemManifestCatalog catalog, int depth,
            HashSet<ulong> ids, ref int count, out RuntimeItem item, out string error)
        {
            item = null;
            if (catalog == null || !catalog.TryGet(snapshot.ItemType, out ItemManifest manifest))
            { error = "子物品缺少目录或清单。"; return false; }
            return manifest.TryRestoreTree(snapshot, catalog, depth + 1, ids, ref count, out item, out error);
        }

        /// <summary>按类型返回第一个匹配的片段；无匹配为 null。</summary>
        public T GetFragment<T>() where T : ItemFragment
        {
            if (fragments == null) return null;

            for (int i = 0; i < fragments.Count; i++)
            {
                if (fragments[i] is T match) return match;
            }

            return null;
        }

        /// <summary>按类型与标签精确匹配返回片段；无匹配为 null。</summary>
        public T GetFragment<T>(BackpackTag fragmentTag) where T : ItemFragment
        {
            if (fragmentTag.IsEmpty || fragments == null) return null;

            for (int i = 0; i < fragments.Count; i++)
            {
                if (fragments[i] is T match && match.FragmentTag.MatchesTagExact(fragmentTag))
                {
                    return match;
                }
            }

            return null;
        }

        /// <summary>校验物品类型与全部片段的配置，非法时抛出异常。</summary>
        public void ValidateConfiguration()
        {
            ValidateItemType(itemType);
            if (fragments == null)
            {
                throw new InvalidOperationException("Fragments 列表不能为空。");
            }

            for (int i = 0; i < fragments.Count; i++)
            {
                string path = $"Fragments[{i}]";
                ItemFragment fragment = fragments[i];
                if (fragment == null) throw new InvalidOperationException($"{path} 为空。");
                fragment.ValidateConfiguration(path);
            }
            int stateDefinitions = 0;
            foreach (ItemFragment fragment in fragments)
                if (fragment is WeaponDefinitionFragment || fragment is MagazineDefinitionFragment || fragment is AttachmentDefinitionFragment) stateDefinitions++;
            if (stateDefinitions > 1 || (stateDefinitions > 0 && (GetFragment<StackableFragment>() != null || GetFragment<AmmoDefinitionFragment>() != null)))
                throw new InvalidOperationException("武器、弹匣、配件定义不能共存，也不能配置为堆叠物品。");
        }

        /// <summary>校验初始堆叠数在 1..单堆上限范围内。</summary>
        private void ValidateInitialStackCount(int initialStackCount)
        {
            StackableFragment stackable = GetFragment<StackableFragment>();
            int maxStackSize = stackable?.MaxStackSize ?? 1;
            if (initialStackCount < 1 || initialStackCount > maxStackSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialStackCount), initialStackCount,
                    $"初始堆叠数必须在 1..{maxStackSize} 范围内。");
            }
        }

        /// <summary>按模板顺序克隆片段；initializeRuntime 为 true 时执行首次随机化。</summary>
        private List<ItemFragment> CloneFragments(bool initializeRuntime, IItemRandomSource randomSource)
        {
            List<ItemFragment> runtimeFragments = new List<ItemFragment>(fragments.Count);
            for (int i = 0; i < fragments.Count; i++)
            {
                string path = $"Fragments[{i}]";
                ItemFragment copy = CopyFragment(fragments[i], path);
                if (initializeRuntime)
                {
                    copy.InitializeRuntime(randomSource);
                }

                copy.ValidateConfiguration(path);
                runtimeFragments.Add(copy);
            }

            return runtimeFragments;
        }

        private string DisplayName => string.IsNullOrEmpty(name) ? nameof(ItemManifest) : name; // 用于日志的可读名称，缺省回退为类型名。

        /// <summary>深拷贝单个片段，并校验拷贝结果是独立且同类型的新实例。</summary>
        private static ItemFragment CopyFragment(ItemFragment source, string path)
        {
            if (source == null) throw new InvalidOperationException($"{path} 为空。");
            source.ValidateConfiguration(path);

            ItemFragment copy = source.DeepCopy();
            if (copy == null)
            {
                throw new InvalidOperationException($"{path} 的 DeepCopy 返回了 null。");
            }

            if (ReferenceEquals(source, copy))
            {
                throw new InvalidOperationException($"{path} 的 DeepCopy 返回了源实例。");
            }

            if (copy.GetType() != source.GetType())
            {
                throw new InvalidOperationException(
                    $"{path} 的 DeepCopy 类型不一致：{source.GetType().Name} -> {copy.GetType().Name}。");
            }

            copy.ValidateConfiguration(path);
            return copy;
        }

        /// <summary>校验物品类型标签非空且格式合法。</summary>
        private static void ValidateItemType(BackpackTag candidate)
        {
            if (candidate.IsEmpty || !candidate.IsValid)
            {
                throw new InvalidOperationException("ItemType 必须是格式合法的非空标签。");
            }
        }
    }
}
