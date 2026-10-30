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

        private float viewYaw;
        private float viewPitch;
        internal PlayerPresenter Presenter { get; private set; }
        internal Vector2 LookSensitivity => lookSensitivity;
        internal Vector2 PitchRange => pitchRange;
        internal float AimSensitivityMultiplier => aimSensitivityMultiplier;
        internal Vector2 InitialAngles => new Vector2(viewYaw, viewPitch);
        public float ViewYaw => viewYaw;

        private void Awake()
        {
            Vector3 angles = transform.rotation.eulerAngles;
            viewYaw = Mathf.DeltaAngle(0f, angles.y);
            viewPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, angles.x), pitchRange.x, pitchRange.y);
        }
        internal void Initialize(Transform ownerRoot, PlayerPresenter presenter)
        {
            Presenter = presenter;
            if (transform.parent != ownerRoot || skeletonRoot == null || skeletonRoot == ownerRoot
                || !skeletonRoot.IsChildOf(ownerRoot) || transform.IsChildOf(skeletonRoot)
                || skeletonRoot.GetComponent<Rigidbody>() != null)
            {
                Debug.LogError("PlayerViewController 需要角色直属子节点和独立的可见骨架引用。", this);
                enabled = false;
            }
        }
        internal void Apply(float yaw, float pitch)
        {
            if (!isActiveAndEnabled || transform.parent == null) return;
            viewYaw = yaw;
            viewPitch = pitch;
            transform.localRotation = Quaternion.Inverse(transform.parent.rotation)
                * Quaternion.Euler(viewPitch, viewYaw, 0f);
        }
        // 仅提交桥接已捕获的观察角，不再读取输入或逻辑黑板。
        private void LateUpdate() => Presenter?.ApplyViewPose();

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
