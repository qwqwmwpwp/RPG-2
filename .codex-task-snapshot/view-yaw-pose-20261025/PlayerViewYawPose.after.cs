using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Strafe 时仅对可见骨架补偿偏航，让显示朝向使用本帧观察角。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Player))]
    [DefaultExecutionOrder(-50)]
    public sealed class PlayerViewYawPose : MonoBehaviour
    {
        [SerializeField] private Player player;
        [SerializeField] private Transform skeletonRoot;

        private Transform posedRoot;
        private Quaternion animationLocalRotation;

        private void Update()
        {
            // 在 Animator 求值前撤销上一帧补偿，避免动画或 IK 重复叠加。
            RestorePose();
        }

        private void LateUpdate()
        {
            RestorePose();
            if (player == null || player.transform != transform || skeletonRoot == null
                || skeletonRoot == transform || !skeletonRoot.IsChildOf(transform)
                || skeletonRoot.GetComponent<Rigidbody>() != null
                || !player.TryGetStrafeViewYaw(out float viewYaw)) return;

            // 保留动画与 AimPose 的姿态，只绕世界 Y 叠加物理显示角到观察角的差。
            animationLocalRotation = skeletonRoot.localRotation;
            posedRoot = skeletonRoot;
            float yawOffset = Mathf.DeltaAngle(transform.eulerAngles.y, viewYaw);
            skeletonRoot.rotation = Quaternion.AngleAxis(yawOffset, Vector3.up) * skeletonRoot.rotation;
        }

        private void RestorePose()
        {
            if (posedRoot != null) posedRoot.localRotation = animationLocalRotation;
            posedRoot = null;
        }

        private void OnDisable() => RestorePose();
        private void OnApplicationFocus(bool focused) { if (!focused) RestorePose(); }
        private void OnApplicationPause(bool paused) { if (paused) RestorePose(); }
    }
}
