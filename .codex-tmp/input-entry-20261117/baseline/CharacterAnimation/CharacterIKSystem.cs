using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Independent constraint planner/executor. It never owns animation arbitration or creates a pose graph.</summary>
    public sealed class CharacterIKSystem
    {
        private readonly CharacterIKProfile profile;
        private readonly PlayerAimPose pose;
        private readonly CharacterIKInput input;
        private int preparedFrame = -1, presentedFrame = -1, committedFrame = -1;
        public CharacterIKFrame Frame { get; private set; }
        public CharacterIKSystem(CharacterIKProfile profile, PlayerAimPose pose)
        {
            this.profile = profile; this.pose = pose;
            string error = ValidateBindings();
            if (error != null) throw new ArgumentException(error);
            input = new CharacterIKInput(profile); pose.Initialize(pose.transform, profile.Modules);
        }
        public string ValidateBindings()
        {
            if (profile == null || pose == null) return "IK requires its own profile and pose executor.";
            string error = profile.ValidateConfiguration();
            return error ?? pose.ValidateBindings(profile.Modules);
        }
        public CharacterPresentationContext ResolveContext(in CharacterIKState state, IReadOnlyList<CharacterActionContribution> actions, float bodyYaw)
            => input.ResolveContext(state, actions, bodyYaw);
        /// <summary>Read committed action contributions and continuity; stage the next internal continuity without publishing it.</summary>
        public CharacterIKFrame Prepare(in CharacterIKState state, IReadOnlyList<CharacterActionContribution> actions,
            in CharacterPresentationContext context, float deltaTime, int renderFrame)
        {
            if (renderFrame <= preparedFrame || preparedFrame > committedFrame) return Frame;
            if (actions == null) throw new ArgumentNullException(nameof(actions));
            preparedFrame = renderFrame;
            Frame = input.Build(state, actions, context.BodyYaw, Mathf.Max(0f, deltaTime), renderFrame);
            return Frame;
        }
        public void Present(int renderFrame)
        {
            if (renderFrame != preparedFrame || presentedFrame == renderFrame) return;
            presentedFrame = renderFrame; pose.ApplyPresentation(Frame);
        }
        public void Commit(int renderFrame)
        {
            if (renderFrame != preparedFrame || presentedFrame != renderFrame || committedFrame == renderFrame) return;
            input.Commit(); pose.CommitPose(); committedFrame = renderFrame;
        }
        /// <summary>Invalidate next-frame continuity without mutating the current frame's constraint parameters.</summary>
        public void ResetContinuity() => input.Reset();
        public void Reset()
        {
            input.Reset(); pose.ResetPose(); Frame = default;
            preparedFrame = presentedFrame = committedFrame = -1;
        }
    }
}
