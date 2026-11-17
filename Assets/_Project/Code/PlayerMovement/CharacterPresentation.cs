using RPG4.Backpack.Items;
using RPG4.CharacterAnimation;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Equipment/preview adapter. All skeletal animation is submitted as semantic state.</summary>
    [DisallowMultipleComponent]
    public sealed class CharacterPresentation : MonoBehaviour
    {
        [SerializeField] private CharacterPresentationSystem animationSystem;
        [SerializeField] private Transform equipmentCarrier;
        private CharacterEquipmentFrames previewEquipment;
        private RuntimeItem confirmedEquipment;
        private ulong eventSequence;
        private int preparedFrame = -1, committedFrame = -1;
        internal Transform EquipmentCarrier => equipmentCarrier;
        public bool HasEquipmentModel => previewEquipment?.Active?.View != null;
        public void BindPreviewEquipment(RuntimeItem item)
        {
            if (previewEquipment == null) previewEquipment = new CharacterEquipmentFrames(equipmentCarrier);
            confirmedEquipment = item;
            previewEquipment.Request(item);
        }
        private void Update() { if (previewEquipment != null) ApplyPreview(Time.deltaTime); }
        private void ApplyPreview(float deltaTime)
        {
            if (preparedFrame == Time.frameCount)
            {
                animationSystem?.Present(Time.frameCount);
                return;
            }
            if (preparedFrame >= 0 && preparedFrame != committedFrame) CommitPreview(preparedFrame);
            preparedFrame = Time.frameCount;
            previewEquipment.Request(confirmedEquipment);
            CharacterAnimationState previous = animationSystem != null ? animationSystem.CommittedState : default;
            CharacterEquipmentPresentation planning = previewEquipment.BeginFrame(previous.EquipmentIdentity,
                previous.EquipmentVersion, previous.HasEquipment);
            bool hasGrip = planning.TryGetGripPose(out Vector3 position, out Quaternion rotation);
            var state = new CharacterAnimationState
            {
                Time = Time.timeAsDouble, Active = true, Alive = true, CanMove = true,
                Preview = true, HasEquipment = planning.View != null, HasGripPose = hasGrip,
                GripCarrierLocalPosition = position, GripCarrierLocalRotation = rotation,
                EquipmentIdentity = planning.Identity, EquipmentVersion = planning.Version,
                BodyYaw = transform.eulerAngles.y, ViewYaw = transform.eulerAngles.y
            };
            animationSystem?.Prepare(state, deltaTime, Time.frameCount);
            animationSystem?.Present(Time.frameCount);
        }
        private void LateUpdate() { if (previewEquipment != null) CommitPreview(Time.frameCount); }
        private void CommitPreview(int frameId)
        {
            if (preparedFrame != frameId || committedFrame == frameId) return;
            animationSystem?.Commit(frameId);
            committedFrame = frameId;
        }
        private void OnDisable()
        {
            if (previewEquipment == null) return;
            previewEquipment.Release();
            preparedFrame = committedFrame = -1;
            animationSystem?.SubmitEvent(new CharacterAnimationEvent(++eventSequence, Time.timeAsDouble, CharacterAnimationEventKind.Reset));
        }
        private void OnDestroy() => previewEquipment?.Release();
    }
}
