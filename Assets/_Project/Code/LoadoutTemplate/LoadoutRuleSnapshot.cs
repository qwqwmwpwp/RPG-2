using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>规则计算的值快照。捕获边界之外不保存展示资产、运行物品或储物模型引用。</summary>
    public sealed class LoadoutRuleSnapshot
    {
        private sealed class EndpointIdentity
        {
            internal readonly long Value = Interlocked.Increment(ref nextIdentity);
        }
        private static long nextIdentity;
        private static readonly ConditionalWeakTable<object, EndpointIdentity> identities =
            new ConditionalWeakTable<object, EndpointIdentity>();

        private LoadoutRuleSnapshot(ContainerSnapshot source, IEnumerable<ItemDisplayData> items,
            HashSet<BackpackContainerData> visited, object endpointIdentity = null)
        {
            Id = source.Id; Revision = source.Revision; Kind = source.Kind;
            EndpointToken = IdentityOf(endpointIdentity ?? source.EventSource ?? source);
            IsSlot = source.IsSlot; IsReady = source.IsReady; WithdrawalOnly = source.WithdrawalOnly;
            RejectContainers = source.RejectContainers; Rule = source.Rule == null ? null : source.Rule.CaptureRule();
            ValidCells = Array.AsReadOnly(source.ValidCells == null ? Array.Empty<Vector2Int>()
                : new List<Vector2Int>(source.ValidCells).ToArray());
            var captured = new List<LoadoutRuleItem>();
            foreach (ItemDisplayData item in items) captured.Add(LoadoutRuleItem.Capture(item, visited));
            Items = captured.AsReadOnly();
            DependencySignature = BuildSignature();
        }

        public string Id { get; }
        public int Revision { get; }
        public long EndpointToken { get; }
        public ContainerKind Kind { get; }
        public bool IsSlot { get; }
        public bool IsReady { get; }
        public bool WithdrawalOnly { get; }
        public bool RejectContainers { get; }
        public LoadoutRuleConstraint Rule { get; }
        public IReadOnlyList<Vector2Int> ValidCells { get; }
        public IReadOnlyList<LoadoutRuleItem> Items { get; }
        internal string DependencySignature { get; }

        public static LoadoutRuleSnapshot Capture(ContainerSnapshot source) => source == null ? null
            : new LoadoutRuleSnapshot(source, source.Items, new HashSet<BackpackContainerData>());

        internal static LoadoutRuleSnapshot Capture(ContainerSnapshot source, IEnumerable<ItemDisplayData> items) =>
            new LoadoutRuleSnapshot(source, items, new HashSet<BackpackContainerData>());

        internal static LoadoutRuleSnapshot CaptureEndpoint(ILoadoutEndpoint endpoint, ContainerSnapshot snapshot) =>
            snapshot == null ? null : new LoadoutRuleSnapshot(snapshot, snapshot.Items,
                new HashSet<BackpackContainerData>(), endpoint.EventSource ?? endpoint);

        internal bool Matches(ILoadoutEndpoint endpoint, ContainerSnapshot current)
        {
            LoadoutRuleSnapshot captured = CaptureEndpoint(endpoint, current);
            return captured != null && EndpointToken == captured.EndpointToken
                && string.Equals(DependencySignature, captured.DependencySignature, StringComparison.Ordinal);
        }

        private string BuildSignature()
        {
            var result = new StringBuilder();
            Append(result, Id); Append(result, Revision); Append(result, Kind); Append(result, IsSlot);
            Append(result, IsReady); Append(result, WithdrawalOnly); Append(result, RejectContainers);
            Append(result, Rule?.Signature);
            foreach (Vector2Int cell in ValidCells) { Append(result, cell.x); Append(result, cell.y); }
            Append(result, "items");
            foreach (LoadoutRuleItem item in Items) Append(result, item?.DependencySignature);
            return result.ToString();
        }

        internal static long IdentityOf(object value) => identities.GetValue(value, _ => new EndpointIdentity()).Value;

        internal static void Append(StringBuilder result, object value)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            result.Append(text.Length).Append(':').Append(text).Append(';');
        }
    }

    public sealed class LoadoutRuleConstraint
    {
        internal LoadoutRuleConstraint(LoadoutTagSource source, LoadoutTagMatch match,
            IEnumerable<BackpackTag> allowed, IEnumerable<BackpackTag> denied)
        {
            Source = source; Match = match;
            Allowed = Array.AsReadOnly(new List<BackpackTag>(allowed ?? Array.Empty<BackpackTag>()).ToArray());
            Denied = Array.AsReadOnly(new List<BackpackTag>(denied ?? Array.Empty<BackpackTag>()).ToArray());
            var signature = new StringBuilder();
            LoadoutRuleSnapshot.Append(signature, source); LoadoutRuleSnapshot.Append(signature, match);
            LoadoutRuleSnapshot.Append(signature, Allowed.Count);
            foreach (BackpackTag tag in Allowed) LoadoutRuleSnapshot.Append(signature, tag.Name);
            LoadoutRuleSnapshot.Append(signature, Denied.Count);
            foreach (BackpackTag tag in Denied) LoadoutRuleSnapshot.Append(signature, tag.Name);
            Signature = signature.ToString();
        }
        public LoadoutTagSource Source { get; }
        public LoadoutTagMatch Match { get; }
        public IReadOnlyList<BackpackTag> Allowed { get; }
        public IReadOnlyList<BackpackTag> Denied { get; }
        internal string Signature { get; }
        internal bool Accepts(BackpackTag itemType, BackpackTag equipmentType, out string reason)
        {
            reason = null;
            BackpackTag item = Source == LoadoutTagSource.ItemType ? itemType : equipmentType;
            foreach (BackpackTag denied in Denied)
                if (Matches(item, denied)) { reason = "物品标签命中禁止规则。"; return false; }
            foreach (BackpackTag allowed in Allowed)
                if (Matches(item, allowed)) return true;
            reason = "物品标签不符合该槽位的接收规则。";
            return false;
        }
        private bool Matches(BackpackTag item, BackpackTag rule) => Match == LoadoutTagMatch.Exact
            ? item.MatchesTagExact(rule) : item.MatchesTag(rule);
    }

    public sealed class LoadoutRuleItem
    {
        private LoadoutRuleItem(ItemDisplayData item, HashSet<BackpackContainerData> visited)
        {
            Id = item.Id; DefinitionId = item.DefinitionId; Count = item.Count; Origin = item.Origin;
            OriginalSize = item.OriginalSize; Rotated = item.Rotated; ContainerType = item.ContainerType;
            ItemType = item.ItemType; EquipmentType = item.EquipmentType;
            RuntimeValid = item.RuntimeItem == null || item.RuntimeItem.ItemId.IsValid
                && item.RuntimeItem.StackCount == item.Count
                && item.RuntimeItem.Size.X == item.OriginalSize.x && item.RuntimeItem.Size.Y == item.OriginalSize.y;
            bool reserved = LoadoutRuntimeItems.IsReserved(item.RuntimeItem);
            var dependency = new StringBuilder();
            var runtimeIds = new List<ulong>();
            if (item.RuntimeItem != null)
                foreach (RuntimeItem node in ItemAssemblyTraversal.Enumerate(item.RuntimeItem))
                {
                    runtimeIds.Add(node.ItemId.Value);
                    LoadoutRuleSnapshot.Append(dependency, LoadoutRuleSnapshot.IdentityOf(node));
                    LoadoutRuleSnapshot.Append(dependency, node.ItemId.Value);
                    LoadoutRuleSnapshot.Append(dependency, node.ParentItem?.ItemId.Value ?? 0UL);
                    LoadoutRuleSnapshot.Append(dependency, node.Revision);
                    LoadoutRuleSnapshot.Append(dependency, node.StructureRevision);
                    LoadoutRuleSnapshot.Append(dependency, node.StackCount);
                    LoadoutRuleSnapshot.Append(dependency, node.ItemType.Name);
                    LoadoutRuleSnapshot.Append(dependency, node.Size.X);
                    LoadoutRuleSnapshot.Append(dependency, node.Size.Y);
                }
            RuntimeItemIds = runtimeIds.AsReadOnly();
            RuntimeSignature = dependency.ToString();
            dependency.Clear();
            LoadoutRuleSnapshot.Append(dependency, RuntimeSignature);
            HasStorage = item.Storage != null;
            StorageId = item.Storage?.Id ?? string.Empty;
            StorageRevision = item.Storage?.Revision ?? 0;
            bool storageValid = true;
            var contents = new List<LoadoutRuleItem>();
            if (item.Storage != null)
            {
                if (!visited.Add(item.Storage)) storageValid = false;
                else
                {
                    LoadoutRuleSnapshot.Append(dependency, LoadoutRuleSnapshot.IdentityOf(item.Storage));
                    LoadoutRuleSnapshot.Append(dependency, item.Storage.Kind);
                    LoadoutRuleSnapshot.Append(dependency, item.Storage.RejectContainers);
                    LoadoutRuleSnapshot.Append(dependency, item.Storage.ValidCells.Count);
                    foreach (Vector2Int cell in item.Storage.ValidCells)
                    { LoadoutRuleSnapshot.Append(dependency, cell.x); LoadoutRuleSnapshot.Append(dependency, cell.y); }
                    LoadoutRuleSnapshot.Append(dependency, item.Storage.Items.Count);
                    foreach (ItemDisplayData child in item.Storage.Items)
                    {
                        LoadoutRuleItem captured = Capture(child, visited);
                        contents.Add(captured);
                        reserved |= captured != null && captured.Reserved;
                        storageValid &= captured != null && captured.StorageValid;
                        LoadoutRuleSnapshot.Append(dependency, captured?.DependencySignature);
                    }
                    visited.Remove(item.Storage);
                }
            }
            Reserved = reserved;
            StorageValid = storageValid;
            StorageItems = contents.AsReadOnly();
            StorageSignature = dependency.ToString();
            DependencySignature = BuildSignature();
        }

        private LoadoutRuleItem(LoadoutRuleItem original, Vector2Int origin, bool rotated)
        {
            Id = original.Id; DefinitionId = original.DefinitionId; Count = original.Count; Origin = origin;
            OriginalSize = original.OriginalSize; Rotated = rotated; ContainerType = original.ContainerType;
            ItemType = original.ItemType; EquipmentType = original.EquipmentType; RuntimeValid = original.RuntimeValid;
            Reserved = original.Reserved; RuntimeSignature = original.RuntimeSignature; RuntimeItemIds = original.RuntimeItemIds;
            HasStorage = original.HasStorage; StorageId = original.StorageId; StorageRevision = original.StorageRevision;
            StorageValid = original.StorageValid; StorageItems = original.StorageItems; StorageSignature = original.StorageSignature;
            DependencySignature = BuildSignature();
        }
        public string Id { get; }
        public string DefinitionId { get; }
        public int Count { get; }
        public Vector2Int Origin { get; }
        public Vector2Int OriginalSize { get; }
        public bool Rotated { get; }
        public Vector2Int Size => Rotated ? new Vector2Int(OriginalSize.y, OriginalSize.x) : OriginalSize;
        public ContainerKind ContainerType { get; }
        public BackpackTag ItemType { get; }
        public BackpackTag EquipmentType { get; }
        public bool IsContainer => ContainerType != ContainerKind.None || HasStorage;
        public bool HasStorage { get; }
        public string StorageId { get; }
        public int StorageRevision { get; }
        public IReadOnlyList<LoadoutRuleItem> StorageItems { get; }
        public bool Reserved { get; }
        public IReadOnlyList<ulong> RuntimeItemIds { get; }
        internal bool RuntimeValid { get; }
        internal bool StorageValid { get; }
        internal string RuntimeSignature { get; }
        internal string StorageSignature { get; }
        internal string DependencySignature { get; }
        internal static LoadoutRuleItem Capture(ItemDisplayData item, HashSet<BackpackContainerData> visited) =>
            item == null ? null : new LoadoutRuleItem(item, visited);
        internal LoadoutRuleItem At(Vector2Int origin) => new LoadoutRuleItem(this, origin, Rotated);
        internal LoadoutRuleItem WithRotation(bool rotated) => new LoadoutRuleItem(this, Origin, rotated);
        private string BuildSignature()
        {
            var result = new StringBuilder();
            LoadoutRuleSnapshot.Append(result, Id); LoadoutRuleSnapshot.Append(result, DefinitionId);
            LoadoutRuleSnapshot.Append(result, Count); LoadoutRuleSnapshot.Append(result, Origin.x);
            LoadoutRuleSnapshot.Append(result, Origin.y); LoadoutRuleSnapshot.Append(result, OriginalSize.x);
            LoadoutRuleSnapshot.Append(result, OriginalSize.y); LoadoutRuleSnapshot.Append(result, Rotated);
            LoadoutRuleSnapshot.Append(result, ContainerType); LoadoutRuleSnapshot.Append(result, ItemType.Name);
            LoadoutRuleSnapshot.Append(result, EquipmentType.Name); LoadoutRuleSnapshot.Append(result, RuntimeValid);
            LoadoutRuleSnapshot.Append(result, Reserved); LoadoutRuleSnapshot.Append(result, HasStorage);
            LoadoutRuleSnapshot.Append(result, StorageId); LoadoutRuleSnapshot.Append(result, StorageRevision);
            LoadoutRuleSnapshot.Append(result, StorageValid); LoadoutRuleSnapshot.Append(result, StorageSignature);
            return result.ToString();
        }
    }
}
