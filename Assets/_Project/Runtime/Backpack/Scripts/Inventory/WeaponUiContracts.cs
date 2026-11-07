using System;
using System.Collections.Generic;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.Backpack.Inventory
{
    public enum WeaponOperationKind { ExchangeMagazine, ExchangeAttachment, LoadMagazine }

    /// <summary>只携带身份和意图；交换时 CandidateItemId=None 表示卸下。来源始终为玩家背包事件键。</summary>
    public readonly struct WeaponOperation
    {
        public WeaponOperation(int requestId, WeaponOperationKind kind, ItemId targetItemId,
            ItemId candidateItemId = default, int count = 0, WeaponAttachmentSlot slot = WeaponAttachmentSlot.Muzzle)
        { RequestId = requestId; Kind = kind; TargetItemId = targetItemId; CandidateItemId = candidateItemId; Count = count; Slot = slot; }
        public int RequestId { get; }
        public WeaponOperationKind Kind { get; }
        public ItemId TargetItemId { get; }
        public ItemId CandidateItemId { get; }
        public int Count { get; }
        public WeaponAttachmentSlot Slot { get; }
    }

    public readonly struct WeaponOperationResult
    {
        public WeaponOperationResult(int requestId, ItemId targetItemId, bool success, string error)
        { RequestId = requestId; TargetItemId = targetItemId; Success = success; Error = error; }
        public int RequestId { get; }
        public ItemId TargetItemId { get; }
        public bool Success { get; }
        public string Error { get; }
    }

    /// <summary>开窗/选物品时请求；None 取消检查。每次请求同时回传当前 HUD，事件不重放。</summary>
    public readonly struct WeaponSnapshotRequest
    {
        public WeaponSnapshotRequest(ItemId inspectedItemId, int sessionId = 0, int requestId = 0, bool hudOnly = false)
        { InspectedItemId = inspectedItemId; SessionId = sessionId; RequestId = requestId; HudOnly = hudOnly; }
        public ItemId InspectedItemId { get; }
        public int SessionId { get; }
        public int RequestId { get; }
        public bool HudOnly { get; }
    }

    public sealed class WeaponUiItem
    {
        public WeaponUiItem(ItemId itemId, string name, Sprite icon, int count = 1)
        { ItemId = itemId; Name = name; Icon = icon; Count = count; }
        public ItemId ItemId { get; }
        public string Name { get; }
        public Sprite Icon { get; }
        public int Count { get; }
    }

    public sealed class WeaponUiCandidate
    {
        public WeaponUiCandidate(WeaponUiItem item, WeaponOperationKind kind, bool canApply, string reason, int maxCount = 0, string combatSummary = null)
        { Item = item; Kind = kind; CanApply = canApply; Reason = reason; MaxCount = maxCount; CombatSummary = combatSummary; }
        public WeaponUiItem Item { get; }
        public WeaponOperationKind Kind { get; }
        public bool CanApply { get; }
        public string Reason { get; }
        public int MaxCount { get; }
        public string CombatSummary { get; }
    }

    /// <summary>纯展示副本；绝不包含 RuntimeItem 或可写物品状态。空 Item 表示清除。</summary>
    public sealed class WeaponInspectionSnapshot
    {
        public WeaponInspectionSnapshot(WeaponUiItem item, bool isWeapon, bool isMagazine, bool isEquipped,
            int chamberCount, int magazineRounds, int magazineCapacity, float durability, float maxDurability,
            WeaponUiItem magazine, WeaponUiItem muzzle, bool canRemoveMagazine, bool canRemoveMuzzle,
            string operationBlockReason, IList<WeaponUiCandidate> candidates, int sessionId = 0, int requestId = 0,
            ItemId inspectedItemId = default, string combatSummary = null, bool hasMuzzleSlot = false)
        {
            Item = item; IsWeapon = isWeapon; IsMagazine = isMagazine; IsEquipped = isEquipped;
            ChamberCount = chamberCount; MagazineRounds = magazineRounds; MagazineCapacity = magazineCapacity;
            Durability = durability; MaxDurability = maxDurability; Magazine = magazine; Muzzle = muzzle;
            CanRemoveMagazine = canRemoveMagazine; CanRemoveMuzzle = canRemoveMuzzle; OperationBlockReason = operationBlockReason;
            Candidates = Array.AsReadOnly(candidates == null ? Array.Empty<WeaponUiCandidate>() : new List<WeaponUiCandidate>(candidates).ToArray());
            SessionId = sessionId; RequestId = requestId; InspectedItemId = inspectedItemId;
            CombatSummary = combatSummary; HasMuzzleSlot = hasMuzzleSlot;
        }
        public WeaponUiItem Item { get; }
        public bool IsWeapon { get; }
        public bool IsMagazine { get; }
        public bool IsEquipped { get; }
        public int ChamberCount { get; }
        public int MagazineRounds { get; }
        public int MagazineCapacity { get; }
        public float Durability { get; }
        public float MaxDurability { get; }
        public WeaponUiItem Magazine { get; }
        public WeaponUiItem Muzzle { get; }
        public bool CanRemoveMagazine { get; }
        public bool CanRemoveMuzzle { get; }
        public string OperationBlockReason { get; }
        public IReadOnlyList<WeaponUiCandidate> Candidates { get; }
        public int SessionId { get; }
        public int RequestId { get; }
        public ItemId InspectedItemId { get; }
        public string CombatSummary { get; }
        public bool HasMuzzleSlot { get; }
    }

    public enum WeaponUiActionStage { None, Started, MagazineCommitted, Chambered, Completed, Cancelled }

    public sealed class WeaponHudSnapshot
    {
        public WeaponHudSnapshot(WeaponUiItem item, int chamberCount, int magazineRounds, int magazineCapacity,
            float durability, float maxDurability, WeaponUiActionStage actionStage, double actionStartTime, float actionDuration,
            int nonEmptyCompatibleMagazineCount = 0, int looseAmmoCount = 0, string fireMode = null, string statusSummary = null)
        {
            Item = item; ChamberCount = chamberCount; MagazineRounds = magazineRounds; MagazineCapacity = magazineCapacity;
            Durability = durability; MaxDurability = maxDurability; ActionStage = actionStage;
            ActionStartTime = actionStartTime; ActionDuration = actionDuration;
            NonEmptyCompatibleMagazineCount = nonEmptyCompatibleMagazineCount; LooseAmmoCount = looseAmmoCount;
            FireMode = fireMode;
            StatusSummary = statusSummary;
        }
        public WeaponUiItem Item { get; }
        public int ChamberCount { get; }
        public int MagazineRounds { get; }
        public int MagazineCapacity { get; }
        public float Durability { get; }
        public float MaxDurability { get; }
        public WeaponUiActionStage ActionStage { get; }
        public double ActionStartTime { get; }
        public float ActionDuration { get; }
        public int NonEmptyCompatibleMagazineCount { get; }
        public int LooseAmmoCount { get; }
        public string FireMode { get; }
        public string StatusSummary { get; }
    }
}
