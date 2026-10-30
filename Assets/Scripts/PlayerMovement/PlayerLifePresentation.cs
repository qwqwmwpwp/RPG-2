using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>受伤/死亡动画执行；持续状态足以在表现重新绑定时恢复。</summary>
    internal sealed class PlayerLifePresentation
    {
        private static readonly int Hit = Animator.StringToHash("Damage Reaction.Hit");
        private static readonly int Dead = Animator.StringToHash("Base Layer.Dead");
        private readonly PlayerStateData data;
        private bool initialized, wasAlive;
        private double observedHurtUntil;
        internal PlayerLifePresentation(PlayerStateData data) { this.data = data; }
        internal void Apply(bool active, bool alive, double time, double hurtUntil)
        {
            Animator animator = data.Content.Animator;
            if (animator == null || animator.runtimeAnimatorController == null || !animator.isActiveAndEnabled)
            {
                initialized = false;
                return;
            }
            if ((!initialized || wasAlive != alive) && !alive && animator.HasState(0, Dead))
                animator.CrossFadeInFixedTime(Dead, data.Hurt.TransitionDuration, 0, 0f);
            int layer = animator.GetLayerIndex("Damage Reaction");
            if (layer >= 0)
            {
                float remaining = active && alive ? Mathf.Max(0f, (float)(hurtUntil - time)) : 0f;
                float fade = data.Hurt.TransitionDuration;
                animator.SetLayerWeight(layer, fade > 0f ? Mathf.Clamp01(remaining / fade) : remaining > 0f ? 1f : 0f);
                if (remaining > 0f && (!initialized || observedHurtUntil != hurtUntil) && animator.HasState(layer, Hit))
                {
                    float elapsed = Mathf.Max(0f, data.Hurt.Duration - remaining);
                    animator.CrossFadeInFixedTime(Hit, fade, layer, elapsed);
                }
            }
            initialized = true;
            wasAlive = alive;
            observedHurtUntil = hurtUntil;
        }
        internal void Reset()
        {
            initialized = false;
            observedHurtUntil = 0d;
            Animator animator = data.Content.Animator;
            if (animator == null) return;
            int layer = animator.GetLayerIndex("Damage Reaction");
            if (layer >= 0) animator.SetLayerWeight(layer, 0f);
        }
    }
}
