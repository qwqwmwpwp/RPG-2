using System;

namespace RPG4.CharacterAttributes
{
    /// <summary>不依赖 Unity 生命周期和时钟的生命状态。</summary>
    public sealed class HealthState : IDamageable
    {
        public const float DefaultMaxHealth = 100f;

        public float MaxHealth { get; private set; }
        public float CurrentHealth { get; private set; }
        public float NormalizedHealth => CurrentHealth / MaxHealth;
        public bool IsAlive => CurrentHealth > 0f;
        private HealTransaction pendingHeal;

        public event Action Changed;
        public event Action Died;
        public event Action Revived;
        /// <summary>只在实际扣血后通知；第二个参数为裁剪过量伤害后的实际扣血量。</summary>
        public event Action<DamageInfo, float> Damaged;

        /// <summary>满血初始化；无效的初始上限使用默认值。</summary>
        public HealthState(float maxHealth = DefaultMaxHealth)
        {
            MaxHealth = IsPositiveFinite(maxHealth) ? maxHealth : DefaultMaxHealth;
            CurrentHealth = MaxHealth;
        }

        public bool ApplyDamage(DamageInfo damage)
        {
            if (pendingHeal != null || !IsAlive || !IsPositiveFinite(damage.Amount)) return false;

            float nextHealth = damage.Amount >= CurrentHealth ? 0f : CurrentHealth - damage.Amount;
            if (nextHealth == CurrentHealth) return false;

            float appliedAmount = CurrentHealth - nextHealth;
            CurrentHealth = nextHealth;
            if (!IsAlive) Died?.Invoke();
            Changed?.Invoke();
            Damaged?.Invoke(damage, appliedAmount);
            return true;
        }

        /// <summary>只治疗存活角色；满血或无效输入不通知。</summary>
        public bool Heal(float amount)
        {
            if (pendingHeal != null || !IsAlive || !IsPositiveFinite(amount)) return false;

            float missingHealth = MaxHealth - CurrentHealth;
            float nextHealth = amount >= missingHealth ? MaxHealth : CurrentHealth + amount;
            if (nextHealth == CurrentHealth) return false;

            CurrentHealth = nextHealth;
            Changed?.Invoke();
            return true;
        }

        /// <summary>只复活死亡角色；复活生命不超过上限。</summary>
        public bool Revive(float health)
        {
            if (pendingHeal != null || IsAlive || !IsPositiveFinite(health)) return false;

            CurrentHealth = Math.Min(health, MaxHealth);
            Revived?.Invoke();
            Changed?.Invoke();
            return true;
        }

        /// <summary>保留当前绝对生命，仅按新上限裁剪；不上补生命，也不复活。</summary>
        public bool SetMaxHealth(float maxHealth)
        {
            if (pendingHeal != null || !IsPositiveFinite(maxHealth) || maxHealth == MaxHealth) return false;

            MaxHealth = maxHealth;
            CurrentHealth = Math.Min(CurrentHealth, MaxHealth);
            Changed?.Invoke();
            return true;
        }

        /// <summary>应用权威快照；不重放伤害回调与伤害来源副作用。</summary>
        internal bool ApplySnapshot(float maxHealth, float currentHealth)
        {
            if (pendingHeal != null || !IsPositiveFinite(maxHealth) || float.IsNaN(currentHealth)
                || float.IsInfinity(currentHealth) || currentHealth < 0f || currentHealth > maxHealth) return false;
            if (MaxHealth == maxHealth && CurrentHealth == currentHealth) return false;
            bool wasAlive = IsAlive;
            MaxHealth = maxHealth;
            CurrentHealth = currentHealth;
            if (wasAlive && !IsAlive) Died?.Invoke();
            else if (!wasAlive && IsAlive) Revived?.Invoke();
            Changed?.Invoke();
            return true;
        }

        internal static bool IsPositiveFinite(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>准备一次可回滚的治疗，不改生命、不发事件；满血或死亡不能准备。</summary>
        public bool TryPrepareHeal(float amount, out HealTransaction transaction)
        {
            transaction = null;
            if (pendingHeal != null || !IsAlive || !IsPositiveFinite(amount) || CurrentHealth >= MaxHealth) return false;
            float missingHealth = MaxHealth - CurrentHealth;
            float nextHealth = amount >= missingHealth ? MaxHealth : CurrentHealth + amount;
            if (nextHealth == CurrentHealth) return false;
            transaction = new HealTransaction(this, nextHealth);
            return true;
        }

        private void PublishHealChanged()
        {
            if (Changed == null) return;
            Exception firstError = null;
            foreach (Delegate callback in Changed.GetInvocationList())
            {
                try { ((Action)callback)(); }
                catch (Exception exception) { if (firstError == null) firstError = exception; }
            }
            if (firstError != null) throw firstError;
        }

        /// <summary>库存消耗适配器使用的单次治疗事务；提交到发布/回滚之间阻止其他生命写入。</summary>
        public sealed class HealTransaction
        {
            private readonly HealthState owner;
            private readonly float before;
            private readonly float maxHealth;
            private readonly float after;
            private bool committed;
            private bool completed;

            internal HealTransaction(HealthState owner, float nextHealth)
            {
                this.owner = owner;
                before = owner.CurrentHealth;
                maxHealth = owner.MaxHealth;
                after = nextHealth;
            }

            public bool TryCommit(out string error)
            {
                if (completed || committed || owner.pendingHeal != null || owner.CurrentHealth != before || owner.MaxHealth != maxHealth)
                { error = "治疗事务已经结束或生命状态已改变。"; return false; }
                owner.pendingHeal = this;
                owner.CurrentHealth = after;
                committed = true;
                error = null;
                return true;
            }

            public void Rollback()
            {
                if (completed) return;
                completed = true;
                if (!committed || !ReferenceEquals(owner.pendingHeal, this)) return;
                owner.CurrentHealth = before;
                owner.pendingHeal = null;
            }

            public void Publish()
            {
                if (completed || !committed || !ReferenceEquals(owner.pendingHeal, this)) return;
                completed = true;
                owner.pendingHeal = null;
                // 各订阅者均有机会收到最终状态；异常不会把已提交事务重新变成可消费状态。
                owner.PublishHealChanged();
            }
        }
    }
}
