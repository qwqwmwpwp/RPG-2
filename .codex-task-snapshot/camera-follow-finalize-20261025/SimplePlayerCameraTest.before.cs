using Cinemachine;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>临时对照相机：复用观察节点，直接输出位置与旋转，不做平滑或避障。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(200)]
    public sealed class SimplePlayerCameraTest : MonoBehaviour
    {
        [SerializeField] private Transform cameraPivot;
        [SerializeField] private CinemachineBrain brain;
        [SerializeField] private CinemachineVirtualCamera virtualCamera;
        [SerializeField] private Vector3 localOffset = new Vector3(0.45f, 0.35f, -2f);

        private bool ownsCamera;
        private bool brainWasEnabled;
        private bool virtualCameraWasEnabled;

        private void OnEnable()
        {
            // 引用不全时保留原相机模式，避免只禁用了一部分驱动。
            if (cameraPivot == null || brain == null || virtualCamera == null)
            {
                enabled = false;
                return;
            }

            brainWasEnabled = brain.enabled;
            virtualCameraWasEnabled = virtualCamera.enabled;
            ownsCamera = true;
            brain.enabled = false;
            virtualCamera.enabled = false;
        }

        private void LateUpdate()
        {
            if (!ownsCamera) return;
            if (cameraPivot == null || brain == null || virtualCamera == null)
            {
                enabled = false;
                return;
            }

            // 测试期间保持独占输出；原控制器仍负责输入、姿态与 CameraPivot。
            brain.enabled = false;
            virtualCamera.enabled = false;
            Quaternion rotation = cameraPivot.rotation;
            transform.SetPositionAndRotation(cameraPivot.position + rotation * localOffset, rotation);
        }

        private void OnDisable()
        {
            if (!ownsCamera) return;
            ownsCamera = false;
            if (virtualCamera != null)
            {
                virtualCamera.PreviousStateIsValid = false;
                virtualCamera.enabled = virtualCameraWasEnabled;
            }
            if (brain != null) brain.enabled = brainWasEnabled;
        }
    }
}
