using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;

namespace RPG4.CharacterAnimation
{
    /// <summary>Owns only the RigBuilder nodes injected into the Animator's existing controller graph.</summary>
    internal sealed class PlayerIKGraphBinding
    {
        private readonly Animator animator;
        private readonly RigBuilder builder;
        private readonly List<PlayableOutput> outputs = new List<PlayableOutput>();
        private readonly List<Playable> roots = new List<Playable>();
        private PlayableGraph graph;

        internal PlayerIKGraphBinding(Animator animator, RigBuilder builder)
        { this.animator = animator; this.builder = builder; }

        internal bool IsAttached
        {
            get
            {
                if (!graph.IsValid() || outputs.Count == 0) return false;
                foreach (var output in outputs) if (!output.IsOutputValid()) return false;
                foreach (var root in roots) if (!root.IsValid()) return false;
                return true;
            }
        }

        internal bool Ensure()
        {
            if (animator == null || builder == null || !animator.isActiveAndEnabled) return false;
            var current = animator.playableGraph;
            if (IsAttached && current.Equals(graph)) return true;
            Release();
            current = animator.playableGraph;
            if (!current.IsValid()) return false;
            int originalOutputCount = current.GetOutputCount();
            if (originalOutputCount == 0) return false;
            graph = current;
            var originalRoots = new HashSet<Playable>();
            for (int i = 0; i < graph.GetRootPlayableCount(); i++) originalRoots.Add(graph.GetRootPlayable(i));
            bool attached = false;
            try
            {
                if (!builder.Build(graph)) return false;
                // Build(graph) appends Sync followed by the valid rig layers. Explicit output
                // order is required for a second evaluation in the same render frame.
                for (int i = originalOutputCount; i < graph.GetOutputCount(); i++)
                {
                    var output = graph.GetOutput(i);
                    UnityEngine.Experimental.Animations.AnimationPlayableOutputExtensions.SetSortingOrder(
                        (AnimationPlayableOutput)output, (ushort)(1000 + i - originalOutputCount));
                }
                CaptureAddedNodes(originalOutputCount, originalRoots);
                return (attached = IsAttached);
            }
            finally
            {
                if (!attached)
                {
                    // A binder can throw while creating jobs, before any output is appended.
                    // Capture new roots as well, then remove nodes before releasing job memory.
                    CaptureAddedNodes(originalOutputCount, originalRoots);
                    Release();
                }
            }
        }

        private void CaptureAddedNodes(int originalOutputCount, HashSet<Playable> originalRoots)
        {
            outputs.Clear(); roots.Clear();
            if (!graph.IsValid()) return;
            for (int i = originalOutputCount; i < graph.GetOutputCount(); i++) outputs.Add(graph.GetOutput(i));
            for (int i = 0; i < graph.GetRootPlayableCount(); i++)
            {
                var root = graph.GetRootPlayable(i);
                if (!originalRoots.Contains(root)) roots.Add(root);
            }
        }

        internal void Evaluate()
        {
            if (!IsAttached) return;
            builder.SyncLayers();
            graph.Evaluate(0f);
        }

        internal void Release()
        {
            if (graph.IsValid())
            {
                // Remove every owned output before freeing any jobs. Build(graph) leaves
                // independent rig chains; none has an input connection to controller nodes.
                foreach (var output in outputs)
                    if (output.IsOutputValid()) graph.DestroyOutput(output);
                foreach (var root in roots)
                    if (root.IsValid()) graph.DestroySubgraph(root);
            }
            outputs.Clear(); roots.Clear(); graph = default;
            // Keep automatic OnEnable/Build from clearing jobs attached to the shared graph.
            // Only this host manages RigBuilder's execution lifecycle from now on.
            if (builder != null)
            {
                if (builder.enabled) builder.enabled = false;
                builder.Clear();
            }
        }
    }
}
