using System;
using RPG4.Backpack.Core;
using RPG4.LoadoutContracts;
using RPG4.Backpack.Items;
using RPG4.LoadoutTemplate;
using System.Collections.Generic;
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
        private RoleLoadoutController reservedCore;
        private Func<ConsumableUseOperation, bool> networkRequest;
        private readonly List<RuntimeItem> quickItems = new List<RuntimeItem>();
        private int nextRequest, pendingRequest;
        private ItemId pendingItem;
        private double requestDeadline, feedbackDeadline;
        private string feedback;
        public bool IsPending => pendingRequest != 0;
        public string Feedback => UnityEngine.Time.unscaledTimeAsDouble < feedbackDeadline ? feedback : null;
        private RoleLoadoutController Role => owner.Equipment?.Role ?? owner.GetComponentInChildren<RoleLoadoutController>(true);
        private ulong nextActionId;
        private float heal;
        private bool committing;
        private ConsumableActionSnapshot state;
        public ConsumableActionSnapshot State => state;
        public bool Active => state.Active;
        internal PlayerConsumableUse(Player owner) { this.owner = owner; }
        // 兼容 Player 的绑定生命周期；新库存由角色控制器直接持有。
        internal void BindOffline()
        {
            if (pendingRequest != 0 && UnityEngine.Time.unscaledTimeAsDouble >= requestDeadline)
            { pendingRequest = 0; ShowFeedback("等待用药确认超时，请以实际状态为准。"); }
        }
        internal void UnbindOffline() { networkRequest = null; pendingRequest = 0; }
        public void ConfigureNetworkRequest(Func<ConsumableUseOperation, bool> request)
        { networkRequest = request; pendingRequest = 0; }
        public bool RequestUse(ItemId itemId)
        {
            if (!itemId.IsValid || pendingRequest != 0 || nextRequest == int.MaxValue) return false;
            var operation = new ConsumableUseOperation(++nextRequest, itemId);
            if (!owner.IsNetworkSimulation)
            {
                ConsumableUseResult result = TryStart(Role, operation, owner.SimulationTime);
                ShowFeedback(result.Success ? null : result.Error); return result.Success;
            }
            pendingRequest = operation.RequestId; pendingItem = itemId;
            requestDeadline = UnityEngine.Time.unscaledTimeAsDouble + 6d;
            if (networkRequest != null && networkRequest(operation)) return true;
            pendingRequest = 0; ShowFeedback("角色物品使用尚未就绪。"); return false;
        }
        public void ApplyResult(ConsumableUseResult result)
        {
            if (result.RequestId != pendingRequest || result.ItemId != pendingItem) return;
            pendingRequest = 0; ShowFeedback(result.Success ? null : result.Error);
        }
        private void ShowFeedback(string value)
        { feedback = value; feedbackDeadline = UnityEngine.Time.unscaledTimeAsDouble + 3d; }
        public RuntimeItem ResolveQuickUse(out long total)
        {
            total = 0;
            RoleLoadoutController role = Role;
            if (role == null) return null;
            role.CopyOwnedRuntimeItems(quickItems);
            string selected = PlayerPrefs.GetString("RPG4.UI.QuickConsumable", "GameItems.Consumables.Potions.Red.Small");
            RuntimeItem first = null;
            foreach (RuntimeItem item in quickItems)
            {
                if (item.ParentItem != null || item.StackCount <= 0 || item.ItemType.Name != selected
                    || item.GetFragment<ConsumableUseDefinitionFragment>() == null) continue;
                total += item.StackCount;
                if (first == null || item.ItemId < first.ItemId) first = item;
            }
            return first;
        }
        public ConsumableUseViewState GetViewState()
        {
            ConsumableActionSnapshot view = owner.HasSimulationAuthority ? state : owner.ConfirmedConsumableState;
            float duration = (float)Math.Max(0d, view.EndTime - view.StartTime);
            return new ConsumableUseViewState(view.ActionId, new ItemId(view.ItemId), view.Active, duration,
                Mathf.Clamp((float)(owner.SimulationTime - view.StartTime), 0f, duration));
        }
        public ConsumableUseResult TryStart(RoleLoadoutController core, ConsumableUseOperation operation, double now)
        {
            ConsumableUseResult Fail(string error) => new ConsumableUseResult(operation.RequestId, false, operation.ItemId, state.ActionId, error);
            if (committing || Active) return Fail("已有消耗品正在使用。");
            if (!owner.HasSimulationAuthority || !owner.isActiveAndEnabled || !owner.IsAlive || owner.IsHurt || core == null || !core.IsAuthority)
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
        private sealed class HealEffect : RPG4.LoadoutTemplate.IReservedConsumeEffect
        {
            private readonly HealthState.HealTransaction transaction;
            internal HealEffect(HealthState.HealTransaction transaction) { this.transaction = transaction; }
            public bool TryCommit(out string error) => transaction.TryCommit(out error);
            public void Rollback() => transaction.Rollback();
            public void Publish() => transaction.Publish();
        }
    }
}
