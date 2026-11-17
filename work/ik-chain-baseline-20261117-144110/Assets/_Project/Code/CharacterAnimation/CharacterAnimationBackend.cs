using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Records a next-frame command list against one frozen observation of the previous Animator evaluation.</summary>
    internal sealed class CharacterAnimationBackend
    {
        internal enum Kind { Float, LayerWeight, Play, CrossFade, FixedCrossFade }
        internal readonly struct Command
        {
            private readonly Kind kind;
            private readonly int id, layer;
            private readonly float value, time;
            internal Command(Kind kind, int id, int layer, float value, float time = 0f)
            { this.kind = kind; this.id = id; this.layer = layer; this.value = value; this.time = time; }
            internal void Execute(Animator animator)
            {
                switch (kind)
                {
                    case Kind.Float: animator.SetFloat(id, value); break;
                    case Kind.LayerWeight: animator.SetLayerWeight(layer, value); break;
                    case Kind.Play: animator.Play(id, layer, value); break;
                    case Kind.CrossFade: animator.CrossFade(id, value, layer, time); break;
                    case Kind.FixedCrossFade: animator.CrossFadeInFixedTime(id, value, layer, time); break;
                }
            }
        }
        private readonly AnimatorStateInfo[] current, next;
        private readonly bool[] transitioning;
        private readonly Dictionary<int, int> unobservedTargets = new Dictionary<int, int>();
        private readonly List<Command> commands = new List<Command>(24);
        internal CharacterAnimationBackend(int layerCount)
        { current = new AnimatorStateInfo[layerCount]; next = new AnimatorStateInfo[layerCount]; transitioning = new bool[layerCount]; }
        internal void Begin(Animator animator)
        {
            commands.Clear();
            for (int i = 0; i < current.Length; i++)
            {
                current[i] = animator.GetCurrentAnimatorStateInfo(i);
                next[i] = animator.GetNextAnimatorStateInfo(i);
                transitioning[i] = animator.IsInTransition(i);
                if (unobservedTargets.TryGetValue(i, out int target)
                    && (current[i].fullPathHash == target || transitioning[i] && next[i].fullPathHash == target))
                    unobservedTargets.Remove(i);
            }
        }
        internal Command[] Finish() => commands.ToArray();
        internal void ClearCommands() => commands.Clear();
        internal bool IsInTransition(int layer) => transitioning[layer];
        internal AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) => current[layer];
        internal AnimatorStateInfo GetNextAnimatorStateInfo(int layer) => next[layer];
        internal void SetFloat(int id, float value) => commands.Add(new Command(Kind.Float, id, 0, value));
        internal void SetFloat(string name, float value) => SetFloat(Animator.StringToHash(name), value);
        internal void SetLayerWeight(int layer, float weight) => commands.Add(new Command(Kind.LayerWeight, 0, layer, weight));
        internal void Play(int id, int layer, float phase) => commands.Add(new Command(Kind.Play, id, layer, phase));
        internal void CrossFade(int id, float duration, int layer, float phase, bool waitForObservation = false)
        { if (!waitForObservation || NewTransition(id, layer)) commands.Add(new Command(Kind.CrossFade, id, layer, duration, phase)); }
        internal void CrossFadeInFixedTime(int id, float duration, int layer, float time, bool waitForObservation = false)
        { if (!waitForObservation || NewTransition(id, layer)) commands.Add(new Command(Kind.FixedCrossFade, id, layer, duration, time)); }
        private bool NewTransition(int id, int layer)
        {
            // A committed command can be one frame ahead of its observed Animator state.
            // Do not restart that transition while waiting for its first evaluated observation.
            if (unobservedTargets.TryGetValue(layer, out int pending) && pending == id) return false;
            unobservedTargets[layer] = id; return true;
        }
        internal void ResetObservations() => unobservedTargets.Clear();
    }
}
