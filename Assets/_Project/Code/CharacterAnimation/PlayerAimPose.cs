using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace RPG4.CharacterAnimation
{
    /// <summary>Applies already solved constraint parameters. It owns no policy or smoothing continuity.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator), typeof(RigBuilder))]
    public sealed class PlayerAimPose : MonoBehaviour
    {
        [SerializeField] private RigBuilder rigBuilder;
        [SerializeField] private PlayerBodyAimConstraint bodyAim;
        [SerializeField] private PlayerShotPoseConstraint shotPose;
        [SerializeField] private PlayerWeaponRig weaponRig;
        private CharacterIKModules modules = CharacterIKModules.All;
        private bool initialized;
        internal RigBuilder Builder => rigBuilder;
        internal bool HasActivePose => shotPose != null && shotPose.data.activeWeight > 0f || bodyAim != null
            && (bodyAim.data.bodyWeight > 0f || bodyAim.data.followWeight > 0f || bodyAim.data.pitchWeight > 0f)
            || weaponRig != null && weaponRig.HasGrip;
        private bool Uses(CharacterIKModules module) => (modules & module) != 0;

        public string ValidateBindings() => ValidateBindings(modules);
        public string ValidateBindings(CharacterIKModules enabledModules)
        {
            if (rigBuilder == null || rigBuilder.transform != transform)
                return "IK requires its model-root RigBuilder.";
            if ((enabledModules & CharacterIKModules.BodyAim) != 0 && (bodyAim == null || !bodyAim.IsValid()
                || bodyAim.data.characterRoot != transform || !bodyAim.transform.IsChildOf(transform)))
                return "BodyAim requires its valid model-root constraint binding.";
            if ((enabledModules & CharacterIKModules.ShotPose) != 0 && (shotPose == null || !shotPose.IsValid()
                || shotPose.data.animatorRoot != transform || !shotPose.transform.IsChildOf(transform)))
                return "ShotPose requires its valid model-root constraint binding.";
            if ((enabledModules & (CharacterIKModules.Weapon | CharacterIKModules.SupportHand)) != 0)
            {
                if (weaponRig == null || weaponRig.transform != transform) return "Weapon/hand modules require a model-root executor.";
                string error = weaponRig.ValidateBindings(enabledModules);
                if (error != null) return error;
            }
            var rigs = BoundRigs(); int previousIndex = -1;
            for (int i = 0; i < rigs.Length; i++)
            {
                if ((enabledModules & (CharacterIKModules)(1 << i)) == 0) continue;
                var rig = rigs[i];
                if (rig == null) return "Every enabled IK module requires a bound rig.";
                for (int j = 0; j < i; j++)
                    if ((enabledModules & (CharacterIKModules)(1 << j)) != 0 && rigs[j] == rig)
                        return "Enabled IK stages must use distinct rigs.";
                int found = -1;
                for (int j = 0; j < rigBuilder.layers.Count; j++)
                {
                    var layer = rigBuilder.layers[j];
                    if (layer == null || layer.rig != rig) continue;
                    if (found >= 0) return "Each enabled IK rig must occur exactly once in RigBuilder.";
                    found = j;
                    if (rig.weight != 1f) return "Enabled IK rigs require weight 1; input frames own effect weights.";
                }
                if (found < 0 || found <= previousIndex)
                    return "Enabled IK stages must retain ShotPose, BodyAim, Weapon, SupportHand order.";
                previousIndex = found;
            }
            return null;
        }
        internal void Initialize(Transform ownerRoot, CharacterIKModules enabledModules)
        {
            modules = enabledModules;
            string error = ValidateBindings();
            if (error != null || transform != ownerRoot)
            { Debug.LogError(error ?? "IK owner root does not match the model.", this); return; }
            var rigs = BoundRigs();
            for (int i = 0; i < rigs.Length; i++)
                foreach (var layer in rigBuilder.layers)
                    if (layer != null && layer.rig == rigs[i]) layer.active = Uses((CharacterIKModules)(1 << i));
            initialized = true; ResetPose();
        }
        internal void ApplyPresentation(in CharacterIKFrame target)
        {
            if (!initialized || !isActiveAndEnabled) { ResetPose(); return; }
            if (bodyAim != null)
            {
                bool body = Uses(CharacterIKModules.BodyAim);
                bodyAim.data.viewYaw = target.ViewYaw;
                bodyAim.data.bodyYaw = target.BodyYaw + target.RenderYawOffset;
                bodyAim.data.bodyWeight = body && target.BodyValid ? target.BodyWeight : 0f;
                bodyAim.data.yawLimit = target.YawLimit; bodyAim.data.pitch = body && target.AimValid ? target.Pitch : 0f;
                bodyAim.data.waistFraction = target.WaistFraction;
                bodyAim.data.yawOffset = body && target.AimValid ? target.YawOffset : 0f;
                bodyAim.data.waistPitchOffset = body && target.AimValid ? target.WaistPitchOffset : 0f;
                bodyAim.data.followWeight = body && target.AimValid ? target.FollowWeight : 0f;
                bodyAim.data.pitchWeight = body && target.AimValid ? target.AimWeight : 0f;
            }
            if (shotPose != null) shotPose.data.activeWeight = Uses(CharacterIKModules.ShotPose) && target.ShotReferenceValid ? target.ShotReferenceWeight : 0f;
            weaponRig?.ApplyPresentation(target, modules);
            rigBuilder.SyncLayers();
        }
        internal void ResetPose()
        {
            if (shotPose != null) shotPose.data.activeWeight = 0f;
            if (bodyAim != null)
            {
                bodyAim.data.bodyWeight = bodyAim.data.followWeight = bodyAim.data.pitchWeight = 0f;
                bodyAim.data.pitch = bodyAim.data.yawOffset = bodyAim.data.waistPitchOffset = 0f;
            }
            weaponRig?.ApplyPresentation(default, CharacterIKModules.None);
            if (rigBuilder != null) rigBuilder.SyncLayers();
        }
        internal void CollectOwnedBehaviours(ICollection<Behaviour> result)
        {
            result.Add(this);
            if (shotPose != null) result.Add(shotPose);
            if (bodyAim != null) result.Add(bodyAim);
            if (weaponRig != null) weaponRig.CollectOwnedBehaviours(result);
            foreach (var rig in BoundRigs()) if (rig != null) result.Add(rig);
        }
        private Rig[] BoundRigs() => new[]
        {
            shotPose != null ? shotPose.GetComponentInParent<Rig>(true) : null,
            bodyAim != null ? bodyAim.GetComponentInParent<Rig>(true) : null,
            weaponRig != null && weaponRig.WeaponConstraint != null ? weaponRig.WeaponConstraint.GetComponentInParent<Rig>(true) : null,
            weaponRig != null && weaponRig.SupportConstraint != null ? weaponRig.SupportConstraint.GetComponentInParent<Rig>(true) : null
        };
        private void OnDisable() => ResetPose();
        private void OnAnimatorMove() { }
    }
}
