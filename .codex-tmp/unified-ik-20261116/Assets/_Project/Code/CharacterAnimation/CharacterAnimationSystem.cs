using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace RPG4.CharacterAnimation
{
    /// <summary>Only character-animation entry point. Owns selection, layers, transitions and procedural rig execution.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Animator))]
    public sealed class CharacterAnimationSystem : MonoBehaviour
    {
        [SerializeField] private CharacterAnimationProfile profile;
        [SerializeField] private Animator animator;
        [SerializeField] private PlayerAimPose aimPose;
        [SerializeField] private PlayerWeaponRig weaponRig;
        private readonly CharacterAnimationArbitrator arbitrator = new CharacterAnimationArbitrator();
        private readonly AnimationActionConfiguration[] previous = new AnimationActionConfiguration[4];
        private readonly AnimationActionConfiguration[] fadingActions = new AnimationActionConfiguration[4];
        private readonly float[] weights = new float[4];
        private readonly int[] layers = { -1, -1, -1, -1 };
        private readonly Dictionary<Behaviour, bool> ownedBehaviours = new Dictionary<Behaviour, bool>();
        private PlayerLocomotionPresentation locomotion;
        private PlayerWeaponPresentation weapon;
        private PlayerConsumablePresentation consumable;
        private PlayerLifePresentation life;
        private bool initialized, initializationFailed, capturedLifecycle, wasAlive = true, wasActive, reloadCrouched;
        private AnimationActionConfiguration shotPoseDefinition;
        private int advancedFrame = -1;
        private ulong lastEventSequence;
        private double lastInputTime;
        private bool lastFollowView;
        public bool WeaponIKEnabled { get; private set; }

        private void Awake()
        {
            CaptureLifecycle();
            Initialize();
            // A network host can disable this endpoint before its own Awake has run.
            if (!isActiveAndEnabled)
                foreach (var entry in ownedBehaviours) if (entry.Key != null) entry.Key.enabled = false;
        }
        private bool Initialize()
        {
            if (initialized) return true;
            if (initializationFailed) return false;
            if (animator == null) animator = GetComponent<Animator>();
            if (aimPose == null) aimPose = GetComponent<PlayerAimPose>();
            if (weaponRig == null) weaponRig = GetComponent<PlayerWeaponRig>();
            string error = ValidateBindings();
            if (error != null) { initializationFailed = true; Debug.LogError(error, this); return false; }
            for (int i = 0; i < 3; i++) layers[i] = animator.GetLayerIndex(profile.Channel((AnimationChannel)i).layerName);
            if (layers[0] != 0 || layers[1] < 0 || layers[2] < 0)
            { initializationFailed = true; Debug.LogError("Character animation channel bindings are missing or base layer is not first.", this); return false; }
            locomotion = new PlayerLocomotionPresentation(animator, profile, layers[0]);
            weapon = new PlayerWeaponPresentation(animator, profile, layers[1]);
            consumable = new PlayerConsumablePresentation(animator, layers[1]);
            life = new PlayerLifePresentation(animator, layers[0], layers[2]);
            aimPose.Initialize(transform, profile.AimPose);
            initialized = true;
            return true;
        }

        /// <summary>Validation exposes diagnostics, never backend/resource handles.</summary>
        public string ValidateBindings()
        {
            if (profile == null) return "Character animation system has no profile.";
            string error = profile.ValidateConfiguration();
            if (error != null) return error;
            if (animator == null || aimPose == null || weaponRig == null) return "Character animation system requires its private Animator, aim pose and weapon rig.";
            if (animator.transform != transform || aimPose.transform != transform || weaponRig.transform != transform)
                return "Animation backend bindings must belong to the same model root.";
            if (animator.runtimeAnimatorController != profile.Controller) return "Animator controller differs from its animation profile.";
            if (animator.applyRootMotion) return "Character animation cannot own gameplay root motion.";
            return null;
        }

        public void SubmitEvent(in CharacterAnimationEvent entry)
        {
            if (entry.Sequence == 0 || entry.Sequence <= lastEventSequence || double.IsNaN(entry.Time) || double.IsInfinity(entry.Time)) return;
            lastEventSequence = entry.Sequence;
            if (!Initialize()) return;
            if (entry.Kind == CharacterAnimationEventKind.WeaponCancelled && entry.Time < lastInputTime - 0.5d) return;
            switch (entry.Kind)
            {
                case CharacterAnimationEventKind.Reset:
                case CharacterAnimationEventKind.Revived: ResetExecution(); break;
                case CharacterAnimationEventKind.RebaseYaw: locomotion.RebaseYaw(entry.Value); break;
                case CharacterAnimationEventKind.MotionDiscontinuity: locomotion.Reset(); break;
                // Action validity comes from the state snapshot; an old cue must never cancel a newer action.
                case CharacterAnimationEventKind.WeaponCancelled: weaponRig.RestoreSocket(); break;
            }
        }

        /// <summary>Same-frame submissions may change view/targets; elapsed time advances at most once.</summary>
        public void Present(in CharacterAnimationState input, float deltaTime, int frameId, bool reevaluate = false)
        {
            if (!isActiveAndEnabled || !Initialize() || !animator.isActiveAndEnabled || double.IsNaN(input.Time) || double.IsInfinity(input.Time)) return;
            lastInputTime = input.Time;
            animator.fireEvents = !input.Preview;
            float step = advancedFrame == frameId ? 0f : Mathf.Max(0f, deltaTime);
            if (deltaTime > 0f) advancedFrame = frameId;
            if (!wasAlive && input.Alive || wasActive && !input.Active) ResetExecution();
            if (input.Alive) locomotion.Prepare(input, step, frameId);
            AnimationFacts facts = Facts(input);
            arbitrator.Resolve(profile, facts);
            ApplyWeights(input, step);
            var baseAction = arbitrator[AnimationChannel.Base];
            var upper = arbitrator[AnimationChannel.Upper];
            var reaction = arbitrator[AnimationChannel.Reaction];
            var pose = arbitrator[AnimationChannel.Pose];
            var effectiveUpper = upper ?? (weights[1] > 0f ? fadingActions[1] : null);
            var effectivePose = pose ?? (weights[3] > 0f ? fadingActions[3] : null);
            if (upper != null && upper.playback == AnimationPlayback.Shot && (input.Shot.Active || shotPoseDefinition == null)) shotPoseDefinition = upper;
            if (effectiveUpper == null || effectiveUpper.playback != AnimationPlayback.Shot) shotPoseDefinition = null;
            var poseSource = effectiveUpper != null && effectiveUpper.playback == AnimationPlayback.Shot ? shotPoseDefinition ?? effectiveUpper : effectivePose;
            float poseWeight = poseSource == effectiveUpper ? weights[1] : weights[3];
            if (poseSource != null && effectivePose != null) poseWeight = Mathf.Max(poseWeight, weights[3]);
            if (poseSource == null) poseWeight = 0f;
            float yawOffset = poseSource != null && poseSource.allowAim ? profile.YawOffset + poseSource.yawOffset : 0f;
            if (baseAction != null)
            {
                if (baseAction.playback == AnimationPlayback.Life) life.ApplyDead(input.Crouched, baseAction);
                else locomotion.Apply(baseAction, yawOffset * poseWeight);
            }
            if (previous[1] != upper)
            {
                if (previous[1] != null && previous[1].playback == AnimationPlayback.Shot && (upper == null || upper.playback != AnimationPlayback.Shot)) weapon.Reset(true);
                else if (upper == null || upper.playback != AnimationPlayback.Shot && upper.playback != AnimationPlayback.Reload) weapon.Reset();
                if (upper == null || upper.playback != AnimationPlayback.Consumable) consumable.Reset();
            }
            bool reloading = upper != null && upper.playback == AnimationPlayback.Reload;
            bool restoreReload = reloading && (previous[1] != upper || reloadCrouched != input.Crouched || !wasActive);
            if (upper != null)
            {
                if (upper.playback == AnimationPlayback.Consumable) consumable.Apply(input.Consumable, input.Crouched, upper, input.Time);
                else weapon.Apply(input, upper);
            }
            if (previous[1] != null && previous[1].playback == AnimationPlayback.Reload && !reloading) weaponRig.RestoreSocket();
            if (reaction != null) life.ApplyHurt(input, reaction);
            else life.ClearHurt();
            bool body = input.Active && input.Alive && !arbitrator.FullBodyExclusive;
            for (int i = 0; i < 4; i++)
            {
                var action = arbitrator[(AnimationChannel)i];
                if (action != null && !action.allowBodyTurn) body = false;
            }
            bool posing = body && poseSource != null && poseSource.allowAim && input.CanLook;
            float support = body && poseSource != null && poseSource.allowSupportHand ? poseWeight : 0f;
            float referenceWeight = effectiveUpper != null && (effectiveUpper.playback == AnimationPlayback.Shot || effectiveUpper.playback == AnimationPlayback.Reload) ? weights[1] : 0f;
            bool follow = posing && (input.Strafe || upper == null && pose == null && lastFollowView);
            var target = new AnimationPoseTarget(posing, follow, reloading,
                input.ViewYaw, input.ViewPitch, yawOffset,
                posing ? profile.WaistPitchOffset + poseSource.waistPitchOffset : 0f,
                support, referenceWeight, input.ImmediateView, body, poseWeight);
            weaponRig.SetGripPose(input);
            aimPose.ApplyPresentation(target, step, frameId, locomotion.BodyYaw + input.RenderYawOffset);
            // Submit this frame's reload/holding state before replaying historical socket events.
            if (restoreReload) weaponRig.RestoreReloadPose(upper.Motion(input.Crouched) as AnimationClip, input.Reload.PhaseAt(input.Time), upper);
            WeaponIKEnabled = support > 0f;
            lastFollowView = follow;
            if (reevaluate) aimPose.Reevaluate();
            for (int i = 0; i < previous.Length; i++) previous[i] = arbitrator[(AnimationChannel)i];
            wasActive = input.Active; wasAlive = input.Alive; reloadCrouched = input.Crouched;
        }

        private AnimationFacts Facts(in CharacterAnimationState state)
        {
            AnimationFacts facts = state.Alive ? AnimationFacts.Alive : AnimationFacts.Dead;
            if (state.Active) facts |= AnimationFacts.Active;
            if (state.CanMove) facts |= AnimationFacts.CanMove;
            if (state.CanLook) facts |= AnimationFacts.CanLook;
            if (state.Crouched) facts |= AnimationFacts.Crouched;
            if (state.Strafe) facts |= AnimationFacts.Strafe;
            if (state.Aiming) facts |= AnimationFacts.Aiming;
            if (state.Preview) facts |= AnimationFacts.Preview;
            if (state.HasEquipment) facts |= AnimationFacts.HasEquipment;
            bool allowed = state.Active && state.Alive && state.CanUseWeapon && !state.Consumable.Active;
            if (allowed)
            {
                facts |= AnimationFacts.CanUseWeapon;
                bool shot = state.Shot.ValidAt(state.Time) && weapon.CanPresentShot(state.Shot.Identity);
                if (shot && state.Shot.Active) facts |= AnimationFacts.Shooting;
                if (shot && state.Shot.FadingOut) facts |= AnimationFacts.ShotFade;
                if (state.Shot.Automatic) facts |= AnimationFacts.Automatic;
                if (state.Reload.Active && state.Reload.ValidAt(state.Time)) facts |= AnimationFacts.Reloading;
            }
            if (state.Active && state.Alive)
            {
                if (state.Consumable.Active && state.Consumable.ValidAt(state.Time)) facts |= AnimationFacts.Consumable;
                if (state.Hurt.Active && state.Hurt.ValidAt(state.Time)) facts |= AnimationFacts.Hurt;
                if (locomotion.TurnDirection < 0) facts |= AnimationFacts.TurnLeft;
                if (locomotion.TurnDirection > 0) facts |= AnimationFacts.TurnRight;
            }
            return facts;
        }

        private void ApplyWeights(in CharacterAnimationState state, float deltaTime)
        {
            for (int i = 0; i < weights.Length; i++)
            {
                var action = arbitrator[(AnimationChannel)i];
                float target = action != null ? action.weight : 0f;
                float duration = target >= weights[i] ? action != null ? action.fadeIn : 0f
                    : fadingActions[i] != null ? fadingActions[i].fadeOut : 0f;
                bool force = i == 0 || arbitrator.FullBodyExclusive && action == null;
                if (action == null && fadingActions[i] != null)
                    for (int j = 0; j < weights.Length; j++)
                    {
                        var winner = arbitrator[(AnimationChannel)j];
                        if (winner != null && !CharacterAnimationArbitrator.Compatible(winner, fadingActions[i])) force = true;
                    }
                if (action != null && action.playback == AnimationPlayback.Shot && !state.Shot.Automatic && duration >= state.Shot.Duration) force = true;
                weights[i] = force || duration <= 0f ? target : Mathf.MoveTowards(weights[i], target, deltaTime / duration);
                if (action != null) fadingActions[i] = action;
                else if (weights[i] <= 0f) fadingActions[i] = null;
                if (i < 3) animator.SetLayerWeight(layers[i], i == 0 ? 1f : weights[i]);
            }
        }
        private void ResetExecution()
        {
            arbitrator.Reset(); locomotion.Reset(); weapon.Reset(); consumable.Reset(); life.Reset();
            shotPoseDefinition = null; WeaponIKEnabled = false;
            for (int i = 0; i < previous.Length; i++) { previous[i] = fadingActions[i] = null; weights[i] = 0f; if (i > 0 && i < 3) animator.SetLayerWeight(layers[i], 0f); }
            lastFollowView = false;
            aimPose.ResetPose(); weaponRig.RestoreSocket();
        }
        private void CaptureLifecycle()
        {
            if (capturedLifecycle) return;
            capturedLifecycle = true;
            foreach (Behaviour behaviour in GetComponentsInChildren<Behaviour>(true))
                if (behaviour != this && (behaviour is Animator || behaviour.GetType().Assembly == typeof(CharacterAnimationSystem).Assembly
                    || behaviour.GetType().Namespace == "UnityEngine.Animations.Rigging"))
                    ownedBehaviours[behaviour] = behaviour.enabled;
        }
        private void OnEnable()
        {
            CaptureLifecycle();
            foreach (var entry in ownedBehaviours) if (entry.Key != null) entry.Key.enabled = entry.Value;
            advancedFrame = -1;
        }
        private void OnDisable()
        {
            CaptureLifecycle();
            if (initialized) ResetExecution();
            foreach (var entry in ownedBehaviours) if (entry.Key != null) entry.Key.enabled = false;
            wasActive = false;
        }
        private void OnAnimatorMove() { }
    }
}
