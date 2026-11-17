using System.Collections.Generic;

namespace RPG4.CharacterAnimation
{
    /// <summary>Stable selection over eligible facts; it never invents an action or changes business state.</summary>
    internal sealed class CharacterAnimationArbitrator
    {
        private readonly List<AnimationActionConfiguration> candidates = new List<AnimationActionConfiguration>(24);
        private readonly AnimationActionConfiguration[] selected = new AnimationActionConfiguration[4];
        private readonly AnimationActionConfiguration[] previous = new AnimationActionConfiguration[4];
        internal AnimationActionConfiguration this[AnimationChannel channel] => selected[(int)channel];
        internal bool FullBodyExclusive { get; private set; }

        internal void Resolve(CharacterAnimationProfile profile, AnimationFacts facts)
        {
            candidates.Clear();
            for (int i = 0; i < selected.Length; i++) { previous[i] = selected[i]; selected[i] = null; }
            foreach (var action in profile.Actions) if (action != null && action.Matches(facts)) candidates.Add(action);
            // Insertion sort preserves authored order; retaining the current action wins an equal-priority tie.
            for (int i = 1; i < candidates.Count; i++)
            {
                var value = candidates[i]; int j = i - 1;
                while (j >= 0 && Before(value, candidates[j])) { candidates[j + 1] = candidates[j]; j--; }
                candidates[j + 1] = value;
            }
            FullBodyExclusive = false;
            foreach (var candidate in candidates)
            {
                if (selected[(int)candidate.channel] != null) continue;
                var current = previous[(int)candidate.channel];
                bool currentValid = current != null && current.Matches(facts);
                if (currentValid)
                    foreach (var other in selected)
                        if (other != null && !Compatible(current, other)) { currentValid = false; break; }
                bool compatible = true;
                foreach (var other in selected)
                {
                    if (other == null) continue;
                    if (!Compatible(candidate, other)) { compatible = false; break; }
                    // A lock only retains a still eligible and compatible action. Real posture/life changes always win.
                    if (currentValid && current != candidate && (candidate.occupiedBody & ~other.changeableBody) != 0)
                    { compatible = false; break; }
                }
                if (!compatible) continue;
                selected[(int)candidate.channel] = candidate;
                if (candidate.occupiedBody == AnimationBody.Full && candidate.participatingBody == AnimationBody.None)
                    FullBodyExclusive = true;
            }
        }
        private bool Before(AnimationActionConfiguration a, AnimationActionConfiguration b)
        {
            if (a.priority != b.priority) return a.priority > b.priority;
            return previous[(int)a.channel] == a && previous[(int)b.channel] != b;
        }
        internal static bool Compatible(AnimationActionConfiguration a, AnimationActionConfiguration b)
        {
            if ((a.compatibleGroups & b.group) == 0 || (b.compatibleGroups & a.group) == 0) return false;
            var overlap = a.occupiedBody & b.occupiedBody;
            if (overlap != AnimationBody.None && !(a.additive || b.additive)) return false;
            return (a.occupiedBody & ~b.participatingBody) == 0 && (b.occupiedBody & ~a.participatingBody) == 0;
        }
        internal void Reset()
        {
            for (int i = 0; i < selected.Length; i++) selected[i] = previous[i] = null;
            candidates.Clear(); FullBodyExclusive = false;
        }
    }
}
