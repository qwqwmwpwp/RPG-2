using RPG4.Input;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色子节点上的独立视角：维护观察角，统一同步角色姿态与相机跟随目标。</summary>
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
        private PlayerInput playerInput;
        private PlayerAimPose aimPose;
        private Transform posedRoot;
        private Quaternion animationLocalRotation;
        private float viewYaw;
        private float viewPitch;
        private bool coupled;
        private bool aiming;

        /// <summary>移动与瞄准共用的逻辑观察角，不受相机避障、构图或刚体插值影响。</summary>
        public float ViewYaw => viewYaw;
        internal float Zoom => CanReadInput ? playerInput.Zoom : 0f;
        internal bool IsAiming => aiming && CanReadInput && Cursor.lockState == CursorLockMode.Locked;
        private bool CanReadInput => player != null && player.CanControl;

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
            UpdatePivotRotation();
        }

        internal void Initialize(Player owner, PlayerAimPose pose)
        {
            player = owner;
            playerInput = owner.Input;
            aimPose = pose;
            if (transform.parent != owner.transform || skeletonRoot == null
                || skeletonRoot == owner.transform || !skeletonRoot.IsChildOf(owner.transform)
                || transform.IsChildOf(skeletonRoot) || skeletonRoot.GetComponent<Rigidbody>() != null)
            {
                Debug.LogError("PlayerViewController 需要角色直属子节点和独立的可见骨架引用。", this);
                enabled = false;
                return;
            }
            if (aimPose != null) aimPose.Initialize(owner, this);
        }

        internal void SetMode(bool followView, bool aim)
        {
            coupled = followView;
            aiming = aim;
            if (!coupled) RestorePose();
        }

        private void Update()
        {
            // 先还原上一帧姿态，再生成本帧观察角；Animator / IK 随后求值。
            RestorePose();
            if (aimPose != null) aimPose.BeginFrame();
            if (!CanReadInput)
            {
                SetMode(false, false);
                return;
            }
            Vector2 look = playerInput.Look;
            float sensitivity = IsAiming ? aimSensitivityMultiplier : 1f;
            viewYaw = Mathf.DeltaAngle(0f, viewYaw + look.x * lookSensitivity.x * sensitivity);
            viewPitch = Mathf.Clamp(viewPitch - look.y * lookSensitivity.y * sensitivity, pitchRange.x, pitchRange.y);
            UpdatePivotRotation();
        }

        private void LateUpdate()
        {
            RestorePose();
            if (aimPose != null) aimPose.ApplyPose(viewPitch);
            if (coupled && CanReadInput && Time.deltaTime > 0f)
            {
                // 物理根仍按 FixedUpdate 转向；只把可见骨架同步到本渲染帧视角。
                animationLocalRotation = skeletonRoot.localRotation;
                posedRoot = skeletonRoot;
                float yawOffset = Mathf.DeltaAngle(player.transform.eulerAngles.y, viewYaw);
                skeletonRoot.rotation = Quaternion.AngleAxis(yawOffset, Vector3.up) * skeletonRoot.rotation;
            }
            // 角色转身和插值完成后，抵消继承的旋转，保持独立的世界观察方向。
            UpdatePivotRotation();
        }

        private void UpdatePivotRotation()
        {
            transform.localRotation = Quaternion.Inverse(transform.parent.rotation)
                * Quaternion.Euler(viewPitch, viewYaw, 0f);
        }

        private void RestorePose()
        {
            if (posedRoot != null) posedRoot.localRotation = animationLocalRotation;
            posedRoot = null;
        }

        private void ResetViewPose()
        {
            SetMode(false, false);
            if (aimPose != null) aimPose.ResetPose();
        }

        private void OnDisable() => ResetViewPose();
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
