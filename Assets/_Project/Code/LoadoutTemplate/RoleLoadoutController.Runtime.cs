using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using RPG4.LoadoutContracts;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    public sealed partial class RoleLoadoutController
    {
        [SerializeField] private ItemManifestCatalog manifestCatalog;
        [SerializeField] private LoadoutItemDefinition[] runtimeItemDefinitions = Array.Empty<LoadoutItemDefinition>();
        [SerializeField] private LoadoutSlotId selectedWeaponSlot = LoadoutSlotId.PrimaryWeapon1;
        private readonly Dictionary<ItemId, ulong> consumableReservations = new Dictionary<ItemId, ulong>();
        public ItemManifestCatalog ManifestCatalog => manifestCatalog;
        public bool IsAuthority => !NetworkManaged || Mirror.NetworkServer.active;
        public LoadoutSlotId SelectedWeaponSlot => !IsAuthority && Network != null ? Network.ProjectedWeaponSlot : selectedWeaponSlot;
        public event Action Changed;
        public event Action RuntimeChanged;
        private NetworkRoleLoadout Network => GetComponentInParent<NetworkRoleLoadout>(true);
        internal IReadOnlyList<LoadoutItemDefinition> RuntimeDefinitions => runtimeItemDefinitions;
        internal void ApplySelectedWeapon(LoadoutSlotId slot) { selectedWeaponSlot = slot; }
        internal static bool IsWeaponSlot(LoadoutSlotId slot) => slot == LoadoutSlotId.PrimaryWeapon1
            || slot == LoadoutSlotId.PrimaryWeapon2 || slot == LoadoutSlotId.SecondaryWeapon || slot == LoadoutSlotId.MeleeWeapon;

        public bool TryGetCurrentWeapon(out RuntimeItem item, out ItemManifestCatalog catalog)
        {
            catalog = manifestCatalog; item = null;
            if (TryReadWeapon(SelectedWeaponSlot, out item)) return true;
            foreach (LoadoutSlotId slot in WeaponFallbackOrder)
                if (slot != SelectedWeaponSlot && TryReadWeapon(slot, out item)) return true;
            return false;
        }
        private static readonly LoadoutSlotId[] WeaponFallbackOrder = { LoadoutSlotId.PrimaryWeapon1,
            LoadoutSlotId.PrimaryWeapon2, LoadoutSlotId.SecondaryWeapon, LoadoutSlotId.MeleeWeapon };
        private bool TryReadWeapon(LoadoutSlotId slot, out RuntimeItem item)
        {
            item = null;
            ContainerSnapshot snapshot = ReadSlot(slot);
            if (snapshot?.Items.Count != 1) return false;
            RuntimeItem candidate = snapshot.Items[0].RuntimeItem;
            if (candidate?.WeaponState == null) return false;
            item = candidate;
            return true;
        }
        public bool GetCurrentWeapon(out RuntimeItem item, out ItemManifestCatalog catalog) => TryGetCurrentWeapon(out item, out catalog);
        public void CopyOwnedRuntimeItems(List<RuntimeItem> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            foreach (ItemDisplayData display in ReadItems())
                if (display.RuntimeItem != null) foreach (RuntimeItem item in ItemAssemblyTraversal.Enumerate(display.RuntimeItem)) destination.Add(item);
        }
        public bool TryGetOwnedRuntimeItem(ItemId id, out RuntimeItem item)
        {
            item = null; if (!id.IsValid) return false;
            foreach (ItemDisplayData display in ReadItems())
                if (ItemAssemblyTraversal.TryFind(display.RuntimeItem, id, out item)) return true;
            return false;
        }
        private ContainerSnapshot ReadSlot(LoadoutSlotId slot) => !IsAuthority && Network != null
            ? Network.GetProjectedSlot(slot) : GetSlot(slot)?.Snapshot;
        private IEnumerable<ItemDisplayData> ReadItems()
        {
            foreach (LoadoutSlotId slot in Enum.GetValues(typeof(LoadoutSlotId)))
            {
                ContainerSnapshot snapshot = ReadSlot(slot);
                if (snapshot == null) continue;
                foreach (ItemDisplayData item in snapshot.Items)
                {
                    yield return item;
                    if (item.Storage != null) foreach (ItemDisplayData stored in item.Storage.Items) yield return stored;
                }
            }
        }
        public bool TrySelectWeapon(LoadoutSlotId slot, out string error)
        {
            if (!CanStartRuntime(out error) || !IsWeaponSlot(slot)) return Fail(error ?? "不是持握武器槽。", out error);
            if (slot == selectedWeaponSlot) return true;
            if (!CommitCoordinator.TryEnter(out IDisposable transaction, out LoadoutFailure failure))
                return Fail(failure.Reason, out error);
            using (transaction)
            {
                LoadoutStateVersion before = CaptureStateVersion();
                var changes = new Dictionary<ILoadoutEndpoint, List<ItemDisplayData>>();
                if (Network != null && !Network.TryPrepareRuntimeChanges(changes, slot, out error)) return false;
                var prepared = new PreparedLoadoutOperation(changes, new Dictionary<ILoadoutEndpoint, object>(),
                    new Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot>(), selectedWeaponChanged: true, runtimeItemsChanged: false);
                if (!CommitCoordinator.TryCommit(prepared, CreateOperationKey(LoadoutOperationChannel.Runtime), before,
                    null, out LoadoutCommitRecord commit, out failure, completeModel: () => selectedWeaponSlot = slot))
                    return Fail(failure.Reason, out error);
                PublishRuntimeTransaction(commit, null);
                error = null;
                return true;
            }
        }
        private bool CanStartRuntime(out string error)
        {
            if (!IsAuthority || CommitCoordinator.IsExecuting || !isActiveAndEnabled) return Fail("角色权威当前不可修改库存。", out error);
            return EnsureInitialized(out error);
        }
        public bool TryReserveConsumable(ItemId id, ulong actionId, out RuntimeItem item, out string error)
        {
            item = null;
            if (!CanStartRuntime(out error)) return false;
            if (actionId == 0 || consumableReservations.ContainsKey(id) || !TryGetOwnedRuntimeItem(id, out item)
                || item.ParentItem != null || !item.IsConsumable || item.GetFragment<ConsumableUseDefinitionFragment>() == null)
            { item = null; return Fail("消耗品不存在、不可使用或已被动作占用。", out error); }
            consumableReservations.Add(id, actionId); LoadoutRuntimeItems.Reserve(id); return true;
        }
        public bool ReleaseConsumableReservation(ItemId id, ulong actionId)
        {
            if (!IsAuthority || CommitCoordinator.IsExecuting || !consumableReservations.TryGetValue(id, out ulong active) || active != actionId) return false;
            consumableReservations.Remove(id); LoadoutRuntimeItems.Release(id); return true;
        }
        public bool TryConsumeReserved(ItemId id, ulong actionId, IReservedConsumeEffect effect, out string error)
        {
            if (effect == null || !consumableReservations.TryGetValue(id, out ulong active) || active != actionId)
                return Fail("动作没有持有消耗品预占。", out error);
            return ExecuteRuntime(work =>
            {
                RuntimeItem item = work.Find(id);
                if (item == null || !item.IsConsumable || !work.IsRoot(id)) return "消耗品已不在角色库存。";
                if (item.StackCount == 1) work.RemoveRoot(id);
                else { item.SetStackCount(item.StackCount - 1); item.AdvanceStateRevision(false); }
                return null;
            }, out error, effect: effect, consumed: id);
        }
        public bool TryConsumeShot(ItemId weaponId, out BackpackTag ammoItemType, out AmmoDefinitionFragment ammo, out ulong sequence, out string error)
        {
            BackpackTag shotType = default; AmmoDefinitionFragment shotAmmo = null; ulong shotSequence = 0;
            bool success = ExecuteRuntime(work =>
            {
                RuntimeItem weapon = work.Find(weaponId);
                if (weapon?.WeaponState == null || LoadoutRuntimeItems.IsReserved(weapon)) return "武器不可用。";
                return weapon.WeaponState.TryConsumeShot(manifestCatalog, out shotType, out shotAmmo, out shotSequence, out string failure) ? null : failure;
            }, out error);
            ammoItemType = success ? shotType : default; ammo = success ? shotAmmo : null; sequence = success ? shotSequence : 0; return success;
        }
        public bool TryChamberRound(ItemId weaponId, out string error) => ExecuteRuntime(work =>
        {
            RuntimeItem weapon = work.Find(weaponId);
            if (weapon?.WeaponState == null || LoadoutRuntimeItems.IsReserved(weapon)) return "武器不可用。";
            return weapon.WeaponState.TryChamberRound(manifestCatalog, out string failure) ? null : failure;
        }, out error);

        public bool TryLoadMagazine(ItemId magazineId, ItemId ammoId, int count, out string error) => LoadMagazine(magazineId, ammoId, count, false, out error);
        public bool CanLoadMagazine(ItemId magazineId, ItemId ammoId, int count, out string error) => LoadMagazine(magazineId, ammoId, count, true, out error);
        private bool LoadMagazine(ItemId magazineId, ItemId ammoId, int count, bool preview, out string error) =>
            LoadMagazine(magazineId, ammoId, count, preview, out _, out error);
        private bool LoadMagazine(ItemId magazineId, ItemId ammoId, int count, bool preview,
            out LoadoutCommitRecord commit, out string error) => ExecuteRuntime(work =>
        {
            RuntimeItem magazine = work.Find(magazineId), ammo = work.Find(ammoId);
            if (magazine?.MagazineState == null || ammo == null || magazineId == ammoId || !work.IsRoot(ammoId)
                || ammo.WeaponState != null || ammo.MagazineState != null || ammo.GetFragment<AttachmentDefinitionFragment>() != null
                || count < 1 || count > ammo.StackCount || count > magazine.MagazineState.Capacity - magazine.MagazineState.RoundCount
                || !magazine.MagazineState.CanAcceptAmmo(ammo.ItemType, manifestCatalog)
                || LoadoutRuntimeItems.IsReserved(magazine) || LoadoutRuntimeItems.IsReserved(ammo)) return "弹匣或弹药不兼容、数量不足或物品被占用。";
            magazine.MagazineState.AddRounds(ammo.ItemType, count);
            if (count == ammo.StackCount) work.RemoveRoot(ammoId);
            else { ammo.SetStackCount(ammo.StackCount - count); ammo.AdvanceStateRevision(false); }
            return null;
        }, out commit, out error, preview);

        public bool TryExchangeWeaponMagazine(ItemId weaponId, ItemId magazineId, out string error) => ExchangeChild(weaponId, ItemAssemblySlots.Magazine, magazineId, false, out error);
        public bool CanExchangeWeaponMagazine(ItemId weaponId, ItemId magazineId, out string error) => ExchangeChild(weaponId, ItemAssemblySlots.Magazine, magazineId, true, out error);
        public bool TryExchangeWeaponAttachment(ItemId weaponId, WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
            => slot == WeaponAttachmentSlot.Muzzle ? ExchangeChild(weaponId, ItemAssemblySlots.Muzzle, attachmentId, false, out error) : Fail("未知武器附件槽。", out error);
        public bool CanExchangeWeaponAttachment(ItemId weaponId, WeaponAttachmentSlot slot, ItemId attachmentId, out string error)
            => slot == WeaponAttachmentSlot.Muzzle ? ExchangeChild(weaponId, ItemAssemblySlots.Muzzle, attachmentId, true, out error) : Fail("未知武器附件槽。", out error);
        public bool TryExchangeAssembly(ItemId parentId, string slotId, ItemId incomingId, out string error) => ExchangeChild(parentId, slotId, incomingId, false, out error);
        private bool ExchangeChild(ItemId parentId, string slotId, ItemId incomingId, bool preview, out string error) =>
            ExchangeChild(parentId, slotId, incomingId, preview, out _, out error);
        private bool ExchangeChild(ItemId parentId, string slotId, ItemId incomingId, bool preview,
            out LoadoutCommitRecord commit, out string error) => ExecuteRuntime(work =>
        {
            RuntimeItem parent = work.Find(parentId), incoming = work.Find(incomingId);
            if (parent == null || incomingId.IsValid && (incoming == null || !work.IsRoot(incomingId))
                || incoming != null && ReferenceEquals(parent, incoming) || LoadoutRuntimeItems.IsReserved(parent)
                || LoadoutRuntimeItems.IsReserved(incoming)) return "装配物品无效或正被使用。";
            RuntimeItem previous = parent.Assembly.GetChild(slotId);
            if (parent.Assembly.GetSlot(slotId) == null || incoming == null && previous == null
                || incoming != null && !parent.Assembly.CanAccept(slotId, incoming, manifestCatalog)) return "该装配槽不接受候选物品。";
            ILoadoutEndpoint preferred = incoming == null ? null : work.RootEndpoint(incomingId);
            Vector2Int origin = incoming == null ? Vector2Int.zero : work.RootDisplay(incomingId).Origin;
            if (incoming != null) work.RemoveRoot(incomingId);
            parent.Assembly.SetChild(slotId, null);
            if (incoming != null) parent.Assembly.SetChild(slotId, incoming);
            if (!ItemAssemblyRules.TryValidateConflicts(new List<RuntimeItem>(ItemAssemblyTraversal.Enumerate(FindRoot(parent))), out _, out _, out string failure)) return failure;
            if (previous != null && !work.TryPlace(previous, preferred, origin, out failure)) return failure;
            return null;
        }, out commit, out error, preview);
        private static RuntimeItem FindRoot(RuntimeItem item) { while (item.ParentItem != null) item = item.ParentItem; return item; }

        internal bool TryApplyWeaponOperation(WeaponOperation operation, out LoadoutCommitRecord commit, out string error)
        {
            commit = null;
            switch (operation.Kind)
            {
                case WeaponOperationKind.ExchangeMagazine:
                    return ExchangeChild(operation.TargetItemId, ItemAssemblySlots.Magazine, operation.CandidateItemId,
                        false, out commit, out error);
                case WeaponOperationKind.ExchangeAttachment:
                    return ExchangeChild(operation.TargetItemId, ItemAssemblySlots.Muzzle, operation.CandidateItemId,
                        false, out commit, out error);
                case WeaponOperationKind.LoadMagazine:
                    return LoadMagazine(operation.TargetItemId, operation.CandidateItemId, operation.Count,
                        false, out commit, out error);
                case WeaponOperationKind.ExchangeAssembly:
                    return ExchangeChild(operation.TargetItemId, operation.AssemblySlotId, operation.CandidateItemId,
                        false, out commit, out error);
                default: return Fail("未知的武器操作。", out error);
            }
        }

        private bool ExecuteRuntime(Func<RuntimeWorkspace, string> mutate, out string error, bool preview = false,
            IReservedConsumeEffect effect = null, ItemId consumed = default) =>
            ExecuteRuntime(mutate, out _, out error, preview, effect, consumed);

        private bool ExecuteRuntime(Func<RuntimeWorkspace, string> mutate, out LoadoutCommitRecord commit,
            out string error, bool preview = false, IReservedConsumeEffect effect = null, ItemId consumed = default)
        {
            commit = null;
            if (!CanStartRuntime(out error)) return false;
            if (!CommitCoordinator.TryEnter(out IDisposable transaction, out LoadoutFailure failure))
                return Fail(failure.Reason, out error);
            using var transactionScope = transaction;
            bool effectAttempted = false;
            try
            {
                LoadoutStateVersion before = CaptureStateVersion();
                LoadoutOperationKey key = CreateOperationKey(LoadoutOperationChannel.Runtime);
                PreparedLoadoutOperation prepared;
                using (LoadoutDiagnostics.Begin(LoadoutDiagnosticStage.Prepare))
                {
                    if (!RuntimeWorkspace.TryCreate(this, out RuntimeWorkspace work, out error)) return false;
                    error = mutate(work);
                    if (error != null) return false;
                    if (!work.TryPrepare(out Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> changes,
                        out Dictionary<ILoadoutEndpoint, object> states, out error)) return false;
                    prepared = new PreparedLoadoutOperation(changes, states, work.Dependencies);
                    if (Network != null && !Network.TryPrepareRuntimeChanges(changes, selectedWeaponSlot, out error)) return false;
                }
                if (!CommitCoordinator.ValidateVersion(before, out failure) || !prepared.ValidateDependencies(out failure))
                    return Fail(failure.Reason, out error);
                if (preview) return true;
                if (effect != null)
                {
                    effectAttempted = true;
                    if (!effect.TryCommit(out error)) return false;
                }
                if (!CommitCoordinator.TryCommit(prepared, key, before, null, out commit, out failure))
                    return Fail(failure.Reason, out error);
                if (consumed.IsValid)
                {
                    consumableReservations.Remove(consumed);
                    LoadoutRuntimeItems.Release(consumed);
                }
                PublishRuntimeTransaction(commit, () =>
                {
                    prepared.PublishChanges();
                    if (effect != null) SafeNotify(effect.Publish);
                });
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = commit == null ? exception.Message : null;
                return commit != null;
            }
            finally
            {
                if (commit == null && effectAttempted) SafeNotify(effect.Rollback);
            }
        }
        internal void ClearRuntimeReservations()
        {
            foreach (ItemId id in consumableReservations.Keys) LoadoutRuntimeItems.Release(id);
            consumableReservations.Clear();
        }
        internal void PublishRuntimeChanged()
        {
            if (Changed != null) foreach (Action listener in Changed.GetInvocationList()) SafeNotify(listener);
            if (RuntimeChanged != null) foreach (Action listener in RuntimeChanged.GetInvocationList()) SafeNotify(listener);
        }
        private void SafeNotify(Action notify) { try { notify(); } catch (Exception exception) { Debug.LogException(exception, this); } }
        private bool TryDefinition(RuntimeItem runtime, out LoadoutItemDefinition definition)
        {
            foreach (LoadoutItemDefinition candidate in runtimeItemDefinitions ?? Array.Empty<LoadoutItemDefinition>())
                if (candidate != null && candidate.Manifest != null && candidate.Manifest.ItemType.MatchesTagExact(runtime.ItemType)) { definition = candidate; return true; }
            if (Network != null && Network.TryGetRuntimeDefinition(runtime.ItemType, out definition)) return true;
            definition = null; return false;
        }

        private sealed class RuntimeWorkspace
        {
            private readonly RoleLoadoutController role;
            private readonly Dictionary<ILoadoutEndpoint, ContainerSnapshot> original = new Dictionary<ILoadoutEndpoint, ContainerSnapshot>();
            private readonly Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> items = new Dictionary<ILoadoutEndpoint, List<ItemDisplayData>>();
            internal Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot> Dependencies { get; }
                = new Dictionary<ILoadoutEndpoint, LoadoutRuleSnapshot>();
            private readonly HashSet<RuntimeItem> clonedRoots = new HashSet<RuntimeItem>();
            private RuntimeWorkspace(RoleLoadoutController role) { this.role = role; }
            internal static bool TryCreate(RoleLoadoutController role, out RuntimeWorkspace work, out string error)
            {
                work = new RuntimeWorkspace(role); error = null;
                foreach (RoleSlotConfiguration config in role.slots)
                {
                    if (!work.Add(role.GetSlot(config.slotId), out error)) return false;
                    if ((int)config.slotId < 4 && config.containerController.Container != null
                        && !work.Add(config.containerController, out error)) return false;
                }
                return true;
            }
            private bool Add(ILoadoutEndpoint endpoint, out string error)
            {
                error = null; ContainerSnapshot snapshot = endpoint.Snapshot;
                Dependencies.Add(endpoint, LoadoutRuleSnapshot.CaptureEndpoint(endpoint, snapshot));
                original.Add(endpoint, snapshot); var copy = new List<ItemDisplayData>();
                copy.AddRange(snapshot.Items);
                items.Add(endpoint, copy); return true;
            }
            internal RuntimeItem Find(ItemId id)
            {
                foreach (var pair in items)
                {
                    for (int i = 0; i < pair.Value.Count; i++)
                    {
                        ItemDisplayData display = pair.Value[i];
                        if (!ItemAssemblyTraversal.TryFind(display.RuntimeItem, id, out RuntimeItem result)) continue;
                        if (clonedRoots.Contains(display.RuntimeItem)) return result;
                        if (!LoadoutRuntimeItems.TryClone(display.RuntimeItem, role.manifestCatalog, out RuntimeItem clone, out string error))
                            throw new InvalidOperationException(error);
                        clonedRoots.Add(clone);
                        pair.Value[i] = display.WithRuntime(clone);
                        ItemAssemblyTraversal.TryFind(clone, id, out result);
                        return result;
                    }
                }
                return null;
            }
            internal ItemDisplayData RootDisplay(ItemId id)
            {
                foreach (List<ItemDisplayData> list in items.Values)
                {
                    foreach (ItemDisplayData item in list)
                    {
                        if (item.RuntimeItem?.ItemId == id) return item;
                    }
                }
                return null;
            }
            internal ILoadoutEndpoint RootEndpoint(ItemId id)
            {
                foreach (var pair in items)
                {
                    foreach (ItemDisplayData item in pair.Value)
                    {
                        if (item.RuntimeItem?.ItemId == id) return pair.Key;
                    }
                }
                return null;
            }
            internal bool IsRoot(ItemId id) => RootEndpoint(id) != null;
            internal void RemoveRoot(ItemId id)
            {
                ILoadoutEndpoint endpoint = RootEndpoint(id);
                if (endpoint == null) throw new InvalidOperationException("目标不是独立库存物品。");
                ItemDisplayData display = RootDisplay(id);
                items[endpoint].Remove(display);
            }
            internal bool TryPlace(RuntimeItem runtime, ILoadoutEndpoint preferred, Vector2Int origin, out string error)
            {
                error = null;
                if (!role.TryDefinition(runtime, out LoadoutItemDefinition definition)) { error = "卸下物品缺少显示定义。"; return false; }
                ItemDisplayData display = definition.Create("runtime/" + runtime.ItemId.Value, origin, runtime.StackCount, runtimeItem: runtime);
                if (preferred != null && TryPlaceAt(preferred, display, origin)) return true;
                foreach (var pair in items)
                {
                    if (original[pair.Key].IsSlot) continue;
                    foreach (Vector2Int cell in original[pair.Key].ValidCells)
                    {
                        if (TryPlaceAt(pair.Key, display, cell)) return true;
                        if (TryPlaceAt(pair.Key, display.WithRotation(!display.Rotated), cell)) return true;
                    }
                }
                error = "角色容器没有可容纳卸下物品的空位。"; return false;
            }
            private bool TryPlaceAt(ILoadoutEndpoint endpoint, ItemDisplayData display, Vector2Int origin)
            {
                if (!items.ContainsKey(endpoint) || original[endpoint].IsSlot) return false;
                var candidate = new List<ItemDisplayData>();
                foreach (ItemDisplayData current in items[endpoint])
                    candidate.Add(current.RuntimeItem == null ? current : current.WithRuntime(current.RuntimeItem));
                candidate.Add(display.At(origin));
                if (!LoadoutOperations.Validate(original[endpoint], candidate, out _)) return false;
                items[endpoint] = candidate; return true;
            }
            internal bool TryPrepare(out Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> changes,
                out Dictionary<ILoadoutEndpoint, object> prepared, out string error)
            {
                changes = new Dictionary<ILoadoutEndpoint, List<ItemDisplayData>>(); prepared = new Dictionary<ILoadoutEndpoint, object>(); error = null;
                var identities = new HashSet<ItemId>();
                foreach (var pair in items)
                {
                    var current = new List<ItemDisplayData>();
                    foreach (ItemDisplayData item in pair.Value)
                    {
                        current.Add(item.RuntimeItem == null ? item : item.WithRuntime(item.RuntimeItem));
                        if (item.RuntimeItem == null) continue;
                        foreach (RuntimeItem node in ItemAssemblyTraversal.Enumerate(item.RuntimeItem))
                        {
                            if (node.ItemId.IsValid && identities.Add(node.ItemId)) continue;
                            error = "角色物品树有重复或无效运行标识。";
                            return false;
                        }
                    }
                    bool changed = current.Count != original[pair.Key].Items.Count;
                    if (!changed)
                    {
                        for (int i = 0; i < current.Count; i++)
                        {
                            if (Same(current[i], original[pair.Key].Items[i])) continue;
                            changed = true;
                            break;
                        }
                    }
                    if (!changed) continue;
                    if (original[pair.Key].Revision == int.MaxValue) { error = "库存端点版本耗尽。"; return false; }
                    if (!pair.Key.TryPrepare(current, out object state, out error)) return false;
                    changes.Add(pair.Key, current); prepared.Add(pair.Key, state);
                }
                return true;
            }
            private static bool Same(ItemDisplayData first, ItemDisplayData second)
            {
                if (first.Id != second.Id || first.Count != second.Count || first.Origin != second.Origin || first.Size != second.Size
                    || first.Rotated != second.Rotated || first.Storage != second.Storage || first.DefinitionId != second.DefinitionId) return false;
                if (ReferenceEquals(first.RuntimeItem, second.RuntimeItem)) return true;
                if (first.RuntimeItem == null || second.RuntimeItem == null) return false;
                return RuntimeItemSnapshotCodec.TryCapture(first.RuntimeItem, out RuntimeItemSnapshot a, out _)
                    && RuntimeItemSnapshotCodec.TryCapture(second.RuntimeItem, out RuntimeItemSnapshot b, out _) && a.Equals(b);
            }
        }
    }
}
