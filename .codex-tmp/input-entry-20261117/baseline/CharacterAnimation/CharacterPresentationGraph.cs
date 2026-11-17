using System;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Single explicit host for a base Animator graph plus optional ordered constraints. External base-pose owners can reuse it.</summary>
    public sealed class CharacterPresentationGraph : IDisposable
    {
        private readonly Animator animator;
        private readonly PlayerIKGraphBinding binding;
        public CharacterPresentationGraph(Animator animator, PlayerAimPose pose = null)
        {
            this.animator = animator;
            if (animator == null) throw new ArgumentNullException(nameof(animator));
            if (pose != null)
            {
                if (pose.transform != animator.transform || pose.Builder == null)
                    throw new ArgumentException("Pose graph bindings must belong to the same model root.");
                binding = new PlayerIKGraphBinding(animator, pose.Builder);
            }
        }
        public bool Ensure() => animator != null && animator.isActiveAndEnabled
            && (binding != null ? binding.Ensure() : animator.playableGraph.IsValid());
        /// <summary>Reevaluate the frozen pose at zero time without replaying animation events.</summary>
        public void Reevaluate()
        {
            if (!Ensure() || !animator.gameObject.activeInHierarchy) return;
            bool fireEvents = animator.fireEvents; animator.fireEvents = false;
            try
            {
                if (binding != null) binding.Evaluate();
                else animator.playableGraph.Evaluate(0f);
            }
            finally { animator.fireEvents = fireEvents; }
        }
        public void Dispose() => binding?.Release();
    }
}
