using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Converts already arbitrated animation, equipment and view values. Never reads entities, networking or input devices.</summary>
    internal sealed class CharacterIKInput
    {
        private readonly CharacterIKProfile profile;
        private bool lastFollowView;
        private uint version, resetVersion, nextVersion, nextResetVersion;
        private bool nextFollowView;
        private ulong nextEquipmentIdentity;
        private uint nextEquipmentVersion;
        private ulong equipmentIdentity;
        private uint equipmentVersion;
        internal CharacterIKInput(CharacterIKProfile profile) { this.profile = profile; }

        internal CharacterIKFrame Build(in CharacterIKState state, IReadOnlyList<CharacterActionContribution> actions,
            float bodyYaw, float deltaTime, int renderFrame)
        {
            nextEquipmentIdentity = state.EquipmentIdentity; nextEquipmentVersion = state.EquipmentVersion;
            nextResetVersion = resetVersion + (equipmentIdentity != state.EquipmentIdentity || equipmentVersion != state.EquipmentVersion ? 1u : 0u);
            bool active = state.Active && state.Alive;
            float body = active ? 1f : 0f, aim = 0f, support = 0f, reference = 0f;
            float offsetWeight = 0f, yaw = 0f, waistOffset = 0f;
            float socketTotal = 0f; Vector3 sockets = Vector3.zero;
            CharacterActionIdentity identity = default;
            float identityWeight = 0f;
            bool livePose = false;
            foreach (var action in actions)
            {
                if (action.Id == null || action.Weight <= 0f) continue;
                // A fading weapon action must not carry a former weapon's grip/reference into a replacement.
                if (action.WeaponRelated && action.Identity.Subject != 0 && action.Identity.Subject != state.EquipmentIdentity) continue;
                var policy = profile.Action(action.Id);
                // Standalone callers may supply only the mappings used by their own action source.
                if (policy == null) continue;
                if (!policy.allowBodyTurn) body *= 1f - action.Weight;
                if (policy.allowAim)
                {
                    aim = Mathf.Max(aim, action.Weight);
                    yaw += (profile.yawOffset + policy.yawOffset) * action.Weight;
                    waistOffset += (profile.waistPitchOffset + policy.waistPitchOffset) * action.Weight;
                    offsetWeight += action.Weight;
                    livePose |= !action.FadingOut;
                }
                var stage = policy.Stage(action.Phase);
                if (policy.allowSupportHand)
                    support = Mathf.Max(support, action.Weight * (stage != null ? stage.supportHandMultiplier : 1f));
                if (policy.shotReference) reference = Mathf.Max(reference, action.Weight);
                if (stage != null)
                {
                    Vector3 socket = stage.socket == CharacterIKSocket.Hand ? Vector3.right
                        : stage.socket == CharacterIKSocket.Stowed ? Vector3.up : Vector3.forward;
                    sockets += socket * action.Weight;
                    socketTotal += action.Weight;
                }
                if (action.Weight > identityWeight && !action.Identity.Equals(default(CharacterActionIdentity)))
                { identity = action.Identity; identityWeight = action.Weight; }
            }
            if (socketTotal < 1f) sockets += Vector3.right * (1f - socketTotal);
            else sockets /= socketTotal;
            bool posing = active && state.CanLook && aim > 0f;
            bool follow = posing && (state.Strafe || !livePose && lastFollowView);
            aim = posing ? aim * body : 0f;
            support = active && state.HasEquipment && state.HasGripPose ? support * body : 0f;
            reference = active && state.HasEquipment ? reference * body : 0f;
            if (offsetWeight > 0f) { yaw /= offsetWeight; waistOffset /= offsetWeight; }
            nextFollowView = follow; nextVersion = version + 1;
            return new CharacterIKFrame(nextVersion, nextResetVersion, state, identity, bodyYaw, deltaTime, renderFrame, body,
                follow ? aim : 0f, aim, reference, support, yaw, waistOffset,
                profile.waistFraction, profile.yawLimit, profile.pitchRange, profile.pitchSmoothTime, sockets, profile.Modules);
        }
        internal void Commit()
        {
            lastFollowView = nextFollowView; version = nextVersion; resetVersion = nextResetVersion;
            equipmentIdentity = nextEquipmentIdentity; equipmentVersion = nextEquipmentVersion;
        }
        internal void Reset()
        {
            lastFollowView = nextFollowView = false; resetVersion++;
            nextResetVersion = resetVersion; equipmentIdentity = nextEquipmentIdentity = 0;
            equipmentVersion = nextEquipmentVersion = 0;
        }
        internal CharacterPresentationContext ResolveContext(in CharacterIKState state, IReadOnlyList<CharacterActionContribution> actions, float bodyYaw)
        {
            if ((profile.Modules & CharacterIKModules.BodyAim) == 0) return new CharacterPresentationContext(bodyYaw);
            // Evaluate against the same committed continuity as Build; keep this shared orientation independent of IKFrame.
            float yaw = 0f, total = 0f, aim = 0f; bool live = false;
            foreach (var action in actions)
            {
                if (action.Id == null || action.Weight <= 0f || action.WeaponRelated && action.Identity.Subject != 0
                    && action.Identity.Subject != state.EquipmentIdentity) continue;
                var policy = profile.Action(action.Id);
                if (policy == null || !policy.allowAim) continue;
                aim = Mathf.Max(aim, action.Weight); yaw += (profile.yawOffset + policy.yawOffset) * action.Weight;
                total += action.Weight; live |= !action.FadingOut;
            }
            float body = state.Active && state.Alive ? 1f : 0f;
            foreach (var action in actions)
            {
                if (action.Id == null || action.Weight <= 0f || action.WeaponRelated && action.Identity.Subject != 0
                    && action.Identity.Subject != state.EquipmentIdentity) continue;
                var policy = profile.Action(action.Id);
                if (policy != null && !policy.allowBodyTurn) body *= 1f - action.Weight;
            }
            bool follow = state.Active && state.Alive && state.CanLook && aim > 0f && (state.Strafe || !live && lastFollowView);
            return new CharacterPresentationContext(bodyYaw, follow && total > 0f ? yaw / total * aim * body : 0f);
        }
    }
}
