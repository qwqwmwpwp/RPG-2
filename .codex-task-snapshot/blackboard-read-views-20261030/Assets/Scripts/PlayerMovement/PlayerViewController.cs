using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色子节点上的独立视角；可见骨架朝向由 BodyAimRig 在动画流中同步。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)] // 输入（-200）之后采样，LateUpdate 先于相机与 Brain。
    public sealed class PlayerViewController : MonoBehaviour
    {
        [SerializeField] private Transform skeletonRoot;
        [Tooltip("鼠标位移到观察角度的倍率，不乘 deltaTime。")]
        [SerializeField] private Vector2 lookSensitivity = new Vector2(0.15f, 0.12f);
        [Tooltip("俯仰角范围；负数向上看，正数向下看。")]
        [SerializeField] private Vector2 pitchRange = new Vector2(-35f, 65f);
        [SerializeField, Min(0f)] private float aimSensitivityMultiplier = 0.65f;

        private Player player;
        private PlayerBlackboard blackboard;
        private PlayerAimPose aimPose;
        private float viewYaw;
        private float viewPitch;

        /// <summary>移动与瞄准共用的逻辑观察角，不受相机避障、构图或刚体插值影响。</summary>
        public float ViewYaw => viewYaw;
        internal PlayerBlackboard Blackboard => blackboard;
        internal PlayerIntentManager IntentManager => player != null ? player.Intents : null;

        private void Awake()
        {
            if (transform.parent == null)
            {
                Debug.LogError("PlayerViewController 必须配置在角色的子节点上。", this);
                enabled = false;
                return;
            }
            Vector3 angles = transform.rotation.eulerAngles;
            viewYaw = Mathf.DeltaAngle(0f, angles.y);
            viewPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, angles.x), pitchRange.x, pitchRange.y);
            IntentManager?.RecordView(viewYaw, viewPitch);
            UpdatePivotRotation();
        }

        internal void Initialize(Player owner, PlayerAimPose pose)
        {
            player = owner;
            blackboard = owner.Blackboard;
            aimPose = pose;
            if (transform.parent != owner.transform || skeletonRoot == null
                || skeletonRoot == owner.transform || !skeletonRoot.IsChildOf(owner.transform)
                || transform.IsChildOf(skeletonRoot) || skeletonRoot.GetComponent<Rigidbody>() != null)
            {
                Debug.LogError("PlayerViewController 需要角色直属子节点和独立的可见骨架引用。", this);
                enabled = false;
                return;
            }
            if (aimPose != null) aimPose.Initialize(owner.transform, blackboard, owner.Intents);
            owner.Intents.RecordView(viewYaw, viewPitch);
        }

        private void Update()
        {
            player?.Intents?.RefreshPermissions();
            if (blackboard == null || !blackboard.CanLook) return;
            player.Intents.ProcessLook(lookSensitivity, pitchRange, aimSensitivityMultiplier);
            viewYaw = blackboard.ViewYaw;
            viewPitch = blackboard.ViewPitch;
            UpdatePivotRotation();
        }

        private void LateUpdate()
        {
            player?.Intents?.RefreshPermissions();
            // 角色转身和插值完成后，抵消继承的旋转，保持独立的世界观察方向。
            UpdatePivotRotation();
        }

        internal void ApplyRecoil(float pitch, float yaw)
        {
            if (player == null || !player.IsAlive || blackboard == null || !blackboard.CanLook) return;
            viewPitch = Mathf.Clamp(viewPitch - pitch, pitchRange.x, pitchRange.y);
            viewYaw = Mathf.DeltaAngle(0f, viewYaw + yaw);
            player.Intents.RecordView(viewYaw, viewPitch);
            UpdatePivotRotation();
        }

        private void UpdatePivotRotation()
        {
            transform.localRotation = Quaternion.Inverse(transform.parent.rotation)
                * Quaternion.Euler(viewPitch, viewYaw, 0f);
        }

        private void ResetViewPose()
        {
            if (aimPose != null) aimPose.ResetPose();
        }

        private void OnDisable()
        {
            ResetViewPose();
            player?.Intents?.RefreshPermissions();
        }
        private void OnApplicationFocus(bool focused) { if (!focused) ResetViewPose(); }
        private void OnApplicationPause(bool paused) { if (paused) ResetViewPose(); }

        private void OnValidate()
        {
            pitchRange.x = Mathf.Clamp(pitchRange.x, -89f, 89f);
            pitchRange.y = Mathf.Clamp(pitchRange.y, pitchRange.x, 89f);
            lookSensitivity.x = Mathf.Max(0f, lookSensitivity.x);
            lookSensitivity.y = Mathf.Max(0f, lookSensitivity.y);
            aimSensitivityMultiplier = Mathf.Max(0f, aimSensitivityMultiplier);
        }
    }
}
