using System;
using System.Collections.Generic;
using RPG4.Backpack.Equipment;
using RPG4.Backpack.Items;
using RPG4.CharacterAttributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG4.Combat
{
    /// <summary>本发已扣弹后的固定射击请求；模型效果不能回写这些参数。</summary>
    public readonly struct WeaponShotRequest
    {
        public WeaponShotRequest(WeaponShot shot, GameObject shooter, Vector3 aimOrigin,
            Vector3 aimDirection, Vector3 bodyOrigin, Vector3 muzzleOrigin)
        {
            Shot = shot;
            Shooter = shooter;
            AimOrigin = aimOrigin;
            AimDirection = aimDirection.normalized;
            BodyOrigin = bodyOrigin;
            MuzzleOrigin = muzzleOrigin;
        }

        public WeaponShot Shot { get; }
        public GameObject Shooter { get; }
        public Vector3 AimOrigin { get; }
        public Vector3 AimDirection { get; }
        public Vector3 BodyOrigin { get; }
        public Vector3 MuzzleOrigin { get; }
    }

    public readonly struct WeaponHitResult
    {
        public WeaponHitResult(WeaponShotRequest request, Collider collider, Vector3 point,
            Vector3 normal, bool damageApplied, bool muzzleObstructed)
        {
            Request = request;
            Collider = collider;
            Point = point;
            Normal = normal;
            DamageApplied = damageApplied;
            MuzzleObstructed = muzzleObstructed;
        }

        public WeaponShotRequest Request { get; }
        public Collider Collider { get; }
        public Vector3 Point { get; }
        public Vector3 Normal { get; }
        public bool HasHit => Collider != null;
        public bool DamageApplied { get; }
        public bool MuzzleObstructed { get; }
    }

    /// <summary>第三人称即时命中：镜头选择目标，枪身与枪口遮挡优先于目标。</summary>
    public sealed class WeaponShotExecutor
    {
        // 服务器生命周期内保留每个武器的水位；旧请求即使不在最近结果窗口内也不能重新伤害。
        private readonly Dictionary<(int Shooter, ItemId Weapon), ulong> executed =
            new Dictionary<(int Shooter, ItemId Weapon), ulong>();
        private RaycastHit[] raycastHits = new RaycastHit[64];
        public void ForgetShooter(GameObject shooter)
        {
            if (shooter == null) return;
            int id = shooter.GetInstanceID();
            var remove = new List<(int Shooter, ItemId Weapon)>();
            foreach (var key in executed.Keys) if (key.Shooter == id) remove.Add(key);
            foreach (var key in remove) executed.Remove(key);
        }
        public void Clear() => executed.Clear();

        private readonly struct TraceHit
        {
            internal TraceHit(Collider collider, Vector3 point, Vector3 normal, float distance)
            { Collider = collider; Point = point; Normal = normal; Distance = distance; }
            internal Collider Collider { get; }
            internal Vector3 Point { get; }
            internal Vector3 Normal { get; }
            internal float Distance { get; }
        }
        public static bool IsValidContext(Vector3 aimOrigin, Vector3 aimDirection,
            Vector3 bodyOrigin, Vector3 muzzleOrigin) => Finite(aimOrigin) && Finite(aimDirection)
            && Finite(bodyOrigin) && Finite(muzzleOrigin) && Finite(aimDirection.sqrMagnitude)
            && aimDirection.sqrMagnitude > 0.000001f;

        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public WeaponHitResult[] Execute(WeaponShotRequest request, ServerCombatHistory history = null,
            double rewindTime = double.NaN)
        {
            if (!IsValidContext(request.AimOrigin, request.AimDirection, request.BodyOrigin, request.MuzzleOrigin)
                || !Finite(request.Shot.Damage) || request.Shot.Damage < 0f
                || !Finite(request.Shot.Range) || request.Shot.Range <= 0f
                || !Finite(request.Shot.SpreadDegrees) || request.Shot.SpreadDegrees < 0f
                || request.Shot.SpreadDegrees > 90f || request.Shot.PelletCount < 1 || request.Shot.PelletCount > 32)
                throw new ArgumentException("射击请求含无效的方向或本发参数。", nameof(request));
            var key = (request.Shooter != null ? request.Shooter.GetInstanceID() : 0, request.Shot.WeaponId);
            if (request.Shot.Sequence == 0 || executed.TryGetValue(key, out ulong sequence) && request.Shot.Sequence <= sequence)
                return Array.Empty<WeaponHitResult>();
            executed[key] = request.Shot.Sequence; // 先登记，阻止伤害回调同步重入重放本发。
            Scene scene = request.Shooter != null ? request.Shooter.scene : SceneManager.GetActiveScene();
            ServerCombatHistory.Query query = history != null ? history.Sample(rewindTime, scene.handle) : default;
            PhysicsScene physicsScene = scene.GetPhysicsScene();
            var results = new WeaponHitResult[request.Shot.PelletCount];
            for (int i = 0; i < results.Length; i++) results[i] = ExecutePellet(request, i, query, scene, physicsScene);
            return results;
        }

        private WeaponHitResult ExecutePellet(WeaponShotRequest request, int pellet,
            ServerCombatHistory.Query history, Scene scene, PhysicsScene physicsScene)
        {
            Vector3 aimDirection = Spread(request.AimDirection, request.Shot.SpreadDegrees,
                unchecked((int)request.Shot.Sequence ^ request.Shot.WeaponId.GetHashCode() ^ pellet * 397));
            Vector3 target = request.AimOrigin + aimDirection * request.Shot.Range;
            if (Closest(request.AimOrigin, aimDirection, request.Shot.Range, request.Shooter, history, scene, physicsScene, out TraceHit aimHit))
                target = aimHit.Point;
            // 贴墙时镜头目标可能在枪口后面；禁止生成反向射线。
            if (Vector3.Dot(target - request.MuzzleOrigin, aimDirection) <= 0.001f)
                target = request.MuzzleOrigin + aimDirection * request.Shot.Range;

            Vector3 barrel = request.MuzzleOrigin - request.BodyOrigin;
            if (barrel.sqrMagnitude > 0.000001f && Closest(request.BodyOrigin, barrel.normalized,
                barrel.magnitude, request.Shooter, history, scene, physicsScene, out TraceHit barrelHit))
                return Resolve(request, barrelHit, true);

            Vector3 trajectory = target - request.MuzzleOrigin;
            float distance = Mathf.Min(request.Shot.Range, trajectory.magnitude);
            if (distance > 0.0001f && Closest(request.MuzzleOrigin, trajectory.normalized,
                distance + 0.001f, request.Shooter, history, scene, physicsScene, out TraceHit hit))
                return Resolve(request, hit, false);
            return new WeaponHitResult(request, null,
                request.MuzzleOrigin + trajectory.normalized * distance, Vector3.zero, false, false);
        }

        private static WeaponHitResult Resolve(WeaponShotRequest request, TraceHit hit, bool obstructed)
        {
            IDamageable target = hit.Collider.GetComponentInParent<IDamageable>();
            bool applied = target != null && target.ApplyDamage(new DamageInfo(request.Shot.Damage,
                request.Shooter, hit.Point, (hit.Point - request.MuzzleOrigin).normalized));
            return new WeaponHitResult(request, hit.Collider, hit.Point, hit.Normal, applied, obstructed);
        }

        private bool Closest(Vector3 origin, Vector3 direction, float distance,
            GameObject shooter, ServerCombatHistory.Query history, Scene scene, PhysicsScene physicsScene, out TraceHit closest)
        {
            closest = default;
            float nearest = float.PositiveInfinity;
            // PhysicsScene.Raycast 的数组重载适用于默认和 local physics 场景。
            // 饱和时扩容再查，避免未排序的截断结果遗漏更近的墙。
            int count;
            while ((count = physicsScene.Raycast(origin, direction, raycastHits, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) == raycastHits.Length)
                Array.Resize(ref raycastHits, raycastHits.Length * 2);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = raycastHits[i];
                if (hit.collider == null || hit.collider.gameObject.scene != scene) continue;
                if (shooter != null && (hit.collider.transform == shooter.transform
                    || hit.collider.transform.IsChildOf(shooter.transform))) continue;
                if (history.IsHistoricalTarget(hit.collider)) continue;
                if (hit.distance >= nearest) continue;
                nearest = hit.distance;
                closest = new TraceHit(hit.collider, hit.point, hit.normal, hit.distance);
            }
            if (history.Closest(origin, direction, Mathf.Min(distance, nearest), shooter,
                out Collider collider, out Vector3 point, out Vector3 normal, out float historicalDistance))
            {
                nearest = historicalDistance;
                closest = new TraceHit(collider, point, normal, historicalDistance);
            }
            return nearest < float.PositiveInfinity;
        }

        private static Vector3 Spread(Vector3 direction, float degrees, int seed)
        {
            if (direction.sqrMagnitude < 0.000001f) direction = Vector3.forward;
            if (degrees <= 0f) return direction;
            var random = new System.Random(seed);
            float cosine = Mathf.Lerp(1f, Mathf.Cos(degrees * Mathf.Deg2Rad), (float)random.NextDouble());
            float sine = Mathf.Sqrt(Mathf.Max(0f, 1f - cosine * cosine));
            float azimuth = (float)random.NextDouble() * Mathf.PI * 2f;
            Vector3 local = new Vector3(Mathf.Cos(azimuth) * sine, Mathf.Sin(azimuth) * sine, cosine);
            return Quaternion.LookRotation(direction) * local;
        }
    }
}
