using UnityEngine;

namespace RPG4.CharacterAnimation
{
    internal sealed class PlayerLocomotionPresentation
    {
        private static readonly int MoveSpeed = Animator.StringToHash("MoveSpeed");
        private static readonly int AimMoveX = Animator.StringToHash("AimMoveX");
        private static readonly int AimMoveY = Animator.StringToHash("AimMoveY");
        private static readonly int AimLocomotionSpeed = Animator.StringToHash("AimLocomotionSpeed");
        private static readonly int TurnSpeed = Animator.StringToHash("TurnSpeed");
        private readonly CharacterAnimationBackend animator;
        private readonly CharacterAnimationProfile profile;
        private readonly int layer;
        private Vector2 animationVelocity;
        private float bodyYaw, turnStartYaw, turnAngle;
        private int turnAnimation, turnState, turnStartedFrame;
        private bool initialized, crouched, postureChanged, wasTurning, startedTurn;
        private CharacterAnimationState frame;
        internal float BodyYaw => bodyYaw;
        internal Vector2 AnimationVelocity => animationVelocity;
        internal int TurnDirection => turnAnimation;
        internal PlayerLocomotionPresentation(CharacterAnimationBackend animator, CharacterAnimationProfile profile, int layer)
        { this.animator = animator; this.profile = profile; this.layer = layer; }

        internal void Prepare(in CharacterAnimationState input, float deltaTime, int frameId)
        {
            frame = input;
            if (!initialized) { bodyYaw = input.BodyYaw; crouched = input.Crouched; initialized = true; }
            var settings = profile.Locomotion;
            var state = CurrentState();
            Vector2 velocity = frame.CanMove ? new Vector2(frame.PlanarVelocity.x, frame.PlanarVelocity.z) : Vector2.zero;
            float smooth = settings.parameterSmoothTime;
            animationVelocity = Vector2.Lerp(animationVelocity, velocity, smooth <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / smooth));
            postureChanged = crouched != frame.Crouched;
            wasTurning = turnAnimation != 0;
            startedTurn = UpdateBody(state, velocity.magnitude, deltaTime, frameId);
        }
        private AnimatorStateInfo CurrentState() => animator.IsInTransition(layer)
            ? animator.GetNextAnimatorStateInfo(layer) : animator.GetCurrentAnimatorStateInfo(layer);
        internal void Apply(AnimationActionConfiguration action, float yawOffset)
        {
            if (action == null) return;
            var settings = profile.Locomotion;
            var state = CurrentState();
            Quaternion facing = Quaternion.Euler(0f, bodyYaw + frame.RenderYawOffset + yawOffset, 0f);
            Vector3 local = Quaternion.Inverse(facing) * new Vector3(animationVelocity.x, 0f, animationVelocity.y);
            float referenceSpeed = frame.Crouched ? settings.crouchReferenceSpeed
                : frame.Strafe ? settings.strafeReferenceSpeed : settings.freeReferenceSpeed;
            float speed = animationVelocity.magnitude;
            Vector2 direction = Vector2.ClampMagnitude(new Vector2(local.x, local.z) / Mathf.Max(0.1f, referenceSpeed), 1f);
            animator.SetFloat(MoveSpeed, speed);
            animator.SetFloat(AimMoveX, direction.x); animator.SetFloat(AimMoveY, direction.y);
            animator.SetFloat(AimLocomotionSpeed, Mathf.Clamp(speed / Mathf.Max(0.1f, referenceSpeed), 1f, 1.5f));
            animator.SetFloat(TurnSpeed, Mathf.Clamp(Mathf.Abs(Mathf.DeltaAngle(bodyYaw, frame.ViewYaw)) / 90f, 1f, 2f));
            int targetState = action.StateHash(frame.Crouched);
            if (action.playback == AnimationPlayback.Turn) turnState = targetState;
            else turnAnimation = 0;
            if (state.fullPathHash != targetState || startedTurn && action.playback == AnimationPlayback.Turn)
            {
                float transition = postureChanged ? settings.postureTransition : action.fadeIn;
                bool preserve = action.progress != AnimationProgress.Restart && action.playback == AnimationPlayback.Locomotion
                    && !wasTurning && IsLocomotion(state.fullPathHash) && speed > settings.turnMoveThreshold;
                if (preserve) animator.CrossFade(targetState, transition / Mathf.Max(0.01f, state.length), layer,
                    Mathf.Repeat(state.normalizedTime, 1f), true);
                else animator.CrossFadeInFixedTime(targetState, transition, layer, 0f, !startedTurn);
            }
            crouched = frame.Crouched;
        }
        private bool UpdateBody(AnimatorStateInfo state, float speed, float deltaTime, int frameId)
        {
            var settings = profile.Locomotion;
            bool moving = frame.HasMoveInput || speed > settings.turnMoveThreshold;
            bool canTurn = frame.CanMove && frame.Strafe && !moving;
            if (!canTurn || postureChanged) turnAnimation = 0;
            if (deltaTime <= 0f) return false;
            if (!canTurn)
            {
                float target = frame.Strafe ? frame.ViewYaw : frame.BodyYaw;
                float smooth = frame.Strafe ? settings.strafeTurnSmoothTime : settings.freeTurnSmoothTime;
                bodyYaw = Mathf.LerpAngle(bodyYaw, target, smooth <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / smooth));
                return false;
            }
            float error = Mathf.DeltaAngle(bodyYaw, frame.ViewYaw);
            if (turnAnimation != 0)
            {
                if (state.fullPathHash == turnState && turnStartedFrame != frameId)
                {
                    float progress = Mathf.Clamp01(state.normalizedTime);
                    bodyYaw = turnStartYaw + turnAngle * Mathf.SmoothStep(0f, 1f, progress);
                    error = Mathf.DeltaAngle(bodyYaw, frame.ViewYaw);
                    bool settled = progress >= 0.85f && Mathf.Abs(error) <= Mathf.Min(settings.turnExitThreshold, settings.turnThreshold);
                    bool reversed = error * turnAnimation < -settings.turnThreshold;
                    if (progress >= 1f || settled || reversed) turnAnimation = 0;
                }
                if (turnAnimation != 0) return false;
            }
            if (postureChanged || Mathf.Abs(error) < settings.turnThreshold) return false;
            turnAnimation = error < 0f ? -1 : 1;
            turnStartYaw = bodyYaw; turnAngle = Mathf.Clamp(error, -90f, 90f); turnStartedFrame = frameId;
            return true;
        }
        private bool IsLocomotion(int hash)
        {
            foreach (var action in profile.Actions)
                if (action.playback == AnimationPlayback.Locomotion
                    && (hash == action.StateHash(false) || hash == action.StateHash(true))) return true;
            return false;
        }
        internal void RebaseYaw(float correction) { bodyYaw += correction; turnStartYaw += correction; }
        internal void Reset() { initialized = false; animationVelocity = Vector2.zero; turnAnimation = turnState = 0; }
    }
}
