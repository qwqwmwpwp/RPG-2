using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>界面持有的物品值投影。实例关联在生成投影时转换为稳定编号。</summary>
    public sealed class ItemDisplaySnapshot
    {
        private readonly long sourceIdentity;

        private ItemDisplaySnapshot(ItemDisplayData item)
        {
            sourceIdentity = LoadoutRuleSnapshot.IdentityOf(item);
            Id = item.Id;
            DefinitionId = item.DefinitionId;
            RuntimeItemId = item.RuntimeItemId;
            Origin = item.Origin;
            OriginalSize = item.OriginalSize;
            Rotated = item.Rotated;
            Icon = item.Icon;
            Count = item.Count;
            Name = item.Name;
            ContainerType = item.ContainerType;
            ItemType = item.ItemType;
            EquipmentType = item.EquipmentType;
            IsContainer = item.IsContainer;
        }

        private ItemDisplaySnapshot(ItemDisplaySnapshot item, bool rotated)
        {
            sourceIdentity = item.sourceIdentity;
            Id = item.Id;
            DefinitionId = item.DefinitionId;
            RuntimeItemId = item.RuntimeItemId;
            Origin = item.Origin;
            OriginalSize = item.OriginalSize;
            Rotated = rotated;
            Icon = item.Icon;
            Count = item.Count;
            Name = item.Name;
            ContainerType = item.ContainerType;
            ItemType = item.ItemType;
            EquipmentType = item.EquipmentType;
            IsContainer = item.IsContainer;
        }

        public string Id { get; }
        public string DefinitionId { get; }
        public ulong RuntimeItemId { get; }
        public Vector2Int Origin { get; }
        public Vector2Int OriginalSize { get; }
        public bool Rotated { get; }
        public Vector2Int Size => Rotated ? new Vector2Int(OriginalSize.y, OriginalSize.x) : OriginalSize;
        public Sprite Icon { get; }
        public int Count { get; }
        public string Name { get; }
        public ContainerKind ContainerType { get; }
        public BackpackTag ItemType { get; }
        public BackpackTag EquipmentType { get; }
        public bool IsContainer { get; }

        public static ItemDisplaySnapshot Capture(ItemDisplayData item) =>
            item == null ? null : new ItemDisplaySnapshot(item);

        /// <summary>重复采样同一库存条目时，视图沿用当前交互绑定。</summary>
        public bool HasSameSource(ItemDisplaySnapshot other) =>
            other != null && sourceIdentity == other.sourceIdentity;

        public ItemDisplaySnapshot WithRotation(bool rotated) =>
            rotated == Rotated ? this : new ItemDisplaySnapshot(this, rotated);
    }

    /// <summary>界面端点投影，包含同一时刻捕获的显示值与规则值。</summary>
    public sealed class ContainerDisplaySnapshot
    {
        private ContainerDisplaySnapshot(ContainerSnapshot source)
        {
            Id = source.Id;
            Kind = source.Kind;
            Icon = source.Icon;
            EventSource = source.EventSource;
            Revision = source.Revision;
            IsSlot = source.IsSlot;
            RejectContainers = source.RejectContainers;
            IsReady = source.IsReady;
            WithdrawalOnly = source.WithdrawalOnly;
            var items = new List<ItemDisplaySnapshot>(source.Items.Count);
            foreach (ItemDisplayData item in source.Items)
                items.Add(ItemDisplaySnapshot.Capture(item));
            Items = items.AsReadOnly();
            ValidCells = source.ValidCells == null ? null
                : Array.AsReadOnly(new List<Vector2Int>(source.ValidCells).ToArray());
            Rules = LoadoutRuleSnapshot.Capture(source);
        }

        public string Id { get; }
        public ContainerKind Kind { get; }
        public Sprite Icon { get; }
        public IReadOnlyList<ItemDisplaySnapshot> Items { get; }
        public IReadOnlyList<Vector2Int> ValidCells { get; }
        public object EventSource { get; }
        public int Revision { get; }
        public bool IsSlot { get; }
        public bool RejectContainers { get; }
        public bool IsReady { get; }
        public bool WithdrawalOnly { get; }
        public LoadoutRuleSnapshot Rules { get; }

        public static ContainerDisplaySnapshot Capture(ContainerSnapshot source) =>
            source == null ? null : new ContainerDisplaySnapshot(source);
    }
}
