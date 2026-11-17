using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Owns the model and render transaction: animation output flows through intent and solver to graph executors.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Animator))]
    public sealed class CharacterPresentationSystem : MonoBehaviour
    {
        [SerializeField] private CharacterAnimationProfile profile;
        [SerializeField] private CharacterIKIntentProfile intentProfile;
        [SerializeField] private CharacterIKProfile ikProfile;
        [SerializeField] private Animator animator;
        [SerializeField] private PlayerAimPose aimPose;
        [SerializeField] private PlayerWeaponRig weaponRig;
        private CharacterAnimationSystem animationSystem;
        private CharacterIKIntentResolver intentResolver;
        private CharacterIKSystem ikSystem;
        private CharacterPresentationGraph graph;
        private readonly Dictionary<Behaviour, bool> ownedBehaviours = new Dictionary<Behaviour, bool>();
        private bool capturedLifecycle, initialized, initializationFailed, renderGraphReady;
        private int activeFrame = -1, presentedFrame = -1, committedFrame = -1;
        private CharacterAnimationPlan committedPlan, currentPlan, nextPlan;
        private CharacterAnimationState pendingInput;
        private ulong lastEventSequence;
        private readonly Queue<CharacterAnimationEvent> pendingEvents = new Queue<CharacterAnimationEvent>();
        public CharacterAnimationState CurrentState => currentPlan != null ? currentPlan.State : default;
        public CharacterAnimationState CommittedState => committedPlan != null ? committedPlan.State : default;
        public ulong EquipmentIdentity => CurrentState.EquipmentIdentity;
        public uint EquipmentVersion => CurrentState.EquipmentVersion;

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
            animationSystem = new CharacterAnimationSystem(animator, profile, (ikProfile.Modules & CharacterIKModules.BodyAim) != 0);
            intentResolver = new CharacterIKIntentResolver(intentProfile);
            ikSystem = new CharacterIKSystem(ikProfile);
            aimPose.Initialize(transform, ikProfile.Modules);
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
            string error = ValidateProfiles(profile, intentProfile, ikProfile);
            if (error != null) return error;
            if (animator == null || aimPose == null || animator.transform != transform || aimPose.transform != transform
                || weaponRig != null && weaponRig.transform != transform)
                return "Presentation backends must belong to their model root.";
            if (animator.runtimeAnimatorController != profile.Controller) return "Animator controller differs from its animation profile.";
            if (animator.applyRootMotion) return "Character presentation cannot own gameplay root motion.";
            // Prefab validation has no native Animator instance; runtime construction checks actual layer indices.
            return aimPose.ValidateBindings(ikProfile.Modules);
        }
        public static string ValidateProfiles(CharacterAnimationProfile animation, CharacterIKIntentProfile intent, CharacterIKProfile ik)
        {
            if (animation == null || intent == null || ik == null) return "Presentation requires explicit animation, IK intent and solver profiles.";
            string error = animation.ValidateConfiguration() ?? intent.ValidateConfiguration() ?? ik.ValidateConfiguration();
            if (error != null) return error;
            foreach (var action in animation.Actions)
                if (action != null && intent.Action(action.id) == null) return action.id + ": missing IK intent action mapping.";
            foreach (var mapping in intent.actions)
            {
                bool found = false;
                foreach (var action in animation.Actions) if (action != null && action.id == mapping.id) { found = true; break; }
                if (!found) return mapping.id + ": IK intent mapping has no animation action in this presentation.";
            }
            return null;
        }
        public void SubmitEvent(in CharacterAnimationEvent entry)
        {
            if (entry.Sequence == 0 || entry.Sequence <= lastEventSequence || double.IsNaN(entry.Time) || double.IsInfinity(entry.Time)) return;
            lastEventSequence = entry.Sequence; pendingEvents.Enqueue(entry);
        }
        /// <summary>Plans the next publication. Later submissions in the same frame only update pending input.</summary>
        public void Prepare(in CharacterAnimationState input, float deltaTime, int frameId)
        {
            if (!isActiveAndEnabled || !Initialize() || !animator.isActiveAndEnabled || double.IsNaN(input.Time) || double.IsInfinity(input.Time)) return;
            if (frameId < activeFrame) return;
            pendingInput = input;
            if (frameId == activeFrame || activeFrame > committedFrame) return;
            activeFrame = frameId; currentPlan = committedPlan;
            renderGraphReady = false;
            while (pendingEvents.Count > 0) animationSystem.SubmitEvent(pendingEvents.Dequeue());
            nextPlan = animationSystem.Prepare(pendingInput, deltaTime, frameId);
        }
        public void Prepare(float deltaTime, int frameId) => Prepare(pendingInput, deltaTime, frameId);
        /// <summary>Executes only the prior committed plan. Repeated calls can only reevaluate the frozen graph at zero time.</summary>
        public void Present(int frameId, bool reevaluate = false)
        {
            if (frameId != activeFrame || !initialized || !isActiveAndEnabled || !animator.isActiveAndEnabled) return;
            if (presentedFrame != frameId)
            {
                presentedFrame = frameId;
                if (currentPlan != null)
                {
                    // Animation determines orientation and contributions before any downstream capability/failure is observed.
                    var animationOutput = animationSystem.Present(currentPlan, frameId);
                    var intent = intentResolver.Resolve(animationOutput);
                    renderGraphReady = graph.Ensure();
                    if (renderGraphReady && aimPose.isActiveAndEnabled)
                    {
                        var solved = ikSystem.Prepare(intent, currentPlan.DeltaTime, frameId);
                        aimPose.ApplyPresentation(solved);
                    }
                    else
                    {
                        // Failure neutralizes only downstream execution. A new render frame may retry attachment.
                        ikSystem.Reset(); aimPose.ResetPose();
                    }
                }
                else
                {
                    animationSystem.PresentNeutral(); aimPose.ResetPose();
                    renderGraphReady = graph.Ensure();
                }
            }
            if (reevaluate && renderGraphReady) graph.Reevaluate();
        }
        /// <summary>Publish once without changing this frame's frozen public view.</summary>
        public void Commit(int frameId)
        {
            if (frameId != activeFrame || presentedFrame != frameId || committedFrame == frameId) return;
            if (currentPlan != null && renderGraphReady) ikSystem.Commit(frameId);
            committedPlan = nextPlan;
            animationSystem.Commit(frameId); committedFrame = frameId;
        }
        public void Present(in CharacterAnimationState input, float deltaTime, int frameId, bool reevaluate = false)
        { Prepare(input, deltaTime, frameId); Present(frameId, reevaluate); Commit(frameId); }
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
            CaptureLifecycle(); graph?.Dispose();
            if (initialized) { animationSystem.Reset(); ikSystem.Reset(); }
            committedPlan = currentPlan = nextPlan = null; pendingEvents.Clear(); pendingInput = default;
            activeFrame = presentedFrame = committedFrame = -1; renderGraphReady = false;
            foreach (var entry in ownedBehaviours) if (entry.Key != null) entry.Key.enabled = false;
        }
        private void OnDestroy() => graph?.Dispose();
        private void OnAnimatorMove() { }
    }
}
