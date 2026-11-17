using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    public enum ContainerLayoutMode { AuthoredCells = 0, Rectangle = 1, Coordinates = 2 }
    public enum ContainerKind { None = 0, ChestRig = 1, Pocket = 2, Backpack = 3, SafeBox = 4 }
    public enum CellFeedback { Normal = 0, Hovered = 1, Allowed = 2, Blocked = 3 }
    public enum LoadoutDragKind { Item = 0, Container = 1 }
    public enum LoadoutSlotId { ChestRig, Pocket, Backpack, SafeBox, Helmet, BodyArmor, SecondaryWeapon, MeleeWeapon, PrimaryWeapon1, PrimaryWeapon2 }

    /// <summary>库存条目，保存物品实例、位置及独立储物实例的关联。</summary>
    public sealed class ItemDisplayData
    {
        public ItemDisplayData(string id, Vector2Int origin, Vector2Int size, Sprite icon,
            int count = 1, string name = null, ContainerKind containerType = ContainerKind.None,
            BackpackTag itemType = default, BackpackTag equipmentType = default, BackpackContainerData storage = null,
            string definitionId = null, bool rotated = false, RuntimeItem runtimeItem = null)
        {
            Id = runtimeItem == null ? id ?? string.Empty : "runtime/" + runtimeItem.ItemId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); Origin = origin; OriginalSize = size; Rotated = rotated; Icon = icon; Count = count;
            Name = name ?? string.Empty; ContainerType = containerType; RuntimeItem = runtimeItem;
            ItemType = itemType; EquipmentType = equipmentType; Storage = storage; DefinitionId = definitionId ?? string.Empty;
        }
        public string Id { get; }
        public string DefinitionId { get; }
        public Vector2Int Origin { get; }
        public Vector2Int OriginalSize { get; }
        public bool Rotated { get; }
        public Vector2Int Size => Rotated ? new Vector2Int(OriginalSize.y, OriginalSize.x) : OriginalSize;
        public Sprite Icon { get; }
        public int Count { get; }
        public RuntimeItem RuntimeItem { get; }
        public ulong RuntimeItemId => RuntimeItem?.ItemId.Value ?? 0UL;
        public string Name { get; }
        public ContainerKind ContainerType { get; }
        public BackpackTag ItemType { get; }
        public BackpackTag EquipmentType { get; }
        public BackpackContainerData Storage { get; }
        public bool IsContainer => ContainerType != ContainerKind.None || Storage != null;
        public ItemDisplayData At(Vector2Int origin) => new ItemDisplayData(Id, origin, OriginalSize, Icon, Count,
            Name, ContainerType, ItemType, EquipmentType, Storage, DefinitionId, Rotated, RuntimeItem);
        internal ItemDisplayData WithRuntime(RuntimeItem runtime) => new ItemDisplayData(Id, Origin,
            runtime == null ? OriginalSize : new Vector2Int(runtime.Size.X, runtime.Size.Y), Icon,
            runtime?.StackCount ?? Count, Name, ContainerType, ItemType, EquipmentType, Storage, DefinitionId, Rotated, runtime);
        public ItemDisplayData WithRotation(bool rotated) => new ItemDisplayData(Id, Origin, OriginalSize, Icon, Count,
            Name, ContainerType, ItemType, EquipmentType, Storage, DefinitionId, rotated, RuntimeItem);
    }

    /// <summary>只读端点快照。EventSource 仅用于定向事件路由。</summary>
    public sealed class ContainerSnapshot
    {
        public ContainerSnapshot(string id, ContainerKind kind, Sprite icon,
            IEnumerable<ItemDisplayData> items = null, IEnumerable<Vector2Int> validCells = null,
            object eventSource = null, int revision = 0, bool isSlot = false,
            LoadoutSlotRule rule = null, bool rejectContainers = true, bool isReady = true, bool withdrawalOnly = false)
        {
            Id = id ?? string.Empty; Kind = kind; Icon = icon; EventSource = eventSource;
            WithdrawalOnly = withdrawalOnly; Revision = revision; IsSlot = isSlot; Rule = rule; RejectContainers = rejectContainers; IsReady = isReady;
            Items = Array.AsReadOnly(items == null ? Array.Empty<ItemDisplayData>() : new List<ItemDisplayData>(items).ToArray());
            ValidCells = validCells == null ? null : Array.AsReadOnly(new List<Vector2Int>(validCells).ToArray());
        }
        public string Id { get; }
        public ContainerKind Kind { get; }
        public Sprite Icon { get; }
        public IReadOnlyList<ItemDisplayData> Items { get; }
        public IReadOnlyList<Vector2Int> ValidCells { get; }
        public object EventSource { get; }
        public int Revision { get; }
        public bool IsSlot { get; }
        public LoadoutSlotRule Rule { get; }
        public bool RejectContainers { get; }
        public bool IsReady { get; }
        public bool WithdrawalOnly { get; }
    }

    public interface ILoadoutEndpoint
    {
        object EventSource { get; }
        ContainerSnapshot Snapshot { get; }
        bool TryPrepare(IReadOnlyList<ItemDisplayData> items, out object prepared, out string error);
        void Commit(object prepared);
        void PublishChanged();
    }

    public readonly struct PlacementPreview
    {
        public PlacementPreview(bool allowed, string reason, Vector2Int origin, Vector2Int size)
        { Allowed = allowed; Reason = reason ?? string.Empty; Origin = origin; Size = size; }
        public bool Allowed { get; }
        public string Reason { get; }
        public Vector2Int Origin { get; }
        public Vector2Int Size { get; }
    }

    public readonly struct LoadoutDropIntent
    {
        public LoadoutDropIntent(LoadoutDragKind kind, string sourceContainerId, string targetContainerId,
            string itemId, Vector2Int origin, int sourceVersion, int targetVersion)
            : this(0, kind, sourceContainerId, targetContainerId, itemId, null,
                Vector2Int.zero, origin, Vector2Int.zero, sourceVersion, targetVersion) { }
        public LoadoutDropIntent(int requestId, LoadoutDragKind kind, string sourceContainerId,
            string targetContainerId, string itemId, string targetItemId, Vector2Int sourceOrigin,
            Vector2Int origin, Vector2Int targetItemOrigin, int sourceVersion, int targetVersion, bool? rotated = null)
        {
            RequestId = requestId; Kind = kind; SourceContainerId = sourceContainerId; TargetContainerId = targetContainerId;
            ItemId = itemId; TargetItemId = targetItemId; SourceOrigin = sourceOrigin; Origin = origin;
            Rotated = rotated; TargetItemOrigin = targetItemOrigin; SourceVersion = sourceVersion; TargetVersion = targetVersion;
        }
        public int RequestId { get; }
        public LoadoutDragKind Kind { get; }
        public string SourceContainerId { get; }
        public string TargetContainerId { get; }
        public string ItemId { get; }
        public string TargetItemId { get; }
        public Vector2Int SourceOrigin { get; }
        public Vector2Int Origin { get; }
        public Vector2Int TargetItemOrigin { get; }
        public int SourceVersion { get; }
        public int TargetVersion { get; }
        public bool? Rotated { get; }
        public bool IsSwap => !string.IsNullOrEmpty(TargetItemId);
    }
}
