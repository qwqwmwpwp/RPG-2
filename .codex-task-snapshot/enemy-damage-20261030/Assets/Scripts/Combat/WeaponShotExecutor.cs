using System;
using RPG4.Backpack.Equipment;
using RPG4.CharacterAttributes;
using UnityEngine;

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
        public static bool IsValidContext(Vector3 aimOrigin, Vector3 aimDirection,
            Vector3 bodyOrigin, Vector3 muzzleOrigin) => Finite(aimOrigin) && Finite(aimDirection)
            && Finite(bodyOrigin) && Finite(muzzleOrigin) && Finite(aimDirection.sqrMagnitude)
            && aimDirection.sqrMagnitude > 0.000001f;

        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public WeaponHitResult[] Execute(WeaponShotRequest request)
        {
            if (!IsValidContext(request.AimOrigin, request.AimDirection, request.BodyOrigin, request.MuzzleOrigin)
                || !Finite(request.Shot.Damage) || request.Shot.Damage < 0f
                || !Finite(request.Shot.Range) || request.Shot.Range <= 0f
                || !Finite(request.Shot.SpreadDegrees) || request.Shot.SpreadDegrees < 0f
                || request.Shot.SpreadDegrees > 90f || request.Shot.PelletCount < 1 || request.Shot.PelletCount > 32)
                throw new ArgumentException("射击请求含无效的方向或本发参数。", nameof(request));
            var results = new WeaponHitResult[request.Shot.PelletCount];
            for (int i = 0; i < results.Length; i++) results[i] = ExecutePellet(request, i);
            return results;
        }

        private static WeaponHitResult ExecutePellet(WeaponShotRequest request, int pellet)
        {
            Vector3 aimDirection = Spread(request.AimDirection, request.Shot.SpreadDegrees,
                unchecked((int)request.Shot.Sequence ^ request.Shot.WeaponId.GetHashCode() ^ pellet * 397));
            Vector3 target = request.AimOrigin + aimDirection * request.Shot.Range;
            if (Closest(request.AimOrigin, aimDirection, request.Shot.Range, request.Shooter, out RaycastHit aimHit))
                target = aimHit.point;
            // 贴墙时镜头目标可能在枪口后面；禁止生成反向射线。
            if (Vector3.Dot(target - request.MuzzleOrigin, aimDirection) <= 0.001f)
                target = request.MuzzleOrigin + aimDirection * request.Shot.Range;

            Vector3 barrel = request.MuzzleOrigin - request.BodyOrigin;
            if (barrel.sqrMagnitude > 0.000001f && Closest(request.BodyOrigin, barrel.normalized,
                barrel.magnitude, request.Shooter, out RaycastHit barrelHit))
                return Resolve(request, barrelHit, true);

            Vector3 trajectory = target - request.MuzzleOrigin;
            float distance = Mathf.Min(request.Shot.Range, trajectory.magnitude);
            if (distance > 0.0001f && Closest(request.MuzzleOrigin, trajectory.normalized,
                distance + 0.001f, request.Shooter, out RaycastHit hit))
                return Resolve(request, hit, false);
            return new WeaponHitResult(request, null,
                request.MuzzleOrigin + trajectory.normalized * distance, Vector3.zero, false, false);
        }

        private static WeaponHitResult Resolve(WeaponShotRequest request, RaycastHit hit, bool obstructed)
        {
            IDamageable target = hit.collider.GetComponentInParent<IDamageable>();
            bool applied = target != null && target.ApplyDamage(new DamageInfo(request.Shot.Damage));
            return new WeaponHitResult(request, hit.collider, hit.point, hit.normal, applied, obstructed);
        }

        private static bool Closest(Vector3 origin, Vector3 direction, float distance,
            GameObject shooter, out RaycastHit closest)
        {
            closest = default;
            float nearest = float.PositiveInfinity;
            foreach (RaycastHit hit in Physics.RaycastAll(origin, direction, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (shooter != null && (hit.collider.transform == shooter.transform
                    || hit.collider.transform.IsChildOf(shooter.transform))) continue;
                if (hit.distance >= nearest) continue;
                nearest = hit.distance;
                closest = hit;
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
