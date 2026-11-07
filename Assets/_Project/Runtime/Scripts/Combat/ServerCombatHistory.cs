using System;
using System.Collections.Generic;
using RPG4.CharacterAttributes;
using UnityEngine;

namespace RPG4.Combat
{
    /// <summary>
    /// 服务器有界胶囊历史。只保存几何，不移动真实 Collider；门、墙与未注册目标始终使用当前碰撞。
    /// 调用者必须用服务器认可的输入 tick 推导采样时间，不能直接传客户端声明的时间。
    /// </summary>
    public sealed class ServerCombatHistory
    {
        public const double MaximumRewindSeconds = 0.25d;
        public const int MaximumFrames = 32;
        public const int MaximumTargets = 128;
        private readonly Frame[] frames = new Frame[MaximumFrames];
        private int frameCount, nextFrame;
        private readonly HashSet<Transform> registeredRoots = new HashSet<Transform>();
        private bool warnedOverflow;

        internal sealed class Frame
        {
            internal double Time;
            internal int Count;
            internal readonly Capsule[] Targets = new Capsule[MaximumTargets];
        }

        public ServerCombatHistory()
        {
            for (int i = 0; i < frames.Length; i++) frames[i] = new Frame();
        }

        private Frame Newest => frameCount == 0 ? null : frames[(nextFrame + MaximumFrames - 1) % MaximumFrames];
        private Frame FrameAt(int index) => frames[(nextFrame - frameCount + MaximumFrames + index) % MaximumFrames];
        private static void ClearFrame(Frame frame) { Array.Clear(frame.Targets, 0, frame.Count); frame.Count = 0; }

        internal readonly struct Capsule
        {
            internal Capsule(Collider collider, Transform root, Vector3 a, Vector3 b, float radius)
            { Collider = collider; Root = root; A = a; B = b; Radius = radius; SceneHandle = collider.gameObject.scene.handle; }
            internal readonly Collider Collider;
            internal readonly Transform Root;
            internal readonly Vector3 A, B;
            internal readonly float Radius;
            internal readonly int SceneHandle;
        }

        internal readonly struct Query
        {
            private readonly ServerCombatHistory owner;
            private readonly Frame frame;
            private readonly int sceneHandle;
            internal Query(ServerCombatHistory owner, Frame frame, int sceneHandle)
            { this.owner = owner; this.frame = frame; this.sceneHandle = sceneHandle; }
            internal bool IsHistoricalTarget(Collider collider) => owner != null && owner.IsHistoricalTarget(collider, frame, sceneHandle);
            internal bool Closest(Vector3 origin, Vector3 direction, float distance, GameObject shooter,
                out Collider collider, out Vector3 point, out Vector3 normal, out float hitDistance)
            {
                collider = null; point = normal = default; hitDistance = distance;
                return owner != null && owner.Closest(origin, direction, distance, shooter, frame, sceneHandle,
                    out collider, out point, out normal, out hitDistance);
            }
        }

        internal Query Sample(double requestedTime, int sceneHandle) => new Query(this, SelectFrame(requestedTime), sceneHandle);

        public void Capture(double serverTime, IReadOnlyList<Collider> authoritativeTargets)
        {
            if (double.IsNaN(serverTime) || double.IsInfinity(serverTime) || serverTime < 0d)
                throw new ArgumentOutOfRangeException(nameof(serverTime));
            if (Newest != null && serverTime <= Newest.Time) return;
            Frame frame = frames[nextFrame];
            ClearFrame(frame);
            frame.Time = serverTime;
            registeredRoots.Clear();
            if (authoritativeTargets != null)
            {
                for (int i = 0; i < authoritativeTargets.Count; i++)
                {
                    Collider collider = authoritativeTargets[i];
                    if (collider == null || !collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy
                        || (Physics.DefaultRaycastLayers & (1 << collider.gameObject.layer)) == 0) continue;
                    if (frame.Count >= MaximumTargets)
                    {
                        if (!warnedOverflow)
                        {
                            Debug.LogWarning("服务器战斗历史目标超过128个；溢出目标继续使用当前碰撞，不参与回溯。");
                            warnedOverflow = true;
                        }
                        break;
                    }
                    if (!TryCapture(collider, out Capsule target)) continue;
                    frame.Targets[frame.Count++] = target;
                    registeredRoots.Add(target.Root);
                }
            }
            nextFrame = (nextFrame + 1) % MaximumFrames;
            frameCount = Math.Min(frameCount + 1, MaximumFrames);
            while (frameCount > 1 && serverTime - FrameAt(0).Time > MaximumRewindSeconds)
            {
                ClearFrame(FrameAt(0));
                frameCount--;
            }
        }

