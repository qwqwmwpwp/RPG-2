using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Timed semantic action projection; completion remains owned by the caller.</summary>
    internal sealed class PlayerConsumablePresentation
    {
        private readonly CharacterAnimationBackend animator;
        private readonly int layer;
        private CharacterActionIdentity identity;
        private int projectedState;
        private bool active;
        internal PlayerConsumablePresentation(CharacterAnimationBackend animator, int layer) { this.animator = animator; this.layer = layer; }
        internal void Apply(CharacterActionState action, bool crouched, AnimationActionConfiguration definition, double time)
        {
            var clip = definition.Motion(crouched) as AnimationClip;
            if (clip == null) return;
            int hash = definition.StateHash(crouched);
            float actionPhase = action.PhaseAt(time);
            var current = animator.IsInTransition(layer) ? animator.GetNextAnimatorStateInfo(layer) : animator.GetCurrentAnimatorStateInfo(layer);
            bool continuing = active && identity.Equals(action.Identity);
            float speed = clip.length / Mathf.Max(0.01f, action.Duration);
            if (continuing && projectedState == hash && current.fullPathHash == hash && definition.progress == AnimationProgress.FollowAction)
                speed *= Mathf.Clamp(1f + (actionPhase - Mathf.Clamp01(current.normalizedTime)) * 2f, 0.8f, 1.2f);
            if (!string.IsNullOrWhiteSpace(definition.speedParameter)) animator.SetFloat(definition.speedParameter, speed);
            if (!continuing || projectedState != hash)
            {
                float phase = definition.progress == AnimationProgress.Restart ? 0f
                    : continuing && definition.progress == AnimationProgress.PreservePhase ? Mathf.Clamp01(current.normalizedTime) : actionPhase;
                animator.CrossFade(hash, definition.fadeIn / Mathf.Max(0.01f, current.length), layer, phase);
            }
            active = true; identity = action.Identity; projectedState = hash;
        }
        internal void Reset() { active = false; projectedState = 0; }
    }
}
