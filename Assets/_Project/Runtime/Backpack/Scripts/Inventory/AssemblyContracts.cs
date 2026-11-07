using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    public readonly struct AssemblyQuery
    {
        public AssemblyQuery(ItemId rootItemId, ItemId parentItemId = default, string slotId = null,
            int sessionId = 0, int requestId = 0, int candidateOffset = 0, int candidateLimit = 64)
        { RootItemId = rootItemId; ParentItemId = parentItemId; SlotId = slotId; SessionId = sessionId; RequestId = requestId; CandidateOffset = candidateOffset; CandidateLimit = candidateLimit; }
        public ItemId RootItemId { get; }
        public ItemId ParentItemId { get; }
        public string SlotId { get; }
        public int SessionId { get; }
        public int RequestId { get; }
        public int CandidateOffset { get; }
        public int CandidateLimit { get; }
    }

    public readonly struct AssemblyOperation
    {
        public AssemblyOperation(int requestId, ItemId rootItemId, ItemId parentItemId, string slotId,
            ItemId candidateItemId = default, int expectedStructureRevision = -1, int sessionId = 0)
        { RequestId = requestId; RootItemId = rootItemId; ParentItemId = parentItemId; SlotId = slotId; CandidateItemId = candidateItemId; ExpectedStructureRevision = expectedStructureRevision; SessionId = sessionId; }
        public int RequestId { get; }
        public ItemId RootItemId { get; }
        public ItemId ParentItemId { get; }
        public string SlotId { get; }
        public ItemId CandidateItemId { get; }
        public int ExpectedStructureRevision { get; }
        public int SessionId { get; }
    }

    public readonly struct AssemblyOperationResult
    {
        public AssemblyOperationResult(int requestId, int sessionId, ItemId rootItemId, ItemId parentItemId, string slotId,
            bool success, string error, int structureRevision = -1, ItemId conflictItemId = default, string conflictSlotId = null)
        { RequestId = requestId; SessionId = sessionId; RootItemId = rootItemId; ParentItemId = parentItemId; SlotId = slotId; Success = success; Error = error; StructureRevision = structureRevision; ConflictItemId = conflictItemId; ConflictSlotId = conflictSlotId; }
        public int RequestId { get; }
        public int SessionId { get; }
        public ItemId RootItemId { get; }
        public ItemId ParentItemId { get; }
        public string SlotId { get; }
        public bool Success { get; }
        public string Error { get; }
        public int StructureRevision { get; }
        public ItemId ConflictItemId { get; }
        public string ConflictSlotId { get; }
    }

    public sealed class AssemblySlotSnapshot
    {
        public AssemblySlotSnapshot(ItemId parentItemId, string slotId, string displayName, bool required, int depth,
            WeaponUiItem installedItem, bool canRemove, string reason)
        { ParentItemId = parentItemId; SlotId = slotId; DisplayName = displayName; Required = required; Depth = depth; InstalledItem = installedItem; CanRemove = canRemove; Reason = reason; }
        public ItemId ParentItemId { get; }
        public string SlotId { get; }
        public string DisplayName { get; }
        public bool Required { get; }
        public int Depth { get; }
        public WeaponUiItem InstalledItem { get; }
        public bool CanRemove { get; }
        public string Reason { get; }
    }

    public sealed class AssemblyCandidateSnapshot
    {
        public AssemblyCandidateSnapshot(WeaponUiItem item, bool canApply, string reason, GridCoord dimensions,
            string combatSummary = null, ItemId conflictItemId = default, string conflictSlotId = null)
        { Item = item; CanApply = canApply; Reason = reason; Dimensions = dimensions; CombatSummary = combatSummary; ConflictItemId = conflictItemId; ConflictSlotId = conflictSlotId; }
        public WeaponUiItem Item { get; }
        public bool CanApply { get; }
        public string Reason { get; }
        public ItemId ConflictItemId { get; }
        public string ConflictSlotId { get; }
        public GridCoord Dimensions { get; }
        public string CombatSummary { get; }
    }

    public sealed class AssemblyInspectionSnapshot
    {
        public AssemblyInspectionSnapshot(WeaponUiItem rootItem, IList<AssemblySlotSnapshot> slots, IList<AssemblyCandidateSnapshot> candidates,
            string currentSummary, string previewSummary, string operationBlockReason, int structureRevision,
            int sessionId, int requestId, ItemId rootItemId, ItemId parentItemId, string slotId,
            int candidateOffset = 0, int totalCandidateCount = 0, int nextCandidateOffset = -1)
        {
            RootItem = rootItem; Slots = Array.AsReadOnly(slots == null ? Array.Empty<AssemblySlotSnapshot>() : new List<AssemblySlotSnapshot>(slots).ToArray());
            Candidates = Array.AsReadOnly(candidates == null ? Array.Empty<AssemblyCandidateSnapshot>() : new List<AssemblyCandidateSnapshot>(candidates).ToArray());
            CurrentSummary = currentSummary; PreviewSummary = previewSummary; OperationBlockReason = operationBlockReason; StructureRevision = structureRevision;
            SessionId = sessionId; RequestId = requestId; RootItemId = rootItemId; ParentItemId = parentItemId; SlotId = slotId;
            CandidateOffset = candidateOffset; TotalCandidateCount = totalCandidateCount; NextCandidateOffset = nextCandidateOffset;
        }
        public WeaponUiItem RootItem { get; }
        public IReadOnlyList<AssemblySlotSnapshot> Slots { get; }
        public IReadOnlyList<AssemblyCandidateSnapshot> Candidates { get; }
        public string CurrentSummary { get; }
        public string PreviewSummary { get; }
        public string OperationBlockReason { get; }
        public int StructureRevision { get; }
        public int SessionId { get; }
        public int RequestId { get; }
        public ItemId RootItemId { get; }
        public ItemId ParentItemId { get; }
        public string SlotId { get; }
        public int CandidateOffset { get; }
        public int TotalCandidateCount { get; }
        public int NextCandidateOffset { get; }
    }
}
