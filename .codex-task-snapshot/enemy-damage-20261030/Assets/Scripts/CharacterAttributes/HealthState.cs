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

        public event Action Changed;
        public event Action Died;
        public event Action Revived;

        /// <summary>满血初始化；无效的初始上限使用默认值。</summary>
        public HealthState(float maxHealth = DefaultMaxHealth)
        {
            MaxHealth = IsPositiveFinite(maxHealth) ? maxHealth : DefaultMaxHealth;
            CurrentHealth = MaxHealth;
        }

        public bool ApplyDamage(DamageInfo damage)
        {
            if (!IsAlive || !IsPositiveFinite(damage.Amount)) return false;

            float nextHealth = damage.Amount >= CurrentHealth ? 0f : CurrentHealth - damage.Amount;
            if (nextHealth == CurrentHealth) return false;

            CurrentHealth = nextHealth;
            if (!IsAlive) Died?.Invoke();
            Changed?.Invoke();
            return true;
        }

        /// <summary>只治疗存活角色；满血或无效输入不通知。</summary>
        public bool Heal(float amount)
        {
            if (!IsAlive || !IsPositiveFinite(amount)) return false;

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
            if (IsAlive || !IsPositiveFinite(health)) return false;

            CurrentHealth = Math.Min(health, MaxHealth);
            Revived?.Invoke();
            Changed?.Invoke();
            return true;
        }

        /// <summary>保留当前绝对生命，仅按新上限裁剪；不上补生命，也不复活。</summary>
        public bool SetMaxHealth(float maxHealth)
        {
            if (!IsPositiveFinite(maxHealth) || maxHealth == MaxHealth) return false;

            MaxHealth = maxHealth;
            CurrentHealth = Math.Min(CurrentHealth, MaxHealth);
            Changed?.Invoke();
            return true;
        }

        internal static bool IsPositiveFinite(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
