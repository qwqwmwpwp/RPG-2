using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    internal sealed class PlayerConsumablePresentation
    {
        private static readonly int UseState = Animator.StringToHash("UpperBody Shooting.UseConsumable");
        private static readonly int Empty = Animator.StringToHash("UpperBody Shooting.Empty");
        private static readonly int Speed = Animator.StringToHash("UseConsumableSpeed");
        private readonly PlayerStateData data;
        private ulong action;
        internal bool Active { get; private set; }
        internal PlayerConsumablePresentation(PlayerStateData data) { this.data = data; }
        internal void Apply(PlayerPublishedFrame frame)
        {
            Animator animator = data.Content.Animator;
            if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null || animator.layerCount <= 1)
            { Active = false; action = 0; return; }
            ConsumableActionSnapshot state = frame.Consumable;
            if (!frame.Active || !frame.Alive || !state.Active)
            { Reset(); return; }
            float duration = Mathf.Max(0.01f, (float)(state.EndTime - state.StartTime));
            float phase = Mathf.Clamp01((float)((frame.Time - state.StartTime) / duration));
            animator.SetFloat(Speed, 2f / duration);
            animator.SetLayerWeight(1, 1f);
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(1);
            if (!Active || action != state.ActionId || current.fullPathHash != UseState
                || Mathf.Abs(current.normalizedTime - phase) > 0.1f)
                animator.Play(UseState, 1, phase);
            action = state.ActionId; Active = true;
        }
        internal void Reset()
        {
            if (!Active) return;
            Animator animator = data.Content.Animator;
            if (animator != null && animator.runtimeAnimatorController != null && animator.layerCount > 1)
            { animator.Play(Empty, 1, 0f); animator.SetLayerWeight(1, 0f); }
            Active = false; action = 0;
        }
    }
}
