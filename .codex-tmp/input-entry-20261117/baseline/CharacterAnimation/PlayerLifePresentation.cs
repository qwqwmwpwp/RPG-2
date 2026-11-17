using UnityEngine;

namespace RPG4.CharacterAnimation
{
    internal sealed class PlayerLifePresentation
    {
        private readonly CharacterAnimationBackend animator;
        private readonly PlayerConsumablePresentation hurt;
        private readonly int baseLayer;
        private int deadState;
        internal PlayerLifePresentation(CharacterAnimationBackend animator, int baseLayer, int reactionLayer)
        { this.animator = animator; this.baseLayer = baseLayer; hurt = new PlayerConsumablePresentation(animator, reactionLayer); }
        internal void ApplyDead(bool crouched, AnimationActionConfiguration action)
        {
            int hash = action.StateHash(crouched);
            if (deadState != hash) animator.CrossFadeInFixedTime(hash, action.fadeIn, baseLayer, 0f);
            deadState = hash;
        }
        internal void ApplyHurt(in CharacterAnimationState state, AnimationActionConfiguration action) => hurt.Apply(state.Hurt, state.Crouched, action, state.Time);
        internal void ClearHurt() => hurt.Reset();
        internal void Reset() { deadState = 0; hurt.Reset(); }
    }
}
