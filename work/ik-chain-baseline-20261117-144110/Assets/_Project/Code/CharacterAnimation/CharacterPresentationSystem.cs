using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Model lifecycle and frame transaction owner. Prepare writes next animation; Present reads only committed actions; Commit publishes once.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Animator))]
    public sealed class CharacterPresentationSystem : MonoBehaviour
    {
        [SerializeField] private CharacterAnimationProfile profile;
        [SerializeField] private CharacterIKProfile ikProfile;
        [SerializeField] private Animator animator;
        [SerializeField] private PlayerAimPose aimPose;
        [SerializeField] private PlayerWeaponRig weaponRig;
        private CharacterAnimationSystem animationSystem;
        private CharacterIKSystem ikSystem;
        private CharacterPresentationGraph graph;
        private readonly Dictionary<Behaviour, bool> ownedBehaviours = new Dictionary<Behaviour, bool>();
        private bool capturedLifecycle, initialized, initializationFailed, nextIKReset;
        private int activeFrame = -1, presentedFrame = -1, committedFrame = -1;
        private CharacterAnimationPlan committedPlan, currentPlan, nextPlan;
        private CharacterAnimationState pendingInput;
        private ulong lastEventSequence;
        private readonly Queue<CharacterAnimationEvent> pendingEvents = new Queue<CharacterAnimationEvent>();
        public CharacterAnimationState CurrentState => currentPlan != null ? currentPlan.State : default;
        public CharacterAnimationState CommittedState => committedPlan != null ? committedPlan.State : default;
        public ulong EquipmentIdentity => CurrentState.EquipmentIdentity;
        public uint EquipmentVersion => CurrentState.EquipmentVersion;
        private readonly struct Observation
        {
            internal readonly CharacterIKFrame IK;
            internal readonly CharacterPresentationContext Context;
            internal readonly bool WeaponEnabled;
            internal Observation(in CharacterIKFrame ik, in CharacterPresentationContext context, bool weaponEnabled)
            { IK = ik; Context = context; WeaponEnabled = weaponEnabled; }
        }
        private Observation committedObservation, currentObservation, nextObservation;
        // External readers see one previous-commit observation for the whole render frame.
        public CharacterIKFrame IKFrame => currentObservation.IK;
        public CharacterPresentationContext Context => currentObservation.Context;
        public bool WeaponIKEnabled => isActiveAndEnabled && currentObservation.WeaponEnabled;

        private void Awake()
        {
            CaptureLifecycle(); Initialize();
            if (!isActiveAndEnabled)
                foreach (var entry in ownedBehaviours) if (entry.Key != null) entry.Key.enabled = false;
        }
        private bool Initialize()
        {
            if (initialized) return true;
            if (initializationFailed) return false;
            ResolveBindings();
            string error = ValidateBindings();
            if (error != null) { initializationFailed = true; Debug.LogError(error, this); return false; }
            animationSystem = new CharacterAnimationSystem(animator, profile);
            ikSystem = new CharacterIKSystem(ikProfile, aimPose);
            graph = new CharacterPresentationGraph(animator, aimPose);
            initialized = true; return true;
        }
        private void ResolveBindings()
        {
            if (animator == null) animator = GetComponent<Animator>();
            if (aimPose == null) aimPose = GetComponent<PlayerAimPose>();
            if (weaponRig == null) weaponRig = GetComponent<PlayerWeaponRig>();
        }
        public string ValidateBindings()
        {
            ResolveBindings();
            if (profile == null || ikProfile == null) return "Character presentation requires explicit animation and IK profiles.";
            string error = ValidateProfiles(profile, ikProfile);
            if (error != null) return error;
            if (animator == null || aimPose == null || animator.transform != transform || aimPose.transform != transform
                || weaponRig != null && weaponRig.transform != transform)
                return "Presentation backends must belong to their model root.";
            if (animator.runtimeAnimatorController != profile.Controller) return "Animator controller differs from its animation profile.";
            if (animator.applyRootMotion) return "Character presentation cannot own gameplay root motion.";
            // Resource validation also runs on persistent prefab Animators without a native controller instance.
            // Controller layer mappings are checked by authoring; runtime layer indices are checked when
            // CharacterAnimationSystem is constructed, before any presentation can execute.
            return aimPose.ValidateBindings(ikProfile.Modules);
        }
        public static string ValidateProfiles(CharacterAnimationProfile animation, CharacterIKProfile ik)
        {
            if (animation == null || ik == null) return "Presentation requires explicit animation and IK profiles.";
            string error = animation.ValidateConfiguration() ?? ik.ValidateConfiguration();
            if (error != null) return error;
            foreach (var action in animation.Actions)
                if (action != null && ik.Action(action.id) == null) return action.id + ": missing IK action mapping.";
            foreach (var mapping in ik.actions)
            {
                bool found = false;
                foreach (var action in animation.Actions) if (action != null && action.id == mapping.id) { found = true; break; }
                if (!found) return mapping.id + ": IK mapping has no animation action in this presentation.";
            }
            return null;
        }
        public void SubmitEvent(in CharacterAnimationEvent entry)
        {
            if (entry.Sequence == 0 || entry.Sequence <= lastEventSequence || double.IsNaN(entry.Time) || double.IsInfinity(entry.Time)) return;
            lastEventSequence = entry.Sequence; pendingEvents.Enqueue(entry);
        }
        /// <summary>One monotonic render-frame transaction. Same-frame input is pending only; it cannot change either executor's frozen read plan.</summary>
        public void Prepare(in CharacterAnimationState input, float deltaTime, int frameId)
        {
            if (!isActiveAndEnabled || !Initialize() || !animator.isActiveAndEnabled || double.IsNaN(input.Time) || double.IsInfinity(input.Time)) return;
            if (frameId < activeFrame) return;
            pendingInput = input;
            if (frameId == activeFrame || activeFrame > committedFrame) return;
            activeFrame = frameId; currentPlan = committedPlan; nextIKReset = false;
            currentObservation = committedObservation; nextObservation = default;
            while (pendingEvents.Count > 0)
            {
                var entry = pendingEvents.Dequeue(); animationSystem.SubmitEvent(entry);
                if (entry.Kind == CharacterAnimationEventKind.Reset || entry.Kind == CharacterAnimationEventKind.Revived
                    || entry.Kind == CharacterAnimationEventKind.MotionDiscontinuity) nextIKReset = true;
            }
            if (currentPlan != null && (!currentPlan.State.Alive && pendingInput.Alive || currentPlan.State.Active && !pendingInput.Active))
                nextIKReset = true;
            nextPlan = animationSystem.Prepare(pendingInput, deltaTime, frameId);
        }
        /// <summary>Consume the latest pending input without submitting a replacement. A newer explicit submission supersedes older pending input.</summary>
        public void Prepare(float deltaTime, int frameId) => Prepare(pendingInput, deltaTime, frameId);
        /// <summary>Both executors consume one committed plan and equipment version. The initial frame has a neutral empty pose.</summary>
        public void Present(int frameId, bool reevaluate = false)
        {
            if (frameId != activeFrame || !initialized || !isActiveAndEnabled || !animator.isActiveAndEnabled) return;
            if (presentedFrame != frameId)
            {
                presentedFrame = frameId;
                if (currentPlan != null)
                {
                    var state = IKState(currentPlan.State);
                    var executionContext = ikSystem.ResolveContext(state, currentPlan.Actions, currentPlan.BodyYaw);
                    animationSystem.Present(currentPlan, executionContext, frameId);
                    // Constraint values are local execution data. Their continuity is published only by Commit.
                    bool graphReady = graph.Ensure();
                    CharacterIKFrame executionIK = default;
                    if (graphReady)
                    {
                        executionIK = ikSystem.Prepare(state, currentPlan.Actions, executionContext, currentPlan.DeltaTime, frameId);
                        ikSystem.Present(frameId);
                    }
                    else
                    {
                        // Do not begin an unpresentable IK transaction: a failed graph attachment is retryable next frame.
                        ikSystem.Reset();
                    }
                    bool weaponEnabled = graphReady && (ikProfile.Modules & CharacterIKModules.SupportHand) != 0 && executionIK.SupportHandValid
                        && aimPose.isActiveAndEnabled && weaponRig != null && weaponRig.isActiveAndEnabled;
                    nextObservation = new Observation(executionIK, executionContext, weaponEnabled);
                }
                else { animationSystem.PresentNeutral(); aimPose.ResetPose(); nextObservation = default; }
            }
            if (reevaluate) graph.Reevaluate();
        }
        /// <summary>Publish once after all readers have presented. This never changes CurrentState within the current render frame.</summary>
        public void Commit(int frameId)
        {
            if (frameId != activeFrame || presentedFrame != frameId || committedFrame == frameId) return;
            ikSystem.Commit(frameId);
            if (nextIKReset) ikSystem.ResetContinuity();
            committedPlan = nextPlan; committedObservation = nextObservation; animationSystem.Commit(frameId); committedFrame = frameId;
        }
        public void Present(in CharacterAnimationState input, float deltaTime, int frameId, bool reevaluate = false)
        { Prepare(input, deltaTime, frameId); Present(frameId, reevaluate); Commit(frameId); }
        private static CharacterIKState IKState(in CharacterAnimationState state) => new CharacterIKState
        {
            Active = state.Active, Alive = state.Alive, CanLook = state.CanLook, Strafe = state.Strafe,
            ImmediateView = state.ImmediateView, HasEquipment = state.HasEquipment, HasGripPose = state.HasGripPose,
            ViewYaw = state.ViewYaw, ViewPitch = state.ViewPitch, RenderYawOffset = state.RenderYawOffset,
            EquipmentIdentity = state.EquipmentIdentity, EquipmentVersion = state.EquipmentVersion,
            GripCarrierLocalPosition = state.GripCarrierLocalPosition, GripCarrierLocalRotation = state.GripCarrierLocalRotation
        };
        private void CaptureLifecycle()
        {
            if (capturedLifecycle) return;
            capturedLifecycle = true; ResolveBindings();
            var owned = new List<Behaviour>();
            if (animator != null) owned.Add(animator);
            aimPose?.CollectOwnedBehaviours(owned);
            foreach (var behaviour in owned)
                if (behaviour != null && !ownedBehaviours.ContainsKey(behaviour)) ownedBehaviours.Add(behaviour, behaviour.enabled);
        }
        private void OnEnable()
        {
            CaptureLifecycle();
            foreach (var entry in ownedBehaviours) if (entry.Key != null) entry.Key.enabled = entry.Value;
        }
        private void OnDisable()
        {
            CaptureLifecycle();
            graph?.Dispose();
            if (initialized) { animationSystem.Reset(); ikSystem.Reset(); }
            committedPlan = currentPlan = nextPlan = null; pendingEvents.Clear(); pendingInput = default;
            activeFrame = presentedFrame = committedFrame = -1; nextIKReset = false;
            committedObservation = currentObservation = nextObservation = default;
            foreach (var entry in ownedBehaviours) if (entry.Key != null) entry.Key.enabled = false;
        }
        private void OnDestroy() => graph?.Dispose();
        private void OnAnimatorMove() { }
    }
}
