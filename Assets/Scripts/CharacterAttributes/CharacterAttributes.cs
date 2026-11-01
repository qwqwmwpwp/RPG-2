using System;
using UnityEngine;

namespace RPG4.CharacterAttributes
{
    /// <summary>角色生命组件；启用和禁用不会重置生命。</summary>
    [DisallowMultipleComponent]
    public sealed class CharacterAttributes : MonoBehaviour, IDamageable
    {
        [Tooltip("初始最大生命。运行时使用 SetMaxHealth 修改上限，当前生命仅裁剪，不自动补血。")]
        [SerializeField, Min(0.01f)] private float initialMaxHealth = HealthState.DefaultMaxHealth;
        [Tooltip("受伤后的无敌秒数。靶子设为 0，确保连射与多弹丸逐次结算。")]
        [SerializeField, Min(0f)] private float invulnerabilityDuration;

        private HealthState health;
        private double invulnerableUntil;
        private bool canExecuteAuthority = true;
        public bool CanExecuteAuthority => canExecuteAuthority;
        /// <summary>联网角色由服务器持有伤害/治疗写权限；离线默认启用。</summary>
        public void ConfigureAuthority(bool canExecute) => canExecuteAuthority = canExecute;
        public bool ApplyReplicatedHealth(float maxHealth, float currentHealth)
            => !canExecuteAuthority && Health.ApplySnapshot(maxHealth, currentHealth);
        private HealthState Health => health ?? (health = new HealthState(initialMaxHealth));

        public float MaxHealth => Health.MaxHealth;
        public float CurrentHealth => Health.CurrentHealth;
        public float NormalizedHealth => Health.NormalizedHealth;
        public bool IsAlive => Health.IsAlive;
        public bool IsInvulnerable => Time.timeAsDouble < invulnerableUntil;

        public event Action<DamageInfo, float> Damaged
        {
            add => Health.Damaged += value;
            remove { if (health != null) health.Damaged -= value; }
        }

        public event Action Changed
        {
            add => Health.Changed += value;
            remove { if (health != null) health.Changed -= value; }
        }

        public event Action Died
        {
            add => Health.Died += value;
            remove { if (health != null) health.Died -= value; }
        }

        public event Action Revived
        {
            add => Health.Revived += value;
            remove { if (health != null) health.Revived -= value; }
        }

        private void Awake()
        {
            _ = Health;
        }

        private void OnValidate()
        {
            if (!HealthState.IsPositiveFinite(initialMaxHealth))
                initialMaxHealth = HealthState.DefaultMaxHealth;
            if (!HealthState.IsPositiveFinite(invulnerabilityDuration)) invulnerabilityDuration = 0f;
        }

        public bool ApplyDamage(DamageInfo damage)
        {
            if (!canExecuteAuthority || !IsAlive || IsInvulnerable || !HealthState.IsPositiveFinite(damage.Amount)) return false;
            // 先设置无敌截止时间，防止生命事件同步触发同一接收者的第二次伤害。
            double previousDeadline = invulnerableUntil;
            invulnerableUntil = Time.timeAsDouble + invulnerabilityDuration;
            if (Health.ApplyDamage(damage)) return true;
            invulnerableUntil = previousDeadline;
            return false;
        }

        public bool Heal(float amount) => canExecuteAuthority && Health.Heal(amount);
        public bool Revive(float health)
        {
            if (!canExecuteAuthority || IsAlive || !HealthState.IsPositiveFinite(health)) return false;
            invulnerableUntil = 0d;
            return Health.Revive(health);
        }
        public bool SetMaxHealth(float maxHealth) => canExecuteAuthority && Health.SetMaxHealth(maxHealth);
    }
}
