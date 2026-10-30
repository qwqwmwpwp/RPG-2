using System;
using RPG4.CharacterAttributes;
using UnityEngine;
using AttributeComponent = RPG4.CharacterAttributes.CharacterAttributes;

namespace RPG4.Combat
{
    /// <summary>静态练习靶：沿用统一生命组件，仅处理受击表现与倒下后的恢复。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AttributeComponent))]
    public sealed class TrainingDummy : MonoBehaviour
    {
        [SerializeField] private Transform visualRoot;
        [SerializeField] private Renderer[] hitRenderers = Array.Empty<Renderer>();
        [SerializeField] private Collider[] hitColliders = Array.Empty<Collider>();
        [Tooltip("倒下后恢复满血的秒数；0 表示不自动恢复。")]
        [SerializeField, Min(0f)] private float resetDelay = 4f;
        [SerializeField, Min(0.01f)] private float flashDuration = 0.12f;

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private AttributeComponent attributes;
        private Quaternion standingRotation;
        private MaterialPropertyBlock[] originalBlocks;
        private Color[] originalColors;
        private bool[] colliderStates;
        private MaterialPropertyBlock flashBlock;
        private double resetAt;
        private double flashUntil;
        private bool flashing;

        private void Awake()
        {
            attributes = GetComponent<AttributeComponent>();
            if (visualRoot == null)
            {
                Debug.LogError("TrainingDummy 未配置可见模型根节点。", this);
                enabled = false;
                return;
            }
            standingRotation = visualRoot.localRotation;
            originalBlocks = new MaterialPropertyBlock[hitRenderers.Length];
            originalColors = new Color[hitRenderers.Length];
            flashBlock = new MaterialPropertyBlock();
            for (int i = 0; i < hitRenderers.Length; i++)
            {
                originalBlocks[i] = new MaterialPropertyBlock();
                Renderer target = hitRenderers[i];
                if (target == null) continue;
                target.GetPropertyBlock(originalBlocks[i]);
                Material material = target.sharedMaterial;
                originalColors[i] = material != null && material.HasProperty(BaseColor)
                    ? material.GetColor(BaseColor) : Color.white;
            }
            colliderStates = new bool[hitColliders.Length];
            for (int i = 0; i < hitColliders.Length; i++)
                colliderStates[i] = hitColliders[i] != null && hitColliders[i].enabled;
        }

        private void OnEnable()
        {
            if (attributes == null || visualRoot == null) return;
            attributes.Damaged += OnDamaged;
            attributes.Died += OnDied;
            attributes.Revived += OnRevived;
            SetHitColliders(attributes.IsAlive);
            if (!attributes.IsAlive) OnDied();
        }

        private void OnDisable()
        {
            if (attributes != null)
            {
                attributes.Damaged -= OnDamaged;
                attributes.Died -= OnDied;
                attributes.Revived -= OnRevived;
            }
            RestoreColors();
        }

        private void Update()
        {
            if (visualRoot == null) return;
            if (!attributes.IsAlive && resetDelay > 0f && Time.timeAsDouble >= resetAt)
                attributes.Revive(attributes.MaxHealth);

            Quaternion targetRotation = attributes.IsAlive ? standingRotation
                : standingRotation * Quaternion.Euler(80f, 0f, 0f);
            visualRoot.localRotation = Quaternion.Slerp(visualRoot.localRotation, targetRotation,
                1f - Mathf.Exp(-14f * Time.deltaTime));

            if (!flashing) return;
            float strength = Mathf.Clamp01((float)(flashUntil - Time.timeAsDouble) / flashDuration);
            if (strength <= 0f) { RestoreColors(); return; }
            for (int i = 0; i < hitRenderers.Length; i++)
            {
                Renderer target = hitRenderers[i];
                if (target == null) continue;
                target.GetPropertyBlock(flashBlock);
                flashBlock.SetColor(BaseColor, Color.Lerp(originalColors[i], Color.white, strength));
                target.SetPropertyBlock(flashBlock);
            }
        }

        private void OnDamaged(DamageInfo damage, float appliedAmount)
        {
            flashUntil = Time.timeAsDouble + flashDuration;
            flashing = true;
        }

        private void OnDied()
        {
            resetAt = Time.timeAsDouble + resetDelay;
            SetHitColliders(false);
        }

        private void OnRevived()
        {
            visualRoot.localRotation = standingRotation;
            SetHitColliders(true);
            RestoreColors();
        }

        private void SetHitColliders(bool active)
        {
            for (int i = 0; i < hitColliders.Length; i++)
                if (hitColliders[i] != null) hitColliders[i].enabled = active && colliderStates[i];
        }

        private void RestoreColors()
        {
            flashing = false;
            if (originalBlocks == null) return;
            for (int i = 0; i < hitRenderers.Length; i++)
                if (hitRenderers[i] != null) hitRenderers[i].SetPropertyBlock(originalBlocks[i]);
        }
    }
}
