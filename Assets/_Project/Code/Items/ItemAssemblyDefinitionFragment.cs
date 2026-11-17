using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.Backpack.Items
{
    public static class ItemAssemblySlots
    {
        public const string Magazine = "magazine";
        public const string Muzzle = "muzzle";
        public const string Rail = "rail";
        public const string Optic = "optic";
        public const int MaxSlotIdLength = 64;
        public static bool IsValid(string slotId) => !string.IsNullOrWhiteSpace(slotId) && slotId.Length <= MaxSlotIdLength;
    }

    [Serializable]
    public sealed class ItemSlotDefinition
    {
        [SerializeField] private string slotId;
        [SerializeField] private string displayName;
        [SerializeField] private List<BackpackTag> allowedItemTypes = new List<BackpackTag>();
        [SerializeField] private bool required;
        public string SlotId => slotId;
        public string DisplayName => displayName;
        public IReadOnlyList<BackpackTag> AllowedItemTypes => allowedItemTypes.AsReadOnly();
        public bool Required => required;
        public ItemSlotDefinition() { }
        public ItemSlotDefinition(string slotId, string displayName, IEnumerable<BackpackTag> allowedItemTypes, bool required = false)
        {
            this.slotId = slotId; this.displayName = displayName; this.required = required;
            this.allowedItemTypes = new List<BackpackTag>(allowedItemTypes ?? throw new ArgumentNullException(nameof(allowedItemTypes)));
            Validate(nameof(ItemSlotDefinition));
        }
        public bool Allows(BackpackTag itemType)
        {
            foreach (BackpackTag allowed in allowedItemTypes) if (allowed.MatchesTagExact(itemType)) return true;
            return false;
        }
        internal ItemSlotDefinition Copy() => new ItemSlotDefinition(slotId, displayName, allowedItemTypes, required);
        internal void Validate(string path)
        {
            if (!ItemAssemblySlots.IsValid(slotId) || allowedItemTypes == null)
                throw new InvalidOperationException(path + " 槽标识或允许表无效。");
            var seen = new HashSet<BackpackTag>();
            foreach (BackpackTag type in allowedItemTypes)
            {
                WeaponDefinitionValidation.Tag(type, false, path);
                if (!seen.Add(type)) throw new InvalidOperationException(path + " 允许表重复。");
            }
        }
    }

    [Serializable]
    public sealed class ItemSlotConflict
    {
        [SerializeField] private BackpackTag ownerItemType;
        [SerializeField] private string slotId;
        public BackpackTag OwnerItemType => ownerItemType;
        public string SlotId => slotId;
        public ItemSlotConflict() { }
        public ItemSlotConflict(BackpackTag ownerItemType, string slotId)
        { this.ownerItemType = ownerItemType; this.slotId = slotId; Validate(nameof(ItemSlotConflict)); }
        internal ItemSlotConflict Copy() => new ItemSlotConflict(ownerItemType, slotId);
        internal void Validate(string path)
        {
            WeaponDefinitionValidation.Tag(ownerItemType, false, path);
            if (!ItemAssemblySlots.IsValid(slotId)) throw new InvalidOperationException(path + " 冲突槽标识无效。");
        }
    }

    [Serializable]
    public sealed class ItemAssemblyDefinitionFragment : InventoryItemFragment
    {
        public const int MaxSlotsPerItem = 32;
        [SerializeField] private List<ItemSlotDefinition> slots = new List<ItemSlotDefinition>();
        [SerializeField] private List<BackpackTag> conflictingItemTypes = new List<BackpackTag>();
        [SerializeField] private List<ItemSlotConflict> slotConflicts = new List<ItemSlotConflict>();
        public IReadOnlyList<ItemSlotDefinition> Slots => slots.AsReadOnly();
        public IReadOnlyList<BackpackTag> ConflictingItemTypes => conflictingItemTypes.AsReadOnly();
        public IReadOnlyList<ItemSlotConflict> SlotConflicts => slotConflicts.AsReadOnly();
        public ItemAssemblyDefinitionFragment() : base(FragmentTags.ItemAssemblyDefinition) { }
        public ItemAssemblyDefinitionFragment(IEnumerable<ItemSlotDefinition> slots,
            IEnumerable<BackpackTag> conflictingItemTypes = null, IEnumerable<ItemSlotConflict> slotConflicts = null) : this()
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            foreach (ItemSlotDefinition slot in slots) this.slots.Add(slot?.Copy());
            if (conflictingItemTypes != null) this.conflictingItemTypes.AddRange(conflictingItemTypes);
            if (slotConflicts != null) foreach (ItemSlotConflict conflict in slotConflicts) this.slotConflicts.Add(conflict?.Copy());
            ValidateConfiguration(nameof(ItemAssemblyDefinitionFragment));
        }
        public override ItemFragment DeepCopy() => new ItemAssemblyDefinitionFragment(slots, conflictingItemTypes, slotConflicts);
        internal override void ValidateConfiguration(string path)
        {
            ValidateExpectedTag(FragmentTags.ItemAssemblyDefinition, path);
            if (slots == null || slots.Count > MaxSlotsPerItem || conflictingItemTypes == null || slotConflicts == null)
                throw new InvalidOperationException(path + " 装配配置为空。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (ItemSlotDefinition slot in slots)
            {
                if (slot == null) throw new InvalidOperationException(path + " 槽为空。");
                slot.Validate(path);
                if (!ids.Add(slot.SlotId)) throw new InvalidOperationException(path + " 本地槽标识重复。");
            }
            foreach (BackpackTag type in conflictingItemTypes) WeaponDefinitionValidation.Tag(type, false, path);
            foreach (ItemSlotConflict conflict in slotConflicts)
            {
                if (conflict == null) throw new InvalidOperationException(path + " 冲突配置为空。");
                conflict.Validate(path);
            }
        }
    }
}
