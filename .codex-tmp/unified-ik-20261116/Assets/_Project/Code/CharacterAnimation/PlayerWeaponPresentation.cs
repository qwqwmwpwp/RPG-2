using UnityEngine;
using System.Collections.Generic;

namespace RPG4.CharacterAnimation
{
    internal sealed class PlayerWeaponPresentation
    {
        private readonly Animator animator;
        private readonly CharacterAnimationProfile profile;
        private readonly int layer;
        private CharacterActionIdentity projectedIdentity;
        private bool hasIdentity, shooting, automatic, fading, reloading, crouched;
        private int projectedState;
        private readonly HashSet<CharacterActionIdentity> endedShots = new HashSet<CharacterActionIdentity>();
        private readonly Queue<CharacterActionIdentity> endedOrder = new Queue<CharacterActionIdentity>();
        internal bool CanPresentShot(CharacterActionIdentity identity) => !endedShots.Contains(identity);
        internal PlayerWeaponPresentation(Animator animator, CharacterAnimationProfile profile, int layer)
        { this.animator = animator; this.profile = profile; this.layer = layer; }
        internal void Apply(in CharacterAnimationState input, AnimationActionConfiguration definition)
        {
            if (definition.playback == AnimationPlayback.Reload) ApplyReload(input, definition);
            else ApplyShot(input, definition);
        }
        private void ApplyReload(in CharacterAnimationState input, AnimationActionConfiguration definition)
        {
            var action = input.Reload;
            float actionPhase = action.PhaseAt(input.Time);
            var clip = definition.Motion(input.Crouched) as AnimationClip;
            if (clip == null) return;
            int hash = definition.StateHash(input.Crouched);
            float speed = clip.length / Mathf.Max(0.01f, action.Duration);
            var state = Current();
            bool continuing = reloading && projectedIdentity.Equals(action.Identity);
            if (continuing && projectedState == hash && definition.progress == AnimationProgress.FollowAction && state.fullPathHash == hash)
                speed *= Mathf.Clamp(1f + (actionPhase - Mathf.Clamp01(state.normalizedTime)) * 2f, 0.8f, 1.2f);
            SetSpeed(definition, speed);
            if (!continuing || projectedState != hash || crouched != input.Crouched)
            {
                float phase = definition.progress == AnimationProgress.Restart ? 0f
                    : continuing && definition.progress == AnimationProgress.PreservePhase ? Mathf.Clamp01(state.normalizedTime) : actionPhase;
                if (continuing) animator.CrossFade(hash, profile.Locomotion.postureTransition / Mathf.Max(0.01f, state.length), layer, phase);
                else animator.CrossFade(hash, definition.fadeIn / Mathf.Max(0.01f, state.length), layer, phase);
            }
            projectedIdentity = action.Identity; hasIdentity = true; projectedState = hash;
            reloading = true; shooting = fading = false; crouched = input.Crouched;
        }
        private void ApplyShot(in CharacterAnimationState input, AnimationActionConfiguration definition)
        {
            var action = input.Shot;
            float actionPhase = action.PhaseAt(input.Time);
            var clip = definition.Motion(input.Crouched) as AnimationClip;
            if (clip == null) return;
            int hash = definition.StateHash(input.Crouched);
            bool newShot = action.Active && (!hasIdentity || !projectedIdentity.Equals(action.Identity) || !shooting);
            float duration = Mathf.Max(0.01f, action.Duration);
            float fadeIn = !action.Automatic && definition.fadeIn >= duration ? 0f : definition.fadeIn;
            SetSpeed(definition, clip.length / duration);
            if (newShot)
            {
                if (action.Automatic)
                {
                    if (!shooting || !automatic || fading) animator.CrossFadeInFixedTime(hash, fadeIn, layer, actionPhase * duration);
                    else if (projectedState != hash) SwitchLoop(hash, fadeIn, duration, definition.progress);
                }
                else if (!shooting || automatic || fading || projectedState != hash)
                    animator.CrossFadeInFixedTime(hash, fadeIn, layer, actionPhase * duration);
                else animator.Play(hash, layer, actionPhase);
                projectedIdentity = action.Identity; hasIdentity = true; shooting = true;
                automatic = action.Automatic; fading = reloading = false; projectedState = hash;
            }
            else if (action.FadingOut && shooting && !fading)
            {
                var binding = profile.Channel(AnimationChannel.Upper);
                animator.CrossFadeInFixedTime(binding.emptyState, action.FadeDuration, layer, 0f);
                fading = true;
            }
            else if (action.Active && shooting && projectedState != hash)
            {
                if (automatic) SwitchLoop(hash, fadeIn, duration, definition.progress);
                else animator.CrossFadeInFixedTime(hash, fadeIn, layer,
                    definition.progress == AnimationProgress.Restart ? 0f : actionPhase * duration);
                projectedState = hash;
            }
        }
        private void SwitchLoop(int hash, float fade, float duration, AnimationProgress progress)
        {
            var source = Current();
            float phase = progress == AnimationProgress.Restart ? 0f : source.normalizedTime;
            animator.CrossFadeInFixedTime(hash, fade, layer, phase * duration);
            projectedState = hash;
        }
        private AnimatorStateInfo Current() => animator.IsInTransition(layer)
            ? animator.GetNextAnimatorStateInfo(layer) : animator.GetCurrentAnimatorStateInfo(layer);
        private void SetSpeed(AnimationActionConfiguration definition, float speed)
        { if (!string.IsNullOrWhiteSpace(definition.speedParameter)) animator.SetFloat(definition.speedParameter, speed); }
        internal void Reset(bool rememberEnded = false)
        {
            if (rememberEnded && hasIdentity && shooting && endedShots.Add(projectedIdentity))
            {
                endedOrder.Enqueue(projectedIdentity);
                if (endedOrder.Count > 128) endedShots.Remove(endedOrder.Dequeue());
            }
            hasIdentity = shooting = automatic = fading = reloading = false; projectedState = 0;
        }
    }
}
