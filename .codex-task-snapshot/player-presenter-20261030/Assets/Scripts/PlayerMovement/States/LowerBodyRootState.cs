using RPG4.PlayerIntent;
using RPG4.StateMachine;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>下身独占刚体平面移动、转向、胶囊及腿部动画。</summary>
    internal sealed class LowerBodyRootState : State
    {
        private static readonly int MoveSpeed = Animator.StringToHash("MoveSpeed");
        private static readonly int AimMoveX = Animator.StringToHash("AimMoveX");
        private static readonly int AimMoveY = Animator.StringToHash("AimMoveY");
        private static readonly int AimLocomotionSpeed = Animator.StringToHash("AimLocomotionSpeed");
        private readonly PlayerContext context;
        private readonly float standingHeight;
        private readonly Vector3 standingCenter;
        private readonly Collider[] standHits = new Collider[32];
        internal bool PhysicallyCrouched { get; private set; }
        private float turnVelocity;
        private Rigidbody strafeBody;
        private float originalMaxAngularVelocity;
        private Vector2 movement;
        private Vector2 animationMove;
        private float previousViewYaw;
        private bool hasViewYaw;
        private int turnAnimation;
        internal PostureState Standing { get; }
        internal PostureState Crouching { get; }
        internal LowerBodyRootState(HierarchicalStateMachine machine, PlayerContext context) : base(machine)
        {
            this.context = context;
            standingHeight = context.Content.Capsule.height;
            standingCenter = context.Content.Capsule.center;
            Standing = new PostureState(machine, this, context, false);
            Crouching = new PostureState(machine, this, context, true);
        }
        protected override State GetInitialState() => context.ReadState.Locomotion.Plan.Crouched ? Crouching : Standing;
        protected override void OnFixedUpdate(float deltaTime)
        {
            if (!context.ReadState.Locomotion.CanMove)
            {
                RestoreStrafeAngularLimit();
                return;
            }
            bool applied = ApplyPosture(context.ReadState.Locomotion.Plan.Crouched);
            context.Intents.RecordPosture(PhysicallyCrouched, !PhysicallyCrouched
                || (applied && context.ReadState.Locomotion.CanStand));
        }

        private bool ApplyPosture(bool wantsCrouch)
        {
            // 再次检测，避免 Update 到 FixedUpdate 间出现障碍；失败时保持当前胶囊。
            if (!wantsCrouch && PhysicallyCrouched && !CanStand()) return false;
            CapsuleCollider capsule = context.Content.Capsule;
            float height = wantsCrouch ? Mathf.Clamp(context.Data.Crouch.Height,
                capsule.radius * 2f, standingHeight) : standingHeight;
            capsule.height = height;
            capsule.center = standingCenter + Vector3.up * ((height - standingHeight) * 0.5f);
            PhysicallyCrouched = wantsCrouch;
            return true;
        }

        internal bool CanStand()
        {
            CapsuleCollider capsule = context.Content.Capsule;
            Transform transform = capsule.transform;
            Vector3 scale = transform.lossyScale;
            float radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float height = Mathf.Max(radius * 2f, standingHeight * Mathf.Abs(scale.y));
            Vector3 up = transform.up;
            Vector3 center = transform.TransformPoint(standingCenter);
            // 内缩少量边界，静止地面接触不应阻止站起；自身与 trigger 一律排除。
            const float skin = 0.01f;
            float checkRadius = Mathf.Max(0.001f, radius - skin);
            Vector3 bottom = center - up * (height * 0.5f - radius);
            Vector3 top = center + up * (height * 0.5f - radius);
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, checkRadius, standHits,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            if (count == standHits.Length) return false;
            for (int i = 0; i < count; i++)
            {
                Collider hit = standHits[i];
                if (hit == capsule || hit.attachedRigidbody == context.Content.Body
                    || hit.transform.IsChildOf(transform)) continue;
                return false;
            }
            return true;
        }
        internal void ResetTurn()
        {
            turnVelocity = 0f;
            hasViewYaw = false;
            turnAnimation = 0;
        }

        internal void Animate(bool strafe, float deltaTime)
        {
            if (!context.AnimatorIsAvailable) return;
            Animator animator = context.Content.Animator;
            LocomotionPlan plan = context.ReadState.Locomotion.Plan;
            Vector2 target = Vector2.zero;
            if (plan.CanMove && context.ReadState.Locomotion.CanMove && plan.MoveSpeed > 0f)
            {
                Vector3 direction = new Vector3(movement.x, 0f, movement.y) / plan.MoveSpeed;
                // Strafe 的可见骨架已对齐观察角，移动动画也采用同一朝向基准。
                Quaternion facing = strafe ? Quaternion.Euler(0f, plan.ViewYaw, 0f) : context.Content.Body.rotation;
                Vector3 local = Quaternion.Inverse(facing) * direction;
                target = Vector2.ClampMagnitude(new Vector2(local.x, local.z), 1f);
            }
            float smooth = context.Data.Aim.AnimationParameterSmoothTime;
            animationMove = plan.CanMove && context.ReadState.Locomotion.CanMove
                ? SmoothMove(animationMove, target, smooth, smooth, deltaTime) : Vector2.zero;
            AnimateState(animator, plan, strafe, deltaTime);
            animator.SetFloat(MoveSpeed, animationMove.magnitude);
            animator.SetFloat(AimMoveX, animationMove.x);
            animator.SetFloat(AimMoveY, animationMove.y);
            animator.SetFloat(AimLocomotionSpeed, plan.AnimationSpeed);
        }

        private void AnimateState(Animator animator, LocomotionPlan plan, bool strafe, float deltaTime)
        {
            float yawSpeed = hasViewYaw && deltaTime > 0f
                ? Mathf.DeltaAngle(previousViewYaw, plan.ViewYaw) / deltaTime : 0f;
            previousViewYaw = plan.ViewYaw;
            hasViewYaw = true;
            AimData settings = context.Data.Aim;
            AnimatorStateInfo state = animator.IsInTransition(0)
                ? animator.GetNextAnimatorStateInfo(0) : animator.GetCurrentAnimatorStateInfo(0);
            bool canTurn = strafe && plan.CanMove && context.ReadState.Pose.IsAiming
                && Mathf.Max(plan.Move.magnitude, animationMove.magnitude) <= settings.TurnAnimationMoveThreshold;
            int requestedTurn = canTurn && Mathf.Abs(yawSpeed) > settings.TurnAnimationThreshold
                ? (yawSpeed < 0f ? -1 : 1) : 0;
            bool wasTurning = turnAnimation != 0;
            if (!canTurn) turnAnimation = 0;
            else if (requestedTurn != 0) turnAnimation = requestedTurn;
            else if (turnAnimation != 0 && state.normalizedTime >= 1f) turnAnimation = 0;

            string animation = turnAnimation != 0
                ? "Base Layer." + (plan.Crouched ? "CrouchAimTurn" : "AimTurn")
                    + (turnAnimation < 0 ? "Left" : "Right")
                : plan.Crouched
                    ? (strafe ? "Base Layer.CrouchAimLocomotion" : "Base Layer.CrouchFreeLocomotion")
                    : strafe ? settings.AimAnimation : "Base Layer.FreeLocomotion";
            int hash = Animator.StringToHash(animation);
            // 持续转身时重播已完成的单次片段；开始移动时可以立即打断，避免等待片尾。
            bool repeatTurn = turnAnimation != 0 && state.fullPathHash == hash && state.normalizedTime >= 1f;
            if (state.fullPathHash == hash && !repeatTurn) return;
            float transition = wasTurning || turnAnimation != 0 ? settings.TurnAnimationTransitionDuration
                : strafe ? settings.AnimationTransitionDuration : context.Data.Exploration.AnimationTransitionDuration;
            animator.CrossFadeInFixedTime(animation, transition, layer: 0, fixedTimeOffset: 0f);
        }

        private static Vector2 SmoothMove(Vector2 current, Vector2 target,
            float directionSmoothTime, float speedSmoothTime, float deltaTime)
        {
            if (deltaTime <= 0f) return current;
            float speedBlend = speedSmoothTime <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / speedSmoothTime);
            float speed = Mathf.Lerp(current.magnitude, target.magnitude, speedBlend);
            if (speed < 0.001f) return Vector2.zero;
            Vector2 direction = target.sqrMagnitude > 0.000001f ? target : current;
            float yaw = Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;
            if (current.sqrMagnitude > 0.000001f)
            {
                float directionBlend = directionSmoothTime <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / directionSmoothTime);
                // 分开平滑角度和强度，180°反向也不会穿过待机混合点。
                yaw = Mathf.LerpAngle(Mathf.Atan2(current.x, current.y) * Mathf.Rad2Deg, yaw, directionBlend);
            }
            return new Vector2(Mathf.Sin(yaw * Mathf.Deg2Rad), Mathf.Cos(yaw * Mathf.Deg2Rad)) * speed;
        }

        internal void Move(bool strafe, float deltaTime)
        {
            Rigidbody body = context.Content.Body;
            if (body == null || body.isKinematic)
            {
                movement = Vector2.zero;
                RestoreStrafeAngularLimit();
                return;
            }
            LocomotionPlan plan = context.ReadState.Locomotion.Plan;
            if (!plan.CanMove || !context.ReadState.Locomotion.CanMove)
            {
                movement = Vector2.zero;
                ClearPlanarVelocity();
                RestoreStrafeAngularLimit();
                turnVelocity = 0f;
                return;
            }
            Vector3 direction = Quaternion.Euler(0f, plan.ViewYaw, 0f)
                * new Vector3(plan.Move.x, 0f, plan.Move.y);
            Vector2 target = new Vector2(direction.x, direction.z) * plan.MoveSpeed;
            movement = Vector2.ClampMagnitude(SmoothMove(movement, target,
                context.Data.Movement.DirectionSmoothTime, context.Data.Movement.SpeedSmoothTime, deltaTime), plan.MoveSpeed);
            SetPlanarVelocity(new Vector3(movement.x, 0f, movement.y));
            if (strafe)
            {
                // 清速之后再提交偏航角速度，由物理积分与插值完成本步转向。
                if (deltaTime <= 0f)
                {
                    RestoreStrafeAngularLimit();
                    return;
                }
                if (strafeBody != body)
                {
                    RestoreStrafeAngularLimit();
                    strafeBody = body;
                    originalMaxAngularVelocity = body.maxAngularVelocity;
                }
                float bodyYaw = body.rotation.eulerAngles.y;
                float yaw = context.Data.Aim.TurnSmoothTime <= 0f ? plan.ViewYaw
                    : Mathf.SmoothDampAngle(bodyYaw, plan.ViewYaw, ref turnVelocity,
                        context.Data.Aim.TurnSmoothTime, Mathf.Infinity, deltaTime);
                float yawSpeed = Mathf.DeltaAngle(bodyYaw, yaw)
                    * Mathf.Deg2Rad / deltaTime;
                // 默认角速度上限可能截断快速观察角；仅在 Strafe 期间按需提高。
                body.maxAngularVelocity = Mathf.Max(originalMaxAngularVelocity, Mathf.Abs(yawSpeed));
                body.angularVelocity = Vector3.up * yawSpeed;
                return;
            }
            RestoreStrafeAngularLimit();
            if (movement == Vector2.zero)
            {
                turnVelocity = 0f;
                return;
            }
            float targetYaw = Mathf.Atan2(movement.x, movement.y) * Mathf.Rad2Deg;
            float freeYaw = context.Data.Exploration.TurnSmoothTime <= 0f ? targetYaw
                : Mathf.SmoothDampAngle(body.rotation.eulerAngles.y, targetYaw,
                    ref turnVelocity, context.Data.Exploration.TurnSmoothTime, Mathf.Infinity, deltaTime);
            body.MoveRotation(Quaternion.Euler(0f, freeYaw, 0f));
        }
        private void SetPlanarVelocity(Vector3 velocity)
        {
            Rigidbody body = context.Content.Body;
            if (body == null || body.isKinematic) return;
            body.velocity = new Vector3(velocity.x, body.velocity.y, velocity.z);
            body.angularVelocity = Vector3.zero;
        }
        private void ClearPlanarVelocity() => SetPlanarVelocity(Vector3.zero);
        internal void RestoreStrafeAngularLimit()
        {
            if (strafeBody != null) strafeBody.maxAngularVelocity = originalMaxAngularVelocity;
            strafeBody = null;
        }
        protected override void OnExit()
        {
            // 生命周期停止允许一次清速；不改胶囊，低顶下禁用/恢复不会强制起身。
            ClearPlanarVelocity();
            RestoreStrafeAngularLimit();
            ResetTurn();
            movement = Vector2.zero;
            animationMove = Vector2.zero;
            // Player 可独立于 Animator 禁用；清掉移动参数，按实际胶囊姿态投影待机。
            Animator animator = context.Content.Animator;
            if (animator == null || animator.runtimeAnimatorController == null) return;
            animator.SetFloat(MoveSpeed, 0f);
            animator.SetFloat(AimMoveX, 0f);
            animator.SetFloat(AimMoveY, 0f);
            animator.SetFloat(AimLocomotionSpeed, 1f);
            animator.Play(PhysicallyCrouched ? "Base Layer.CrouchFreeLocomotion"
                : "Base Layer.FreeLocomotion", 0, 0f);
        }
    }

    /// <summary>站立/蹲伏两个兄弟组；进入组时按本帧完整计划选择 Free/Strafe。</summary>
    internal sealed class PostureState : State
    {
        private readonly LowerBodyRootState root;
        private readonly PlayerContext context;
        private readonly bool crouched;
        internal LocomotionState Free { get; }
        internal LocomotionState Strafe { get; }
        internal PostureState(HierarchicalStateMachine machine, LowerBodyRootState parent,
            PlayerContext context, bool crouched) : base(machine, parent)
        {
            root = parent;
            this.context = context;
            this.crouched = crouched;
            Free = new LocomotionState(machine, this, root, context, false);
            Strafe = new LocomotionState(machine, this, root, context, true);
        }
        protected override State GetInitialState() => context.ReadState.Locomotion.Plan.Strafe ? Strafe : Free;
        protected override State GetTransition() => context.ReadState.Locomotion.Plan.Crouched == crouched ? null
            : crouched ? root.Standing : root.Crouching;
    }

    internal sealed class LocomotionState : State
    {
        private readonly PostureState posture;
        private readonly LowerBodyRootState root;
        private readonly PlayerContext context;
        private readonly bool strafe;
        internal LocomotionState(HierarchicalStateMachine machine, PostureState parent,
            LowerBodyRootState root, PlayerContext context, bool strafe) : base(machine, parent)
        {
            posture = parent;
            this.root = root;
            this.context = context;
            this.strafe = strafe;
        }
        protected override State GetTransition() => context.ReadState.Locomotion.Plan.Strafe == strafe ? null
            : strafe ? posture.Free : posture.Strafe;
        protected override void OnEnter() => root.ResetTurn();
        protected override void OnExit()
        {
            if (strafe) root.RestoreStrafeAngularLimit();
        }
        protected override void OnUpdate(float deltaTime) => root.Animate(strafe, deltaTime);
        protected override void OnFixedUpdate(float deltaTime) => root.Move(strafe, deltaTime);
    }
}
