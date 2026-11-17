using System;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Stateless animation-to-constraint boundary. The numerical solver never sees this policy.</summary>
    public sealed class CharacterIKIntentResolver
    {
        private readonly CharacterIKIntentProfile profile;
        public CharacterIKIntentResolver(CharacterIKIntentProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            string error = profile.ValidateConfiguration();
            if (error != null) throw new ArgumentException(error, nameof(profile));
            this.profile = profile;
        }
        public CharacterIKIntent Resolve(CharacterAnimationOutput output)
        {
            if (output == null) return default;
            float support = 0f, reference = 0f, waistOffset = 0f, offsetWeight = 0f, socketTotal = 0f;
            Vector3 sockets = Vector3.zero;
            foreach (var action in output.Actions)
            {
                if (action.Id == null || action.Weight <= 0f) continue;
                // Retained fades from a former equipment identity cannot supply a new item's grip.
                if (action.WeaponRelated && action.Identity.Subject != 0 && action.Identity.Subject != output.EquipmentIdentity) continue;
                var policy = profile.Action(action.Id);
                if (policy == null) continue;
                var stage = policy.Stage(action.Phase);
                if (policy.allowSupportHand)
                    support = Mathf.Max(support, action.Weight * (stage != null ? stage.supportHandMultiplier : 1f));
                if (policy.shotReference) reference = Mathf.Max(reference, action.Weight);
                // Orientation participation is already resolved by animation. Only its aim actions carry waist offsets.
                if (action.AimParticipant)
                {
                    waistOffset += (profile.waistPitchOffset + policy.waistPitchOffset) * action.Weight;
                    offsetWeight += action.Weight;
                }
                if (stage != null)
                {
                    Vector3 socket = stage.socket == CharacterIKSocket.Hand ? Vector3.right
                        : stage.socket == CharacterIKSocket.Stowed ? Vector3.up : Vector3.forward;
                    sockets += socket * action.Weight; socketTotal += action.Weight;
                }
            }
            if (socketTotal < 1f) sockets += Vector3.right * (1f - socketTotal);
            else sockets /= socketTotal;
            bool active = output.Active && output.Alive;
            bool weapon = active && output.HasEquipment;
            support = weapon && output.HasGripPose ? support * output.BodyWeight : 0f;
            reference = weapon ? reference * output.BodyWeight : 0f;
            if (offsetWeight > 0f) waistOffset /= offsetWeight;
            return new CharacterIKIntent(output.RenderFrame, output.SourceFrame, output.ContinuityVersion, output.EquipmentIdentity, output.EquipmentVersion,
                output.ImmediateView, output.BodyWeight > 0f, output.AimWeight > 0f, reference > 0f, weapon, support > 0f,
                output.ViewYaw, output.BodyYaw, output.RenderYawOffset, output.ViewPitch, output.BodyWeight, output.FollowWeight,
                output.AimWeight, reference, support, output.YawOffset, waistOffset, sockets,
                output.GripCarrierLocalPosition, output.GripCarrierLocalRotation);
        }
    }
}
