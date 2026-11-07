using RPG4.PlayerIntent;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Unity 世界执行端；不驱动 Animator，状态机只提交移动/姿态计划。</summary>
    internal sealed class PlayerWorldAdapter : IPlayerMovementWorld
    {
        private const float NetworkContactSkin = 0.002f;
        private readonly PlayerContentData content;
        private readonly PlayerStateData data;
        private readonly float standingHeight;
        private readonly Vector3 standingCenter;
        private readonly Collider[] standHits = new Collider[32];
        private float turnVelocity;
        private readonly RaycastHit[] moveHits = new RaycastHit[32];
        private float verticalVelocity;
        private bool networkMotor;
        private bool hasNetworkPlan, lastNetworkStrafe, lastNetworkCrouched;
        private long networkQueryTick;
        private readonly Dictionary<Collider, SupportObservation> supportObservations = new Dictionary<Collider, SupportObservation>();
        private struct SupportObservation
        {
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
            internal long Tick;
            internal bool Stable;
        }
        private bool originalKinematic, originalGravity;
        private RigidbodyInterpolation originalInterpolation;
        internal bool NetworkMotor => networkMotor;
        internal MovementKinematicsSettings KinematicsSettings => new MovementKinematicsSettings
        {
            DirectionSmoothTime = data.Movement.DirectionSmoothTime,
            SpeedSmoothTime = data.Movement.SpeedSmoothTime,
            FreeTurnSmoothTime = data.Exploration.TurnSmoothTime,
            StrafeTurnSmoothTime = data.Aim.TurnSmoothTime, Gravity = Physics.gravity.y
        };
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
        internal Ray GetAimRay(float yaw, float pitch)
        {
            return new Ray(LogicAnchor(0.8f),
                Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);
        }
        internal void GetShotOrigins(Vector3 direction, out Vector3 bodyOrigin, out Vector3 muzzleOrigin)
        {
            // 逻辑锚点只来自物理主体与胶囊；动画、模型枪口和相机都不参与命中计算。
            bodyOrigin = LogicAnchor(0.65f);
            muzzleOrigin = bodyOrigin + direction.normalized * 0.5f;
        }
        private Vector3 LogicAnchor(float heightFraction)
        {
            CapsuleCollider capsule = content.Capsule;
            Rigidbody body = content.Body;
            Vector3 local = capsule.center + Vector3.up * (capsule.height * (heightFraction - 0.5f));
            return body.position + body.rotation * Vector3.Scale(local, capsule.transform.lossyScale);
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
                if (!Blocks(hit)) continue;
                return false;
            }
            return true;
        }
        internal void Move(LocomotionPlan plan, bool strafe, float deltaTime)
        {
            Rigidbody body = content.Body;
            if (networkMotor) { MoveNetwork(plan, strafe, deltaTime); return; }
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
        internal void ConfigureNetworkMotor(bool enabled)
        {
            if (networkMotor == enabled) return;
            Rigidbody body = content.Body;
            if (enabled)
            {
                originalKinematic = body.isKinematic;
                originalGravity = body.useGravity;
                originalInterpolation = body.interpolation;
                Stop();
                body.interpolation = RigidbodyInterpolation.None;
                body.isKinematic = true;
                body.useGravity = false;
                verticalVelocity = 0f;
            }
            else
            {
                body.isKinematic = originalKinematic;
                body.useGravity = originalGravity;
                body.interpolation = originalInterpolation;
            }
            networkMotor = enabled;
            hasNetworkPlan = false;
        }
        internal PlayerMotorState CaptureMotor() => new PlayerMotorState
        {
            Position = content.Body.position, Yaw = BodyYaw, Velocity = PlanarVelocity,
            VerticalVelocity = verticalVelocity, TurnVelocity = turnVelocity, Crouched = PhysicallyCrouched
        };
        internal void RestoreMotor(PlayerMotorState state)
        {
            hasNetworkPlan = false;
            movement = new Vector2(state.Velocity.x, state.Velocity.z);
            verticalVelocity = state.VerticalVelocity;
            turnVelocity = state.TurnVelocity;
            float height = state.Crouched ? Mathf.Clamp(data.Crouch.Height,
                content.Capsule.radius * 2f, standingHeight) : standingHeight;
            content.Capsule.height = height;
            content.Capsule.center = standingCenter + Vector3.up * ((height - standingHeight) * 0.5f);
            PhysicallyCrouched = state.Crouched;
            SetPose(state.Position, state.Yaw);
        }
        internal bool NeedsTurnReset(LocomotionPlan plan) => !hasNetworkPlan
            || lastNetworkStrafe != plan.Strafe || lastNetworkCrouched != plan.Crouched;
        internal void BeginNetworkQueryTick(long tick) => networkQueryTick = tick;
        private void RecordNetworkPlan(LocomotionPlan plan)
        {
            hasNetworkPlan = true;
            lastNetworkStrafe = plan.Strafe;
            lastNetworkCrouched = plan.Crouched;
        }
        internal void CommitMotor(PlayerMotorState state, LocomotionPlan plan)
        {
            RestoreMotor(state);
            RecordNetworkPlan(plan);
        }
        private void SetPose(Vector3 position, float yaw)
        {
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            content.Body.position = position;
            content.Body.rotation = rotation;
            content.Body.transform.SetPositionAndRotation(position, rotation);
        }
        private bool Blocks(Collider other)
            => other != null && other != content.Capsule && other.attachedRigidbody != content.Body
                && !other.transform.IsChildOf(content.Body.transform)
                && !Physics.GetIgnoreLayerCollision(content.Capsule.gameObject.layer, other.gameObject.layer)
                && !Physics.GetIgnoreCollision(content.Capsule, other);

        /// <summary>Virtual target-posture queries. No collider, rigidbody or transform is changed here.</summary>
        public MovementPathResult CheckPath(in PlayerMotorState from, in PlayerMotorState proposed)
        {
            MovementPathResult uncertain = new MovementPathResult(MovementPathKind.RequiresAuthority, from.Position.y, false);
            CapsuleCollider capsule = content.Capsule;
            Vector3 delta = proposed.Position - from.Position;
            // Bound queries even if this interface is called outside the numeric validator.
            if (!Finite(from.Position) || !Finite(proposed.Position) || !Finite(from.Yaw) || !Finite(proposed.Yaw)
                || !Finite(from.VerticalVelocity) || !Finite(proposed.VerticalVelocity)
                || delta.sqrMagnitude > 1f || Mathf.Abs(delta.y) > 0.1f
                || from.VerticalVelocity > 0.005f || proposed.VerticalVelocity > 0.005f
                || capsule.direction != 1 || capsule.transform != content.Body.transform
                || Mathf.Abs(standingCenter.x) > 0.00001f || Mathf.Abs(standingCenter.z) > 0.00001f
                || Vector3.Dot(capsule.transform.up, Vector3.up) < 0.99999f) return uncertain;
            Vector3 scale = capsule.transform.lossyScale;
            if (scale.x <= 0f || scale.y <= 0f || scale.z <= 0f) return uncertain;
            VirtualCapsule(from.Position, from.Yaw, proposed.Crouched, out Vector3 bottom,
                out Vector3 top, out float radius);
            if (!ClearVolume(from.Position, from.Yaw, proposed.Crouched))
                return new MovementPathResult(MovementPathKind.Blocked, from.Position.y, false);
            if (!FlatSupport(from.Position, from.Yaw, proposed.Crouched, out Collider support, out float startY)
                || Mathf.Abs(startY - from.Position.y) > 0.002f) return uncertain;
            Vector3 end = proposed.Position;
            // Query at the proved plane rather than trusting the reported vertical coordinate.
            end.y = startY;
            if (!FlatSupport(end, proposed.Yaw, proposed.Crouched, out Collider endSupport, out float endY)
                || support != endSupport || Mathf.Abs(endY - startY) > 0.002f) return uncertain;
            Vector3 middle = Vector3.Lerp(from.Position, end, 0.5f);
            if (!FlatSupport(middle, proposed.Yaw, proposed.Crouched, out Collider middleSupport, out float middleY)
                || support != middleSupport || Mathf.Abs(middleY - startY) > 0.002f) return uncertain;
            Vector3 travel = end - from.Position;
            float distance = travel.magnitude;
            if (distance > 0.000001f)
            {
                const float skin = NetworkContactSkin;
                float inset = Mathf.Min(skin, radius * 0.5f);
                int count = Physics.CapsuleCastNonAlloc(bottom, top, radius - inset,
                    travel / distance, moveHits, distance + skin, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                if (count == moveHits.Length) return uncertain;
                for (int i = 0; i < count; i++)
                    if (Blocks(moveHits[i].collider))
                        return new MovementPathResult(MovementPathKind.Blocked, startY, false);
            }
            // Yaw can rotate an offset capsule: independently check the projected target volume.
            if (!ClearVolume(end, proposed.Yaw, proposed.Crouched)) return uncertain;
            bool canStand = ClearVolume(from.Position, from.Yaw, false) && ClearVolume(end, proposed.Yaw, false);
            if (!proposed.Crouched && !canStand) return uncertain;
            return new MovementPathResult(MovementPathKind.ClearFlatGround, endY, canStand);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private void VirtualCapsule(Vector3 position, float yaw, bool crouched,
            out Vector3 bottom, out Vector3 top, out float radius)
        {
            CapsuleCollider capsule = content.Capsule;
            Vector3 scale = capsule.transform.lossyScale;
            float localHeight = crouched ? Mathf.Clamp(data.Crouch.Height, capsule.radius * 2f, standingHeight) : standingHeight;
            Vector3 localCenter = standingCenter + Vector3.up * ((localHeight - standingHeight) * 0.5f);
            radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float height = Mathf.Max(radius * 2f, localHeight * Mathf.Abs(scale.y));
            Vector3 center = position + Quaternion.Euler(0f, yaw, 0f) * Vector3.Scale(localCenter, scale);
            bottom = center - Vector3.up * (height * 0.5f - radius);
            top = center + Vector3.up * (height * 0.5f - radius);
        }
        private bool ClearVolume(Vector3 position, float yaw, bool crouched)
        {
            VirtualCapsule(position, yaw, crouched, out Vector3 bottom, out Vector3 top, out float radius);
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius,
                standHits, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            if (count == standHits.Length) return false;
            for (int i = 0; i < count; i++) if (Blocks(standHits[i])) return false;
            return true;
        }
        private bool FlatSupport(Vector3 position, float yaw, bool crouched, out Collider support, out float rootY)
        {
            VirtualCapsule(position, yaw, crouched, out Vector3 bottom, out _, out float radius);
            support = null;
            rootY = position.y;
            float bottomOffset = bottom.y - radius - position.y;
            float plane = 0f;
            // The footprint must remain on one static plane, including its leading/trailing edges.
            for (int sample = 0; sample < 5; sample++)
            {
                Vector3 offset = sample == 0 ? Vector3.zero : sample == 1 ? Vector3.right : sample == 2
                    ? Vector3.left : sample == 3 ? Vector3.forward : Vector3.back;
                Vector3 origin = new Vector3(bottom.x, bottom.y - radius + 0.05f, bottom.z) + offset * (radius * 0.8f);
                int count = Physics.RaycastNonAlloc(origin, Vector3.down, moveHits, 0.1f,
                    Physics.AllLayers, QueryTriggerInteraction.Ignore);
                if (count == moveHits.Length) return false;
                float nearest = float.PositiveInfinity;
                RaycastHit selected = default;
                for (int i = 0; i < count; i++)
                    if (Blocks(moveHits[i].collider) && moveHits[i].distance < nearest)
                    { selected = moveHits[i]; nearest = selected.distance; }
                if (float.IsPositiveInfinity(nearest) || selected.collider.attachedRigidbody != null
                    || selected.normal.y < 0.999f) return false;
                if (sample == 0)
                {
                    support = selected.collider;
                    plane = selected.point.y;
                    if (!StationarySupport(support)) return false;
                }
                else if (selected.collider != support || Mathf.Abs(selected.point.y - plane) > 0.002f) return false;
            }
            // Match the existing authority motor's contact clearance; it is part of its resting pose.
            rootY = plane - bottomOffset + NetworkContactSkin;
            return true;
        }
        private bool StationarySupport(Collider support)
        {
            // PhysX static colliders have no Rigidbody. Rendering/batching Static flags are unrelated.
            // Require a second fixed-step observation; repeated queries in one tick cannot establish stability.
            Transform pose = support.transform;
            var current = new SupportObservation
            {
                Position = pose.position, Rotation = pose.rotation, Scale = pose.lossyScale, Tick = networkQueryTick
            };
            if (supportObservations.TryGetValue(support, out SupportObservation previous))
            {
                bool unchanged = previous.Position.Equals(current.Position) && previous.Rotation.Equals(current.Rotation)
                    && previous.Scale.Equals(current.Scale);
                current.Stable = unchanged && (previous.Tick == networkQueryTick ? previous.Stable
                    : previous.Tick < networkQueryTick && networkQueryTick - previous.Tick <= 1);
            }
            else if (supportObservations.Count >= 32) supportObservations.Clear();
            supportObservations[support] = current;
            return current.Stable;
        }
        private void CapsuleAt(Vector3 position, out Vector3 bottom, out Vector3 top, out float radius)
        {
            CapsuleCollider capsule = content.Capsule;
            Vector3 scale = capsule.transform.lossyScale;
            radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float height = Mathf.Max(radius * 2f, capsule.height * Mathf.Abs(scale.y));
            Vector3 center = position + content.Body.rotation * Vector3.Scale(capsule.center, scale);
            bottom = center - Vector3.up * (height * 0.5f - radius);
            top = center + Vector3.up * (height * 0.5f - radius);
        }
        private void MoveNetwork(LocomotionPlan plan, bool strafe, float deltaTime)
        {
            if (deltaTime <= 0f) return;
            Physics.SyncTransforms();
            PlayerMotorState candidate = PlayerMotorKinematics.Step(CaptureMotor(), plan,
                KinematicsSettings, NeedsTurnReset(plan), deltaTime);
            RecordNetworkPlan(plan);
            movement = new Vector2(candidate.Velocity.x, candidate.Velocity.z);
            turnVelocity = candidate.TurnVelocity;
            verticalVelocity = candidate.VerticalVelocity;
            float yaw = candidate.Yaw;
            Vector3 position = content.Body.position;
            // Limited depenetration also recovers a server correction inside a moving obstacle.
            for (int iteration = 0; iteration < 4; iteration++)
            {
                CapsuleAt(position, out Vector3 bottom, out Vector3 top, out float radius);
                int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, standHits,
                    Physics.AllLayers, QueryTriggerInteraction.Ignore);
                bool moved = false;
                for (int i = 0; i < count; i++)
                {
                    Collider other = standHits[i];
                    if (!Blocks(other)) continue;
                    if (Physics.ComputePenetration(content.Capsule, position, content.Body.rotation,
                        other, other.transform.position, other.transform.rotation, out Vector3 normal, out float distance))
                    { position += normal * (distance + 0.001f); moved = true; }
                }
                if (!moved) break;
            }
            Vector3 remaining = new Vector3(movement.x, verticalVelocity, movement.y) * deltaTime;
            const float skin = NetworkContactSkin;
            for (int iteration = 0; iteration < 4 && remaining.sqrMagnitude > 0.0000001f; iteration++)
            {
                CapsuleAt(position, out Vector3 bottom, out Vector3 top, out float radius);
                float distance = remaining.magnitude;
                Vector3 directionStep = remaining / distance;
                // A full-size capsule touching the floor can start a cast overlapped. PhysX then
                // reports -direction as its normal, which would erase horizontal movement too.
                float inset = Mathf.Min(skin, radius * 0.5f);
                float queryRadius = radius - inset;
                int count = Physics.CapsuleCastNonAlloc(bottom, top, queryRadius, directionStep, moveHits,
                    distance + skin, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                float nearest = float.PositiveInfinity;
                Vector3 normal = Vector3.zero;
                for (int i = 0; i < count; i++)
                {
                    RaycastHit hit = moveHits[i];
                    float approach = -Vector3.Dot(directionStep, hit.normal);
                    if (!Blocks(hit.collider) || approach <= 0.0001f) continue;
                    // Restore the full capsule's contact distance, including at oblique walls.
                    float contactDistance = Mathf.Max(0f, hit.distance - inset / approach);
                    if (contactDistance < nearest) { nearest = contactDistance; normal = hit.normal; }
                }
                // A saturated query is conservative: do not tunnel through an omitted collider.
                if (count == moveHits.Length) break;
                if (float.IsPositiveInfinity(nearest)) { position += remaining; break; }
                float travelled = Mathf.Max(0f, nearest - skin);
                position += directionStep * travelled;
                remaining = Vector3.ProjectOnPlane(remaining - directionStep * travelled, normal);
                if (normal.y > 0.5f && verticalVelocity < 0f) verticalVelocity = 0f;
                else if (normal.y < -0.5f && verticalVelocity > 0f) verticalVelocity = 0f;
            }
            SetPose(position, yaw);
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
            verticalVelocity = 0f;
        }
    }
}
