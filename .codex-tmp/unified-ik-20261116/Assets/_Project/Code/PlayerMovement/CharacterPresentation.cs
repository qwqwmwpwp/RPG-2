using RPG4.Backpack.Items;
using RPG4.CharacterAnimation;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Equipment/preview adapter. All skeletal animation is submitted as semantic state.</summary>
    [DisallowMultipleComponent]
    public sealed class CharacterPresentation : MonoBehaviour
    {
        [SerializeField] private CharacterAnimationSystem animationSystem;
        [SerializeField] private Transform equipmentCarrier;
        private CharacterEquipmentPresentation previewEquipment;
        private RuntimeItem confirmedEquipment;
        private ulong eventSequence;
        internal Transform EquipmentCarrier => equipmentCarrier;
        public bool HasEquipmentModel => previewEquipment != null && previewEquipment.View != null;
        public void BindPreviewEquipment(RuntimeItem item)
        {
            if (previewEquipment == null) previewEquipment = new CharacterEquipmentPresentation(equipmentCarrier);
            confirmedEquipment = item;
            if (isActiveAndEnabled) ApplyPreview(0f);
        }
        private void Update() { if (previewEquipment != null) ApplyPreview(Time.deltaTime); }
        private void ApplyPreview(float deltaTime)
        {
            previewEquipment.SetVisible(true); previewEquipment.Refresh(confirmedEquipment);
            bool hasGrip = previewEquipment.TryGetGripPose(out Vector3 position, out Quaternion rotation);
            var state = new CharacterAnimationState
            {
                Time = Time.timeAsDouble, Active = true, Alive = true, CanMove = true,
                Preview = true, HasEquipment = HasEquipmentModel, HasGripPose = hasGrip,
                GripPosition = position, GripRotation = rotation,
                BodyYaw = transform.eulerAngles.y, ViewYaw = transform.eulerAngles.y
            };
            animationSystem?.Present(state, deltaTime, Time.frameCount);
        }
        private void OnDisable()
        {
            if (previewEquipment == null) return;
            previewEquipment.Release();
            animationSystem?.SubmitEvent(new CharacterAnimationEvent(++eventSequence, Time.timeAsDouble, CharacterAnimationEventKind.Reset));
        }
        private void OnDestroy() => previewEquipment?.Release();
    }
}
