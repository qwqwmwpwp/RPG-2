using System.Collections.Generic;
using RPG4.CharacterAttributes;
using UnityEngine;
using AttributeComponent = RPG4.CharacterAttributes.CharacterAttributes;

namespace RPG4.Combat
{
    /// <summary>触发器伤害源；同一角色的多个碰撞体共享冷却，死亡的来源不再造成伤害。</summary>
    [DisallowMultipleComponent]
    public sealed class ContactDamage : MonoBehaviour
    {
        [SerializeField] private AttributeComponent owner;
        [SerializeField, Min(0.01f)] private float damage = 10f;
        [SerializeField, Min(0.05f)] private float repeatInterval = 1f;
        [SerializeField] private LayerMask targetLayers = ~0;

        private readonly Dictionary<AttributeComponent, double> nextDamageAt = new Dictionary<AttributeComponent, double>();
        private readonly List<AttributeComponent> expired = new List<AttributeComponent>();

        private void Awake()
        {
            if (owner == null) owner = GetComponentInParent<AttributeComponent>();
        }

        private void OnTriggerEnter(Collider other) => TryDamage(other);
        private void OnTriggerStay(Collider other) => TryDamage(other);

        private void TryDamage(Collider other)
        {
            if (!isActiveAndEnabled || owner != null && !owner.IsAlive
                || (targetLayers.value & (1 << other.gameObject.layer)) == 0) return;
            AttributeComponent target = other.GetComponentInParent<AttributeComponent>();
            if (target == null || target == owner || !target.IsAlive) return;
            double now = Time.timeAsDouble;
            if (nextDamageAt.TryGetValue(target, out double next) && now < next) return;
            Vector3 point = other.ClosestPoint(transform.position);
            var request = new DamageInfo(damage, owner != null ? owner.gameObject : gameObject,
                point, (other.bounds.center - transform.position).normalized);
            // 先登记，生命事件内的同步调用也不能绕过该角色的来源冷却。
            nextDamageAt[target] = now + repeatInterval;
            if (!target.ApplyDamage(request)) nextDamageAt.Remove(target);
        }

        private void FixedUpdate()
        {
            if (nextDamageAt.Count == 0) return;
            expired.Clear();
            foreach (var entry in nextDamageAt)
                if (entry.Key == null || Time.timeAsDouble >= entry.Value) expired.Add(entry.Key);
            foreach (AttributeComponent target in expired) nextDamageAt.Remove(target);
        }

        private void OnDisable()
        {
            nextDamageAt.Clear();
            expired.Clear();
        }
    }
}
