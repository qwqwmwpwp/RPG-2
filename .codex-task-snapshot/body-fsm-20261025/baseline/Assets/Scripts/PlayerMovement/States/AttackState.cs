using System;
using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>持枪父状态：协调开火、射击表现及瞄准与腰射共用的方向运动。</summary>
    internal sealed class AttackState : State
    {
        [Serializable]
        internal sealed class Data
        {
            [SerializeField] private AnimationClip shotClip;
            [SerializeField] private AnimationClip autoShotClip;
            [SerializeField, Min(0.01f)] private float shotInterval = 0.16f;
            [SerializeField, Min(0.01f)] private float shotAnimationDuration = 0.24f;
            [Tooltip("首发进入上半身射击状态的过渡时间。")]
            [SerializeField, Min(0f)] private float layerFadeInDuration = 0.06f;
            [Tooltip("末发完整结束后过渡到 Empty 的时间。")]
            [SerializeField, Min(0f)] private float layerFadeOutDuration = 0.12f;

            internal AnimationClip ShotClip => shotClip;
            internal AnimationClip AutoShotClip => autoShotClip;
            internal float ShotInterval => shotInterval;
            internal float ShotAnimationDuration => shotAnimationDuration;
            internal float LayerFadeInDuration => layerFadeInDuration;
            internal float LayerFadeOutDuration => layerFadeOutDuration;

            internal void Validate()
            {
                shotInterval = Mathf.Max(0.01f, shotInterval);
                shotAnimationDuration = Mathf.Max(0.01f, shotAnimationDuration);
                layerFadeInDuration = Mathf.Max(0f, layerFadeInDuration);
                layerFadeOutDuration = Mathf.Max(0f, layerFadeOutDuration);
            }
        }

        private static readonly int AimMoveX = Animator.StringToHash("AimMoveX");
        private static readonly int AimMoveY = Animator.StringToHash("AimMoveY");
        private static readonly int LocomotionSpeed = Animator.StringToHash("AimLocomotionSpeed");
        private static readonly int ShotSpeed = Animator.StringToHash("ShotSpeed");
        private static readonly int HipFireAnimation = Animator.StringToHash("UpperBody Shooting.HipFire");
        private static readonly int ADSFire = Animator.StringToHash("UpperBody Shooting.ADSFire");
        private static readonly int HipAutoFire = Animator.StringToHash("UpperBody Shooting.HipAutoFire");
        private static readonly int ADSAutoFire = Animator.StringToHash("UpperBody Shooting.ADSAutoFire");
        private static readonly int Empty = Animator.StringToHash("UpperBody Shooting.Empty");
        private const int ShootingLayer = 1;

        private bool hasShotProjection;
        private bool ads;
        private bool automatic;
        private bool fadingProjection;
        private bool resetAnimator = true;

        private readonly PlayerFireAction fireAction = new PlayerFireAction();
        private readonly Data attackData;
        private PlayerFireAction.Frame fireFrame;
        private readonly PlayerRootState root;
        private readonly PlayerContext context;
        private readonly AimState.Data data;
        private PlayerMode mode;
        private float locomotionSpeed;
        private float turnVelocity;
        private Vector2 animationMove;

        internal AimState Aim { get; }
        internal HipFireState HipFire { get; }

        internal AttackState(HierarchicalStateMachine machine, PlayerRootState parent, PlayerContext context)
            : base(machine, parent)
        {
            root = parent;
            this.context = context;
            data = context.Aim;
            attackData = context.Attack;
            ClearPresentation();
            Aim = new AimState(machine, this, context);
            HipFire = new HipFireState(machine, this, context);
        }

        internal void PrepareFrame(float deltaTime, double now)
        {
            bool canUseWeapon = context.CanUseWeapon && deltaTime > 0f;
            if (!canUseWeapon)
            {
                root.CancelWeaponAction();
                return;
            }

            fireFrame = fireAction.Tick(context.Input.FirePressed, context.Input.FireHeld, now,
                attackData.ShotInterval, attackData.ShotAnimationDuration, attackData.LayerFadeOutDuration);
            context.ResolveMode(true, context.Input.AimHeld,
                fireAction.HasWeaponAction, fireAction.HasShotAction);
        }

        internal void CancelWeaponAction()
        {
            fireAction.Cancel();
            fireFrame = default;
            ClearPresentation();
        }

        protected override State GetInitialState() => context.Mode == PlayerMode.Aim ? (State)Aim : HipFire;

        protected override State GetTransition() => context.Mode == PlayerMode.Exploration
            ? root.Exploration : null;

        protected override void OnEnter()
        {
            turnVelocity = 0f;
            if (context.AnimatorIsAvailable)
            {
                Animator animator = context.Content.Animator;
                animationMove = new Vector2(animator.GetFloat(AimMoveX), animator.GetFloat(AimMoveY));
            }
        }

        internal void SetLocomotion(PlayerMode mode, float locomotionSpeed)
        {
            this.mode = mode;
            this.locomotionSpeed = locomotionSpeed;
            turnVelocity = 0f;
        }

        protected override void OnUpdate(float deltaTime)
        {
            TickAnimation(fireFrame, context.Mode == PlayerMode.Aim);
            if (!context.AnimatorIsAvailable) return;
            Animator animator = context.Content.Animator;
            animator.SetFloat(LocomotionSpeed, locomotionSpeed);
            SetAnimationMove(animationMove);
            // 互切不重播腿部；Animator 恢复后也能投影当前逻辑模式。
            int stateHash = Animator.StringToHash(data.AimAnimation);
            if (animator.GetCurrentAnimatorStateInfo(0).fullPathHash != stateHash
                && (!animator.IsInTransition(0)
                    || animator.GetNextAnimatorStateInfo(0).fullPathHash != stateHash))
                animator.CrossFadeInFixedTime(data.AimAnimation,
                    data.AnimationTransitionDuration, layer: 0);
        }

        protected override void OnExit()
        {
            CancelWeaponAction();
            turnVelocity = 0f;
        }

        protected override void OnFixedUpdate(float deltaTime)
        {
            if (!context.CanUseWeapon || context.Mode != mode)
            {
                context.SetPlanarVelocity(Vector3.zero);
                turnVelocity = 0f;
                return;
            }

            Rigidbody body = context.Content.Body;
            float viewYaw = context.Content.CameraController.ViewYaw;
            Vector2 move = Vector2.ClampMagnitude(context.Input.Move, 1f);
            if (move.sqrMagnitude <= data.MoveInputThreshold * data.MoveInputThreshold)
                move = Vector2.zero;
            Vector3 direction = Quaternion.Euler(0f, viewYaw, 0f) * new Vector3(move.x, 0f, move.y);
            float moveSpeed = mode == PlayerMode.HipFire ? context.HipFire.MoveSpeed : data.MoveSpeed;
            context.SetPlanarVelocity(direction * moveSpeed);

            // 原地也朝向观察 yaw；旋转只使用 Y 轴，根节点始终直立。
            float yaw = Mathf.SmoothDampAngle(body.rotation.eulerAngles.y, viewYaw,
                ref turnVelocity, data.TurnSmoothTime, Mathf.Infinity, deltaTime);
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            body.MoveRotation(rotation);

            Vector3 localDirection = Quaternion.Inverse(rotation) * direction;
            Vector2 target = Vector2.ClampMagnitude(new Vector2(localDirection.x, localDirection.z), 1f);
            float blend = data.AnimationParameterSmoothTime <= 0f ? 1f
                : 1f - Mathf.Exp(-deltaTime / data.AnimationParameterSmoothTime);
            animationMove = Vector2.ClampMagnitude(Vector2.Lerp(animationMove, target, blend), 1f);
            SetAnimationMove(animationMove);
        }

        private bool AnimatorAvailable => context.AnimatorIsAvailable && context.Content.Animator.layerCount > ShootingLayer;

        private void TickAnimation(PlayerFireAction.Frame frame, bool wantsAds)
        {
            Animator animator = context.Content.Animator;
            if (!AnimatorAvailable || attackData.ShotClip == null)
            {
                // 表现不可用不反馈给逻辑；恢复时清空旧画面，只响应恢复当帧的新发。
                if (!AnimatorAvailable) resetAnimator = true;
                ClearPresentation();
                return;
            }

            if (resetAnimator) ClearPresentation();
            if (hasShotProjection && automatic && attackData.AutoShotClip == null) ClearPresentation();
            bool wantsAutomatic = frame.IsAutomatic && attackData.AutoShotClip != null;
            int state = wantsAutomatic ? (wantsAds ? ADSAutoFire : HipAutoFire)
                : (wantsAds ? ADSFire : HipFireAnimation);
            if (frame.ShotStarted)
            {
                if (wantsAutomatic)
                {
                    // 连射循环只在进入时过渡，后续每发不重播动画。
                    if (!hasShotProjection || !automatic || fadingProjection)
                        animator.CrossFadeInFixedTime(state, attackData.LayerFadeInDuration, ShootingLayer, 0f);
                    else if (ads != wantsAds)
                        SwitchAutomaticState(state);
                }
                else
                {
                    animator.SetFloat(ShotSpeed, attackData.ShotClip.length / Mathf.Max(0.01f, attackData.ShotAnimationDuration));
                    if (!hasShotProjection || automatic)
                        animator.CrossFadeInFixedTime(state, attackData.LayerFadeInDuration, ShootingLayer, 0f);
                    else
                        animator.Play(state, ShootingLayer, 0f);
                }
                hasShotProjection = true;
                ads = wantsAds;
                automatic = wantsAutomatic;
                fadingProjection = false;
            }
            else if (frame.FadeStarted && hasShotProjection)
            {
                animator.CrossFadeInFixedTime(Empty, attackData.LayerFadeOutDuration, ShootingLayer, 0f);
                fadingProjection = true;
            }
            else if (frame.ShotActive && hasShotProjection && ads != wantsAds)
            {
                // 模式切换只延续已有投影，不生成新发或补播缺失期间的动作。
                if (automatic)
                    SwitchAutomaticState(wantsAds ? ADSAutoFire : HipAutoFire);
                else
                    animator.Play(wantsAds ? ADSFire : HipFireAnimation, ShootingLayer, frame.ShotPhase);
                ads = wantsAds;
            }
            if (!frame.ShotActive && !frame.FadingOut)
            {
                hasShotProjection = false;
                automatic = false;
                fadingProjection = false;
            }
        }

        private void SwitchAutomaticState(int state)
        {
            Animator animator = context.Content.Animator;
            AnimatorStateInfo source = animator.GetCurrentAnimatorStateInfo(ShootingLayer);
            if (animator.IsInTransition(ShootingLayer))
            {
                AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(ShootingLayer);
                if (IsAutomaticState(next)) source = next;
            }
            float phase = IsAutomaticState(source) ? source.normalizedTime : 0f;
            animator.Play(state, ShootingLayer, phase);
        }

        private static bool IsAutomaticState(AnimatorStateInfo state) => state.fullPathHash == HipAutoFire
            || state.fullPathHash == ADSAutoFire;

        private void ClearPresentation()
        {
            resetAnimator |= hasShotProjection;
            hasShotProjection = false;
            ads = false;
            automatic = false;
            fadingProjection = false;
            if (resetAnimator && AnimatorAvailable)
            {
                context.Content.Animator.Play(Empty, ShootingLayer, 0f);
                resetAnimator = false;
            }
        }

        private void SetAnimationMove(Vector2 move)
        {
            if (!context.AnimatorIsAvailable) return;
            context.Content.Animator.SetFloat(AimMoveX, move.x);
            context.Content.Animator.SetFloat(AimMoveY, move.y);
        }
    }
}