        /// <summary>断线/销毁时立即删除全部历史引用。</summary>
        public void Remove(GameObject target)
        {
            if (target == null) return;
            Transform root = target.transform;
            for (int f = 0; f < frameCount; f++)
            {
                Frame frame = FrameAt(f);
                int count = 0, previous = frame.Count;
                for (int i = 0; i < previous; i++)
                {
                    Capsule entry = frame.Targets[i];
                    if (entry.Collider == null || entry.Root == null || entry.Root == root || entry.Root.IsChildOf(root)) continue;
                    frame.Targets[count++] = entry;
                }
                Array.Clear(frame.Targets, count, previous - count);
                frame.Count = count;
            }
            registeredRoots.RemoveWhere(entry => entry == null || entry == root || entry.IsChildOf(root));
        }

        public void Clear()
        {
            foreach (Frame frame in frames) ClearFrame(frame);
            frameCount = nextFrame = 0; registeredRoots.Clear(); warnedOverflow = false;
        }

        private bool IsHistoricalTarget(Collider collider, Frame selected, int sceneHandle)
        {
            if (selected == null) return false;
            for (int i = 0; i < selected.Count; i++)
            {
                Capsule target = selected.Targets[i];
                if (target.SceneHandle != sceneHandle || target.Collider == null
                    || target.Collider.gameObject.scene.handle != target.SceneHandle) continue;
                Transform root = target.Root;
                if (target.Collider != null && root != null && root.gameObject.activeInHierarchy && registeredRoots.Contains(root)
                    && (collider.transform == root || collider.transform.IsChildOf(root))) return true;
            }
            return false;
        }

        private Frame SelectFrame(double requestedTime)
        {
            if (frameCount == 0) return null;
            double newest = Newest.Time;
            double time = double.IsNaN(requestedTime) || double.IsInfinity(requestedTime) ? newest
                : Math.Max(newest - MaximumRewindSeconds, Math.Min(newest, requestedTime));
            Frame selected = FrameAt(0);
            for (int i = 1; i < frameCount; i++)
            {
                Frame frame = FrameAt(i);
                if (Math.Abs(frame.Time - time) < Math.Abs(selected.Time - time)) selected = frame;
            }
            return selected;
        }

        private bool Closest(Vector3 origin, Vector3 direction, float distance, GameObject shooter,
            Frame selected, int sceneHandle, out Collider collider, out Vector3 point, out Vector3 normal, out float hitDistance)
        {
            collider = null; point = normal = default; hitDistance = distance;
            if (selected == null) return false;
            for (int i = 0; i < selected.Count; i++)
            {
                Capsule target = selected.Targets[i];
                if (target.SceneHandle != sceneHandle || target.Collider == null
                    || target.Collider.gameObject.scene.handle != target.SceneHandle) continue;
                if (target.Collider == null || target.Root == null || !target.Root.gameObject.activeInHierarchy
                    || !registeredRoots.Contains(target.Root)
                    || shooter != null && (target.Root == shooter.transform || target.Root.IsChildOf(shooter.transform))) continue;
                if (!RayCapsule(origin, direction, target.A, target.B, target.Radius, out float t) || t > hitDistance) continue;
                hitDistance = t; collider = target.Collider; point = origin + direction * t;
                Vector3 segment = target.B - target.A;
                float along = segment.sqrMagnitude > 0.000001f
                    ? Mathf.Clamp01(Vector3.Dot(point - target.A, segment) / segment.sqrMagnitude) : 0f;
                normal = (point - target.A - segment * along).normalized;
            }
            return collider != null;
        }

