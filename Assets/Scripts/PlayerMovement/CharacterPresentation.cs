using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>共用角色外观资产的展示入口；未启用预览时由局内 PlayerPresenter 驱动。</summary>
    [DisallowMultipleComponent]
    public sealed class CharacterPresentation : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private PlayerAimPose aimPose;
        [SerializeField] private PlayerWeaponRig weaponRig;
        private CharacterEquipmentPresentation previewEquipment;
        private RuntimeItem confirmedEquipment;
        private bool hasIdleState, wasArmed;
        public bool HasEquipmentModel => previewEquipment != null && previewEquipment.View != null;

        public void BindPreviewEquipment(RuntimeItem item)
        {
            if (previewEquipment == null)
            {
                previewEquipment = new CharacterEquipmentPresentation(weaponRig);
                animator.fireEvents = false;
                aimPose.Initialize(transform);
            }
            confirmedEquipment = item;
            if (isActiveAndEnabled) ApplyPreview(0f);
        }
        private void Update()
        {
            if (previewEquipment != null) ApplyPreview(Time.deltaTime);
        }
        private void ApplyPreview(float deltaTime)
        {
            previewEquipment.SetVisible(true);
            previewEquipment.Refresh(confirmedEquipment);
            bool armed = HasEquipmentModel;
            if (!hasIdleState || armed != wasArmed)
            {
                animator.SetFloat("MoveSpeed", 0f);
                animator.SetFloat("AimMoveX", 0f);
                animator.SetFloat("AimMoveY", 0f);
                animator.SetFloat("AimLocomotionSpeed", 1f);
                if (animator.layerCount > 1) animator.SetLayerWeight(1, 0f);
                animator.CrossFadeInFixedTime("Base Layer.FreeLocomotion", 0.15f, 0);
                hasIdleState = true;
                wasArmed = armed;
            }
            aimPose.ApplyPresentation(new PlayerPoseTarget(false, false, false,
                transform.eulerAngles.y, 0f, 0f, 0f, armed ? 1f : 0f, 0f), deltaTime, Time.frameCount);
        }
        private void OnDisable()
        {
            if (previewEquipment == null) return;
            previewEquipment.Release();
            aimPose.ResetPose();
            hasIdleState = false;
        }
        private void OnDestroy() => previewEquipment?.Release();
    }
}
