using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.CharacterAnimation
{
    /// <summary>Animation arbitration, playback and shared orientation. No IK output is accepted or observed.</summary>
    public sealed class CharacterAnimationSystem
    {
        private readonly Animator animator;
        private readonly CharacterAnimationProfile profile;
        private readonly CharacterAnimationBackend backend;
        private readonly bool bodyOrientationEnabled;
        private readonly CharacterAnimationArbitrator arbitrator = new CharacterAnimationArbitrator();
        private readonly AnimationActionConfiguration[] previous = new AnimationActionConfiguration[4];
        private readonly AnimationActionConfiguration[] fadingActions = new AnimationActionConfiguration[4];
        private readonly float[] weights = new float[4];
        private readonly int[] layers = { -1, -1, -1, -1 };
        private readonly CharacterActionContribution[] ikActions = new CharacterActionContribution[4];
        private readonly PlayerLocomotionPresentation locomotion;
        private readonly PlayerWeaponPresentation weapon;
        private readonly PlayerConsumablePresentation consumable;
        private readonly PlayerLifePresentation life;
        private readonly Queue<CharacterAnimationEvent> events = new Queue<CharacterAnimationEvent>();
        private bool wasAlive = true, wasActive, needsNeutralPose = true;
        private int preparedFrame = -1, executedFrame = -1, committedFrame = -1;
        private ulong lastEventSequence, equipmentIdentity;
        private uint equipmentVersion;
        private double lastInputTime;
        private CharacterAnimationPlan nextPlan;
        private uint continuityVersion, committedContinuityVersion, nextContinuityVersion;
        private bool lastFollowView, nextFollowView;
        public CharacterAnimationOutput Output { get; private set; }

        public CharacterAnimationSystem(Animator animator, CharacterAnimationProfile profile, bool bodyOrientationEnabled = true)
        {
            this.animator = animator; this.profile = profile;
            this.bodyOrientationEnabled = bodyOrientationEnabled;
            string error = ValidateBindings();
            if (error != null) throw new ArgumentException(error);
            for (int i = 0; i < 3; i++) layers[i] = animator.GetLayerIndex(profile.Channel((AnimationChannel)i).layerName);
            backend = new CharacterAnimationBackend(animator.layerCount);
            locomotion = new PlayerLocomotionPresentation(backend, profile, layers[0]);
            weapon = new PlayerWeaponPresentation(backend, profile, layers[1]);
            consumable = new PlayerConsumablePresentation(backend, layers[1]);
            life = new PlayerLifePresentation(backend, layers[0], layers[2]);
        }
        public string ValidateBindings()
        {
            if (animator == null || profile == null) return "Animation requires an Animator and animation profile.";
            string error = profile.ValidateConfiguration();
            if (error != null) return error;
            if (animator.runtimeAnimatorController != profile.Controller) return "Animator controller differs from its animation profile.";
            if (animator.applyRootMotion) return "Character animation cannot own gameplay root motion.";
            if (animator.GetLayerIndex(profile.Channel(AnimationChannel.Base).layerName) != 0
                || animator.GetLayerIndex(profile.Channel(AnimationChannel.Upper).layerName) < 0
                || animator.GetLayerIndex(profile.Channel(AnimationChannel.Reaction).layerName) < 0)
                return "Animation channel bindings are missing or base layer is not first.";
            return null;
        }
        public void SubmitEvent(in CharacterAnimationEvent entry)
        {
            if (entry.Sequence == 0 || entry.Sequence <= lastEventSequence || double.IsNaN(entry.Time) || double.IsInfinity(entry.Time)) return;
            lastEventSequence = entry.Sequence; events.Enqueue(entry);
        }
        private void ConsumeEvents()
        {
            while (events.Count > 0)
            {
                var entry = events.Dequeue();
                if (entry.Kind == CharacterAnimationEventKind.WeaponCancelled && entry.Time < lastInputTime - 0.5d) continue;
                switch (entry.Kind)
                {
                    case CharacterAnimationEventKind.Reset:
                    case CharacterAnimationEventKind.Revived: ResetExecution(); break;
                    case CharacterAnimationEventKind.RebaseYaw: locomotion.RebaseYaw(entry.Value); break;
                    case CharacterAnimationEventKind.MotionDiscontinuity: locomotion.Reset(); continuityVersion++; break;
                }
            }
        }
        /// <summary>Plans once per monotonic render frame; no Animator parameters, states or layers are written here.</summary>
        public CharacterAnimationPlan Prepare(in CharacterAnimationState input, float deltaTime, int frameId)
        {
            if (frameId <= preparedFrame || preparedFrame > committedFrame) return nextPlan;
            if (double.IsNaN(input.Time) || double.IsInfinity(input.Time)) return nextPlan;
            preparedFrame = frameId; backend.Begin(animator); ConsumeEvents();
            lastInputTime = input.Time;
            float step = Mathf.Max(0f, deltaTime);
            if (!wasAlive && input.Alive || wasActive && !input.Active) ResetExecution();
            RefreshEquipmentIdentity(input);
            if (input.Alive) locomotion.Prepare(input, step, frameId);
            AnimationFacts facts = Facts(input);
            arbitrator.Resolve(profile, facts);
            for (int i = 0; i < previous.Length; i++)
            {
                if (arbitrator[(AnimationChannel)i] != null || previous[i] == null) continue;
                // A business shot fade already owns the entire exit. Its removal must not
                // restart the generic action fade from a channel weight that was still 1.
                bool completedShotFade = previous[i].playback == AnimationPlayback.Shot && weapon.ShotFading;
                weights[i] = completedShotFade ? 0f : Mathf.Min(weights[i], ikActions[i].Weight);
                if (completedShotFade) ikActions[i] = ikActions[i].Fade(0f);
            }
            var nextUpper = arbitrator[AnimationChannel.Upper];
            if (nextUpper != null && nextUpper.playback == AnimationPlayback.Shot && input.Shot.Active
                && weapon.ShotFading && !ikActions[1].Identity.Equals(input.Shot.Identity))
                weights[1] = ikActions[1].Weight;
            ApplyWeights(input, step);
            var baseAction = arbitrator[AnimationChannel.Base];
            var upper = arbitrator[AnimationChannel.Upper];
            var reaction = arbitrator[AnimationChannel.Reaction];
            if (previous[1] != upper)
            {
                if (previous[1] != null && previous[1].playback == AnimationPlayback.Shot && (upper == null || upper.playback != AnimationPlayback.Shot)) weapon.Reset(true);
                else if (upper == null || upper.playback != AnimationPlayback.Shot && upper.playback != AnimationPlayback.Reload) weapon.Reset();
                if (upper == null || upper.playback != AnimationPlayback.Consumable) consumable.Reset();
            }
            if (upper != null)
            {
                if (upper.playback == AnimationPlayback.Consumable) consumable.Apply(input.Consumable, input.Crouched, upper, input.Time);
                else
                {
                    bool sameShot = ikActions[1].Id != null && ikActions[1].Identity.Equals(input.Shot.Identity);
                    float sourceWeight = upper.playback == AnimationPlayback.Shot && input.Shot.FadingOut && sameShot
                        ? ikActions[1].Weight : weights[1];
                    weapon.Apply(input, upper, step, sourceWeight);
                }
            }
            if (reaction != null) life.ApplyHurt(input, reaction);
            else life.ClearHurt();
            // The animation backend has now produced its final contribution, including the
            // explicit shot-to-empty fade. IK consumes it without another fade or arbitration.
            for (int i = 0; i < ikActions.Length; i++)
            {
                var selected = arbitrator[(AnimationChannel)i];
                if (selected != null)
                {
                    bool shot = selected.playback == AnimationPlayback.Shot;
                    float contribution = shot ? weapon.ShotContribution : weights[i];
                    ikActions[i] = BuildContribution(selected, contribution, input, shot && weapon.ShotFading);
                }
                else
                    // Preserve source identity and phase while the channel fades. Do not relabel
                    // a retained action from a newer input snapshot, or raise an exhausted shot.
                    ikActions[i] = ikActions[i].Fade(weights[i]);
                if (i == 1) backend.SetLayerWeight(layers[i], ikActions[i].Weight);
            }
            if (baseAction != null)
            {
                if (baseAction.playback == AnimationPlayback.Life) life.ApplyDead(input.Crouched, baseAction);
                else locomotion.Apply(baseAction, 0f);
            }
            for (int i = 0; i < previous.Length; i++) previous[i] = arbitrator[(AnimationChannel)i];
            wasActive = input.Active; wasAlive = input.Alive;
            nextPlan = new CharacterAnimationPlan(input, locomotion.BodyYaw, locomotion.AnimationVelocity, step, frameId, continuityVersion,
                (CharacterActionContribution[])ikActions.Clone(), backend.Finish());
            return nextPlan;
        }
        /// <summary>Executes a published plan once; repeated graph evaluation is owned by the caller's pose graph.</summary>
        public CharacterAnimationOutput Present(CharacterAnimationPlan plan, int renderFrame)
        {
            if (plan == null || renderFrame <= executedFrame || !animator.isActiveAndEnabled) return Output;
            PresentNeutral();
            executedFrame = renderFrame; animator.fireEvents = !plan.State.Preview;
            foreach (var command in plan.Commands) command.Execute(animator);
            Output = BuildOutput(plan, renderFrame);
            var state = plan.State;
            var settings = profile.Locomotion;
            float reference = state.Crouched ? settings.crouchReferenceSpeed : state.Strafe ? settings.strafeReferenceSpeed : settings.freeReferenceSpeed;
            var facing = Quaternion.Euler(0f, Output.BodyYaw + Output.RenderYawOffset + Output.LocomotionYawOffset, 0f);
            Vector3 local = Quaternion.Inverse(facing) * new Vector3(plan.Velocity.x, 0f, plan.Velocity.y);
            Vector2 direction = Vector2.ClampMagnitude(new Vector2(local.x, local.z) / Mathf.Max(0.1f, reference), 1f);
            animator.SetFloat("AimMoveX", direction.x); animator.SetFloat("AimMoveY", direction.y);
            return Output;
        }
        public void Commit(int frameId)
        {
            if (frameId != preparedFrame || committedFrame == frameId) return;
            if (executedFrame == frameId)
            {
                lastFollowView = nextFollowView;
                committedContinuityVersion = nextContinuityVersion;
            }
            committedFrame = frameId;
        }
        private CharacterAnimationOutput BuildOutput(CharacterAnimationPlan plan, int renderFrame)
        {
            var state = plan.State;
            float body = state.Active && state.Alive ? 1f : 0f;
            float aim = 0f, yaw = 0f, yawWeight = 0f;
            bool livePose = false;
            foreach (var action in plan.Actions)
            {
                if (action.Id == null || action.Weight <= 0f || action.WeaponRelated && action.Identity.Subject != 0
                    && action.Identity.Subject != state.EquipmentIdentity) continue;
                AnimationActionConfiguration policy = null;
                foreach (var definition in profile.Actions)
                    if (definition.id == action.Id) { policy = definition; break; }
                if (policy == null) continue;
                if (!policy.allowBodyTurn) body *= 1f - action.Weight;
                if (!policy.allowAim) continue;
                aim = Mathf.Max(aim, action.Weight);
                yaw += (profile.YawOffset + policy.yawOffset) * action.Weight;
                yawWeight += action.Weight;
                livePose |= !action.FadingOut;
            }
            bool posing = state.Active && state.Alive && state.CanLook && aim > 0f;
            bool retainedFollow = plan.ContinuityVersion == committedContinuityVersion && lastFollowView;
            bool follow = posing && (state.Strafe || !livePose && retainedFollow);
            aim = posing ? aim * body : 0f;
            if (yawWeight > 0f) yaw /= yawWeight;
            // Continuity belongs to the executed committed plan, never the next plan currently being prepared.
            nextFollowView = follow; nextContinuityVersion = plan.ContinuityVersion;
            return new CharacterAnimationOutput(plan, renderFrame, body, aim, follow ? aim : 0f, yaw, bodyOrientationEnabled);
        }
        /// <summary>Ends this instance's model lifetime; queued events and privately planned state are discarded.</summary>
        public void Reset()
        {
            // Reset must also work before Animator initialization or after its native lifetime has ended.
            backend.ClearCommands(); ResetExecution(); backend.ClearCommands();
            needsNeutralPose = true; PresentNeutral();
            backend.ResetObservations(); events.Clear(); nextPlan = null;
            preparedFrame = executedFrame = committedFrame = -1; wasAlive = true; wasActive = false;
            equipmentIdentity = 0; equipmentVersion = 0; lastInputTime = 0d;
            lastFollowView = nextFollowView = false; Output = null;
            committedContinuityVersion = nextContinuityVersion = continuityVersion;
        }
        /// <summary>Apply a pending lifecycle neutral pose only when the Animator's native instance is usable.</summary>
        public void PresentNeutral()
        {
            if (!needsNeutralPose || animator == null || !animator.isActiveAndEnabled || !animator.isInitialized) return;
            RestoreNeutralPose(); needsNeutralPose = false;
        }
        private void RestoreNeutralPose()
        {
            animator.SetLayerWeight(layers[0], 1f);
            for (int i = 1; i < 3; i++)
            {
                animator.SetLayerWeight(layers[i], 0f);
                string empty = profile.Channel((AnimationChannel)i).emptyState;
                if (!string.IsNullOrWhiteSpace(empty)) animator.Play(Animator.StringToHash(empty), layers[i], 0f);
            }
            AnimationActionConfiguration neutral = null;
            foreach (var action in profile.Actions)
                if (action != null && action.playback == AnimationPlayback.Locomotion)
                {
                    if (neutral == null) neutral = action;
                    if (action.Matches(AnimationFacts.Alive)) { neutral = action; break; }
                }
            if (neutral != null) animator.Play(neutral.StateHash(false), layers[0], 0f);
            animator.SetFloat("MoveSpeed", 0f); animator.SetFloat("AimMoveX", 0f); animator.SetFloat("AimMoveY", 0f);
            animator.SetFloat("AimLocomotionSpeed", 1f); animator.SetFloat("TurnSpeed", 1f);
        }
        private static CharacterActionContribution BuildContribution(AnimationActionConfiguration definition, float weight, in CharacterAnimationState state, bool fadingOut)
        {
            CharacterActionState action = default;
            if (definition != null)
                switch (definition.playback)
                {
                    case AnimationPlayback.Shot: action = state.Shot; break;
                    case AnimationPlayback.Reload: action = state.Reload; break;
                    case AnimationPlayback.Consumable: action = state.Consumable; break;
                    case AnimationPlayback.Hurt: action = state.Hurt; break;
                }
            return new CharacterActionContribution(definition != null ? definition.id : null, weight, action.PhaseAt(state.Time), action.Identity,
                fadingOut, definition != null && (definition.playback == AnimationPlayback.Shot || definition.playback == AnimationPlayback.Reload),
                definition != null && definition.allowAim);
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
                bool shot = state.Shot.ValidAt(state.Time) && state.Shot.Identity.Subject == state.EquipmentIdentity
                    && weapon.CanPresentShot(state.Shot.Identity);
                if (shot && state.Shot.Active) facts |= AnimationFacts.Shooting;
                if (shot && state.Shot.FadingOut) facts |= AnimationFacts.ShotFade;
                if (state.Shot.Automatic) facts |= AnimationFacts.Automatic;
                if (state.Reload.Active && state.Reload.ValidAt(state.Time) && state.Reload.Identity.Subject == state.EquipmentIdentity) facts |= AnimationFacts.Reloading;
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
                if (action != null && action.playback == AnimationPlayback.Shot
                    && (state.Shot.FadingOut || !state.Shot.Automatic && duration >= state.Shot.Duration)) force = true;
                weights[i] = force || duration <= 0f ? target : Mathf.MoveTowards(weights[i], target, deltaTime / duration);
                if (action != null) fadingActions[i] = action;
                else if (weights[i] <= 0f) fadingActions[i] = null;
                if (i < 3) backend.SetLayerWeight(layers[i], i == 0 ? 1f : weights[i]);
            }
        }
        private void RefreshEquipmentIdentity(in CharacterAnimationState input)
        {
            if (equipmentIdentity == input.EquipmentIdentity && equipmentVersion == input.EquipmentVersion) return;
            bool replacedItem = equipmentIdentity != input.EquipmentIdentity;
            equipmentIdentity = input.EquipmentIdentity; equipmentVersion = input.EquipmentVersion;
            // Equipment replacement invalidates old weapon contributions even on a zero-time
            // same-render-frame refresh; non-weapon reaction/consumable channels retain identity.
            for (int i = 0; i < fadingActions.Length; i++)
            {
                var retained = fadingActions[i];
                if (retained == null || retained.playback != AnimationPlayback.Shot && retained.playback != AnimationPlayback.Reload) continue;
                fadingActions[i] = previous[i] = null; weights[i] = 0f; ikActions[i] = default;
                if (i > 0 && i < 3) backend.SetLayerWeight(layers[i], 0f);
            }
            // Rebuilding the same item's model must not blacklist its still-valid action.
            weapon.Reset(replacedItem);
        }
        private void ResetExecution()
        {
            continuityVersion++;
            backend.ResetObservations();
            arbitrator.Reset(); locomotion.Reset(); weapon.Reset(); consumable.Reset(); life.Reset();
            for (int i = 0; i < previous.Length; i++) { previous[i] = fadingActions[i] = null; ikActions[i] = default; weights[i] = 0f; if (i > 0 && i < 3) backend.SetLayerWeight(layers[i], 0f); }
        }
    }
}
