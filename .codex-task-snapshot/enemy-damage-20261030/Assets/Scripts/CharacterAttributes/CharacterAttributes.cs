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

        private HealthState health;
        private HealthState Health => health ?? (health = new HealthState(initialMaxHealth));

        public float MaxHealth => Health.MaxHealth;
        public float CurrentHealth => Health.CurrentHealth;
        public float NormalizedHealth => Health.NormalizedHealth;
        public bool IsAlive => Health.IsAlive;

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
        }

        public bool ApplyDamage(DamageInfo damage) => Health.ApplyDamage(damage);
        public bool Heal(float amount) => Health.Heal(amount);
        public bool Revive(float health) => Health.Revive(health);
        public bool SetMaxHealth(float maxHealth) => Health.SetMaxHealth(maxHealth);
    }
}
