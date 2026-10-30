using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Unity 世界执行端；不驱动 Animator，状态机只提交移动/姿态计划。</summary>
    internal sealed class PlayerWorldAdapter
    {
        private readonly PlayerContentData content;
        private readonly PlayerStateData data;
        private readonly float standingHeight;
        private readonly Vector3 standingCenter;
        private readonly Collider[] standHits = new Collider[32];
        private float turnVelocity;
        private Rigidbody strafeBody;
        private float originalMaxAngularVelocity;
        private Vector2 movement;
        internal bool PhysicallyCrouched { get; private set; }
        internal Vector3 PlanarVelocity => new Vector3(movement.x, 0f, movement.y);
        internal float BodyYaw => content.Body != null ? content.Body.rotation.eulerAngles.y : 0f;
        internal PlayerWorldAdapter(PlayerStateData data)
        {
            this.data = data;
            content = data.Content;
            standingHeight = content.Capsule.height;
            standingCenter = content.Capsule.center;
        }
        internal void ResetTurn() => turnVelocity = 0f;
        internal static Ray GetAimRay(Transform owner, Camera configuredCamera, float yaw, float pitch, bool useSceneCamera)
        {
            Camera camera = null;
            if (useSceneCamera) camera = configuredCamera != null ? configuredCamera : Camera.main;
            if (camera != null && camera.isActiveAndEnabled)
                return camera.ViewportPointToRay(new Vector3(0.5f, 0.5f));
            return new Ray(owner.position + Vector3.up * 1.5f,
                Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);
        }
        internal bool ApplyPosture(bool wantsCrouch)
        {
            // 再次检测，避免 Update 到 FixedUpdate 间出现障碍；失败时保持当前胶囊。
            if (!wantsCrouch && PhysicallyCrouched && !CanStand()) return false;
            CapsuleCollider capsule = content.Capsule;
            float height = wantsCrouch ? Mathf.Clamp(data.Crouch.Height,
                capsule.radius * 2f, standingHeight) : standingHeight;
            capsule.height = height;
            capsule.center = standingCenter + Vector3.up * ((height - standingHeight) * 0.5f);
            PhysicallyCrouched = wantsCrouch;
            return true;
        }
        internal bool CanStand()
        {
            CapsuleCollider capsule = content.Capsule;
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
                if (hit == capsule || hit.attachedRigidbody == content.Body
                    || hit.transform.IsChildOf(transform)) continue;
                return false;
            }
            return true;
        }
        internal void Move(LocomotionPlan plan, bool strafe, float deltaTime)
        {
            Rigidbody body = content.Body;
            if (body == null || body.isKinematic)
            {
                movement = Vector2.zero;
                RestoreStrafeAngularLimit();
                return;
            }
            if (!plan.CanMove)
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
            movement = Vector2.ClampMagnitude(PlayerMotionMath.SmoothMove(movement, target,
                data.Movement.DirectionSmoothTime, data.Movement.SpeedSmoothTime, deltaTime), plan.MoveSpeed);
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
                float yaw = data.Aim.TurnSmoothTime <= 0f ? plan.ViewYaw
                    : Mathf.SmoothDampAngle(bodyYaw, plan.ViewYaw, ref turnVelocity,
                        data.Aim.TurnSmoothTime, Mathf.Infinity, deltaTime);
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
            float freeYaw = data.Exploration.TurnSmoothTime <= 0f ? targetYaw
                : Mathf.SmoothDampAngle(body.rotation.eulerAngles.y, targetYaw,
                    ref turnVelocity, data.Exploration.TurnSmoothTime, Mathf.Infinity, deltaTime);
            body.MoveRotation(Quaternion.Euler(0f, freeYaw, 0f));
        }
        private void SetPlanarVelocity(Vector3 velocity)
        {
            Rigidbody body = content.Body;
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
        internal void Stop()
        {
            ClearPlanarVelocity();
            RestoreStrafeAngularLimit();
            ResetTurn();
            movement = Vector2.zero;
        }
    }
}
