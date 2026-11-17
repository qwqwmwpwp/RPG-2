using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>A privately owned immutable action/command snapshot. Planning never changes an already published plan.</summary>
    public sealed class CharacterAnimationPlan
    {
        public CharacterAnimationState State { get; }
        public float BodyYaw { get; }
        public int FrameId { get; }
        public float DeltaTime { get; }
        public IReadOnlyList<CharacterActionContribution> Actions { get; }
        internal readonly Vector2 Velocity;
        internal readonly CharacterAnimationBackend.Command[] Commands;
        internal CharacterAnimationPlan(in CharacterAnimationState state, float bodyYaw, Vector2 velocity,
            float deltaTime, int frameId, CharacterActionContribution[] actions, CharacterAnimationBackend.Command[] commands)
        {
            State = state; BodyYaw = bodyYaw; Velocity = velocity; DeltaTime = deltaTime; FrameId = frameId;
            // The caller gives this plan exclusive ownership. The public view cannot be cast back to a mutable array.
            Actions = System.Array.AsReadOnly(actions); Commands = commands;
        }
    }

    /// <summary>Shared orientation for both pose consumers; it is independent of either executor's output frame.</summary>
    public readonly struct CharacterPresentationContext
    {
        public readonly float BodyYaw, LocomotionYawOffset;
        public CharacterPresentationContext(float bodyYaw, float locomotionYawOffset = 0f)
        { BodyYaw = bodyYaw; LocomotionYawOffset = locomotionYawOffset; }
    }
}
