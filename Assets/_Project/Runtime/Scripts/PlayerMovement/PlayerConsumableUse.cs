using System;
using RPG4.Backpack.Core;
using RPG4.Backpack.Inventory;
using RPG4.Backpack.Items;
using RPG4.Backpack.Runtime;
using RPG4.CharacterAttributes;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色模拟时钟驱动的单次消耗动作；库存预占和治疗共同提交，动画不参与完成判定。</summary>
    public sealed class PlayerConsumableUse
    {
        private static readonly BackpackTag HealTag = new BackpackTag("FragmentTags.Consume.Heal");
        private readonly Player owner;
        private InventoryHostCore reservedCore, offlineCore;
        private ulong nextActionId;
        private float heal;
        private bool committing;
        private ConsumableActionSnapshot state;
        public ConsumableActionSnapshot State => state;
        public bool Active => state.Active;
        internal PlayerConsumableUse(Player owner) { this.owner = owner; }
        internal void BindOffline()
        {
            if (owner.IsNetworkSimulation) return;
            InventoryHostCore core = owner.GetComponent<BackpackInventory>()?.AuthorityCore;
            if (core == null || ReferenceEquals(core, offlineCore)) return;
            UnbindOffline();
            offlineCore = core;
            core.AuthorityConsumableOperationDispatcher = DispatchOffline;
            core.ConsumableStateProvider = GetViewState;
        }
        private void DispatchOffline(ConsumableUseOperation operation)
            => offlineCore?.PublishConsumableOperationResult(TryStart(offlineCore, operation, owner.SimulationTime));
        internal void UnbindOffline()
        {
            if (offlineCore == null) return;
            if (offlineCore.AuthorityConsumableOperationDispatcher == DispatchOffline) offlineCore.AuthorityConsumableOperationDispatcher = null;
            if (offlineCore.ConsumableStateProvider == GetViewState) offlineCore.ConsumableStateProvider = null;
            offlineCore = null;
        }
        public ConsumableUseViewState GetViewState()
        {
            ConsumableActionSnapshot view = owner.HasSimulationAuthority ? state : owner.ConfirmedConsumableState;
            float duration = (float)Math.Max(0d, view.EndTime - view.StartTime);
            return new ConsumableUseViewState(view.ActionId, new ItemId(view.ItemId), view.Active, duration,
                Mathf.Clamp((float)(owner.SimulationTime - view.StartTime), 0f, duration));
        }
        public ConsumableUseResult TryStart(InventoryHostCore core, ConsumableUseOperation operation, double now)
        {
            ConsumableUseResult Fail(string error) => new ConsumableUseResult(operation.RequestId, false, operation.ItemId, state.ActionId, error);
            if (committing || Active) return Fail("已有消耗品正在使用。");
            if (!owner.HasSimulationAuthority || !owner.isActiveAndEnabled || !owner.IsAlive || owner.IsHurt || core?.Container == null)
                return Fail("角色当前不能使用消耗品。");
            if (owner.Attributes.CurrentHealth >= owner.Attributes.MaxHealth) return Fail("生命值已满。");
            if (nextActionId == ulong.MaxValue) return Fail("动作标识已耗尽。");
            ulong id = ++nextActionId;
            if (!core.TryReserveConsumable(operation.ItemId, id, out RuntimeItem item, out string error)) return Fail(error);
            ConsumableUseDefinitionFragment definition = item.GetFragment<ConsumableUseDefinitionFragment>();
            float amount = 0f;
            var modifiers = item.GetFragment<ConsumableFragment>()?.Modifiers;
            if (modifiers != null)
                foreach (ConsumeModifier modifier in modifiers)
                    if (modifier != null && modifier.FragmentTag.MatchesTagExact(HealTag)) amount += modifier.Value;
            float duration = definition?.UseDuration ?? 0f;
            if (definition == null || definition.ActionKey != "Heal" || !Finite(duration) || duration <= 0f || !Finite(amount) || amount <= 0f)
            { core.ReleaseConsumableReservation(operation.ItemId, id); return Fail("此物品没有有效的治疗使用定义。"); }
            reservedCore = core; heal = amount;
            state = new ConsumableActionSnapshot(id, item.ItemId.Value, now, now + duration, true);
            owner.RefreshConsumablePublication();
            return new ConsumableUseResult(operation.RequestId, true, operation.ItemId, id, null);
        }
        public void Cancel()
        {
            if (committing || !Active) return;
            reservedCore?.ReleaseConsumableReservation(new ItemId(state.ItemId), state.ActionId);
            state = new ConsumableActionSnapshot(state.ActionId, state.ItemId, state.StartTime, state.EndTime, false);
            reservedCore = null; heal = 0f;
            owner.RefreshConsumablePublication();
        }
        public void CompleteDueUse(double now)
        {
            if (!Active || committing || now < state.EndTime) return;
            if (!owner.HasSimulationAuthority || !owner.isActiveAndEnabled || !owner.IsAlive
                || !owner.Attributes.TryPrepareHeal(heal, out HealthState.HealTransaction transaction))
            { Cancel(); return; }
            committing = true;
            bool completed;
            try { completed = reservedCore != null && reservedCore.TryConsumeReserved(new ItemId(state.ItemId), state.ActionId, new HealEffect(transaction), out _); }
            finally { committing = false; }
            if (!completed) { transaction.Rollback(); Cancel(); return; }
            state = new ConsumableActionSnapshot(state.ActionId, state.ItemId, state.StartTime, state.EndTime, false);
            reservedCore = null; heal = 0f;
            owner.RefreshConsumablePublication();
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private sealed class HealEffect : IReservedConsumeEffect
        {
            private readonly HealthState.HealTransaction transaction;
            internal HealEffect(HealthState.HealTransaction transaction) { this.transaction = transaction; }
            public bool TryCommit(out string error) => transaction.TryCommit(out error);
            public void Rollback() => transaction.Rollback();
            public void Publish() => transaction.Publish();
        }
    }
}
