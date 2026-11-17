using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace RPG4.CharacterAnimation
{
    /// <summary>Private socket/hand execution. All decisions and phases arrive in the target frame.</summary>
    [DisallowMultipleComponent]
    public sealed class PlayerWeaponRig : MonoBehaviour
    {
        [SerializeField] private MultiParentConstraint weaponSocket;
        [SerializeField] private TwoBoneIKConstraint supportHand;
        [SerializeField] private PlayerSupportHandTargetConstraint gripTarget;
        internal MultiParentConstraint WeaponConstraint => weaponSocket;
        internal TwoBoneIKConstraint SupportConstraint => supportHand;
        internal bool HasGrip => supportHand != null && supportHand.weight > 0f;
        public string ValidateBindings() => ValidateBindings(CharacterIKModules.All);
        public string ValidateBindings(CharacterIKModules modules)
        {
            bool weapon = (modules & CharacterIKModules.Weapon) != 0;
            bool support = (modules & CharacterIKModules.SupportHand) != 0;
            if (weapon && (weaponSocket == null || !weaponSocket.IsValid()
                || !weaponSocket.transform.IsChildOf(transform) || weaponSocket.data.sourceObjects.Count != 3))
                return "Weapon module requires valid Hand/Stowed/Reload socket sources on its model.";
            if (!support) return null;
            if (supportHand == null || !supportHand.IsValid() || gripTarget == null || !gripTarget.IsValid()
                || !supportHand.transform.IsChildOf(transform) || !gripTarget.transform.IsChildOf(transform))
                return "SupportHand requires valid target reconstruction and TwoBoneIK bindings on its model.";
            if (gripTarget.data.target != supportHand.data.target || !gripTarget.data.target.IsChildOf(transform)
                || !gripTarget.data.carrier.IsChildOf(transform)
                || weapon && gripTarget.data.carrier != weaponSocket.data.constrainedObject)
                return "SupportHand must bind the same carrier-local target; combined Weapon must constrain that carrier.";
            var supportRig = supportHand.GetComponentInParent<Rig>(true);
            if (supportRig == null || gripTarget.GetComponentInParent<Rig>(true) != supportRig)
                return "SupportHand target reconstruction and TwoBoneIK must share the same rig.";
            bool seenTarget = false;
            foreach (var constraint in supportRig.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (constraint == gripTarget) seenTarget = true;
                if (constraint == supportHand && !seenTarget) return "Target reconstruction must precede TwoBoneIK.";
            }
            return null;
        }
        internal void ApplyPresentation(in CharacterIKFrame frame, CharacterIKModules modules = CharacterIKModules.All)
        {
            bool enabledFrame = isActiveAndEnabled;
            bool validGrip = enabledFrame && (modules & CharacterIKModules.SupportHand) != 0 && frame.SupportHandValid;
            if (gripTarget != null)
            {
                gripTarget.data.localPosition = validGrip ? frame.GripCarrierLocalPosition : Vector3.zero;
                gripTarget.data.localRotation = validGrip ? frame.GripCarrierLocalRotation : Quaternion.identity;
                gripTarget.data.carrierScale = gripTarget.data.carrier != null ? gripTarget.data.carrier.lossyScale : Vector3.one;
                gripTarget.data.activeWeight = validGrip ? 1f : 0f;
            }
            if (supportHand != null) supportHand.weight = validGrip ? frame.SupportHandWeight : 0f;
            if (weaponSocket != null)
            {
                weaponSocket.weight = enabledFrame && (modules & CharacterIKModules.Weapon) != 0 ? 1f : 0f;
                Vector3 weights = enabledFrame && (modules & CharacterIKModules.Weapon) != 0 && frame.WeaponValid ? frame.SocketWeights : Vector3.right;
                var sources = weaponSocket.data.sourceObjects;
                if (sources.Count == 3)
                {
                    sources.SetWeight(0, weights.x); sources.SetWeight(1, weights.y); sources.SetWeight(2, weights.z);
                    weaponSocket.data.sourceObjects = sources;
                }
            }
        }
        internal void CollectOwnedBehaviours(ICollection<Behaviour> result)
        {
            result.Add(this);
            if (weaponSocket != null) result.Add(weaponSocket);
            if (gripTarget != null) result.Add(gripTarget);
            if (supportHand != null) result.Add(supportHand);
        }
        // Authored legacy clips still send these events. Compatibility receivers never mutate constraints.
        private void SwitchSocket(AnimationEvent animationEvent) { }
        private void SwitchSocketByString(string commands) { }
        private void OnDisable() => ApplyPresentation(default);
    }
}
