using System;
using System.Collections.Generic;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;

namespace RPG4.Backpack.Inventory
{
    public sealed partial class InventoryContainer
    {
        private static readonly object ConsumedItemOwner = new object();
        private readonly Dictionary<ItemId, ulong> consumableReservations = new Dictionary<ItemId, ulong>();

        public bool TryReceiveExistingItem(RuntimeItem item, out string error)
        {
            if (item == null || !item.ItemId.IsValid)
            { error = "世界物品实例或标识无效。"; return false; }
            return TryReceiveItem(item, out error);
        }

        public bool IsReserved(ItemId itemId) => consumableReservations.ContainsKey(itemId);

        public bool TryReserveConsumable(ItemId itemId, ulong actionId, out RuntimeItem item, out string error)
        {
            item = null;
            if (activeTransaction != null || actionId == 0 || !itemId.IsValid || IsReserved(itemId))
            { error = "动作标识无效或物品已被占用。"; return false; }
            if (!TryGetItemById(itemId, out RuntimeItem candidate) || !candidate.IsConsumable
                || candidate.GetFragment<ConsumableUseDefinitionFragment>() == null || candidate.StackCount < 1)
            { error = "库存内没有可使用的消耗品实例。"; return false; }
            consumableReservations.Add(itemId, actionId);
            item = candidate;
            error = null;
            return true;
        }

        public bool ReleaseConsumableReservation(ItemId itemId, ulong actionId)
        {
            if (activeTransaction != null || !consumableReservations.TryGetValue(itemId, out ulong reserved)
                || reserved != actionId) return false;
            return consumableReservations.Remove(itemId);
        }

        private bool CanMutate(RuntimeItem item, out string error)
        {
            InventoryContainer owner = item?.RootOwner as InventoryContainer;
            if ((owner != null && owner.activeTransaction != null && !ReferenceEquals(owner.activeTransaction, activeTransaction))
                || (owner != null && owner.IsReserved(item.ItemId)) || (item != null && IsReserved(item.ItemId)))
            { error = "物品正被使用动作占用，暂不可修改。"; return false; }
            error = null;
            return true;
        }

        public bool TryConsumeReserved(ItemId itemId, ulong actionId, IReservedConsumeEffect effect, out string error)
            => TryConsumeReserved(itemId, actionId, effect, CommitParticipant, out error);

        /// <summary>库存、预留与外部效果共用准备/提交边界；失败保留预留供动作拥有者取消。</summary>
        internal bool TryConsumeReserved(ItemId itemId, ulong actionId, IReservedConsumeEffect effect,
            IInventoryCommitParticipant confirm, out string error)
            => InventoryTransactions.Execute(this, null, (InventoryTransactions tx, out string failure) =>
            {
                if (actionId == 0 || effect == null
                    || !consumableReservations.TryGetValue(itemId, out ulong reserved) || reserved != actionId
                    || !TryGetItemById(itemId, out RuntimeItem item) || !item.IsConsumable)
                { failure = "使用动作不再持有有效的消耗品占用。"; return false; }
                tx.Track(item);
                bool removed = item.StackCount == 1;
                if (removed) { ClearCells(placements[item]); RemovePlacement(item); }
                else SetItemStack(item, item.StackCount - 1);
                tx.OnRollback(effect.Rollback);
                if (!effect.TryCommit(out failure) || !tx.Prepare(confirm, removed ? ItemId.None : itemId, out failure)) return false;
                return tx.Commit(() =>
                {
                    InventoryTransactions.RunPostCommit(effect.Publish, "消耗效果已提交，通知失败");
                    NotifyChanged(); if (removed) NotifyItemRemoved(item); else NotifyStackChanged(item);
                }, out failure, () =>
                {
                    consumableReservations.Remove(itemId);
                    if (removed) item.RootOwner = ConsumedItemOwner;
                });
            }, out error);
    }
}
