using UnityEngine;

namespace RPG4.PlayerMovement
{
    internal static class PlayerMotionMath
    {
        internal static Vector2 StepVelocity(Vector2 current, Vector2 target,
            in MovementKinematicsSettings settings, float deltaTime)
        {
            if (deltaTime <= 0f) return current;
            bool braking = target.sqrMagnitude < current.sqrMagnitude
                || Vector2.Dot(current, target) < 0f;
            float rate = braking ? settings.Deceleration : settings.Acceleration;
            // 未配置新速率时保留旧平滑语义；新算法通过速度向量趋近完成急转减速。
            return rate <= 0f ? SmoothMove(current, target,
                settings.DirectionSmoothTime, settings.SpeedSmoothTime, deltaTime)
                : Vector2.MoveTowards(current, target, rate * deltaTime);
        }

        internal static Vector2 SmoothMove(Vector2 current, Vector2 target,
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
    }
}