        private static bool TryCapture(Collider collider, out Capsule result)
        {
            Vector3 center, axis;
            float radius, height;
            Vector3 scale = collider.transform.lossyScale;
            scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            if (collider is CapsuleCollider capsule)
            {
                Vector3 localAxis = capsule.direction == 0 ? Vector3.right : capsule.direction == 2 ? Vector3.forward : Vector3.up;
                axis = collider.transform.TransformDirection(localAxis).normalized;
                float axialScale = capsule.direction == 0 ? scale.x : capsule.direction == 2 ? scale.z : scale.y;
                float radialScale = capsule.direction == 0 ? Mathf.Max(scale.y, scale.z)
                    : capsule.direction == 2 ? Mathf.Max(scale.x, scale.y) : Mathf.Max(scale.x, scale.z);
                center = collider.transform.TransformPoint(capsule.center);
                radius = capsule.radius * radialScale; height = capsule.height * axialScale;
            }
            else if (collider is CharacterController controller)
            {
                axis = collider.transform.up;
                center = collider.transform.TransformPoint(controller.center);
                radius = controller.radius * Mathf.Max(scale.x, scale.z); height = controller.height * scale.y;
            }
            else { result = default; return false; }
            IDamageable damageable = collider.GetComponentInParent<IDamageable>();
            Transform root = damageable is Component component ? component.transform : collider.transform;
            float halfSegment = Mathf.Max(0f, height * 0.5f - radius);
            result = new Capsule(collider, root, center - axis * halfSegment, center + axis * halfSegment, radius);
            return radius > 0f;
        }

        private static bool RayCapsule(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, float radius, out float distance)
        {
            Vector3 segment = b - a, offset = origin - a;
            float lengthSquared = segment.sqrMagnitude;
            float along = lengthSquared > 0.000001f ? Mathf.Clamp01(Vector3.Dot(offset, segment) / lengthSquared) : 0f;
            if ((offset - segment * along).sqrMagnitude <= radius * radius) { distance = 0f; return true; }
            distance = float.PositiveInfinity;
            // 有限圆柱侧面与两端球的并集；只取非负的最近交点。
            if (lengthSquared > 0.000001f)
            {
                float segmentDirection = Vector3.Dot(segment, direction), segmentOffset = Vector3.Dot(segment, offset);
                float qa = lengthSquared - segmentDirection * segmentDirection;
                float qb = lengthSquared * Vector3.Dot(offset, direction) - segmentOffset * segmentDirection;
                float qc = lengthSquared * (offset.sqrMagnitude - radius * radius) - segmentOffset * segmentOffset;
                float discriminant = qb * qb - qa * qc;
                if (Mathf.Abs(qa) > 0.000001f && discriminant >= 0f)
                {
                    float t = (-qb - Mathf.Sqrt(discriminant)) / qa;
                    float y = segmentOffset + t * segmentDirection;
                    if (t >= 0f && y >= 0f && y <= lengthSquared) distance = t;
                }
            }
            SphereDistance(origin - a, direction, radius, ref distance);
            SphereDistance(origin - b, direction, radius, ref distance);
            return !float.IsPositiveInfinity(distance);
        }

        private static void SphereDistance(Vector3 offset, Vector3 direction, float radius, ref float distance)
        {
            float projection = Vector3.Dot(offset, direction);
            float discriminant = projection * projection - (offset.sqrMagnitude - radius * radius);
            if (discriminant < 0f) return;
            float t = -projection - Mathf.Sqrt(discriminant);
            if (t >= 0f) distance = Mathf.Min(distance, t);
        }
    }
}
