using RPG4.Input;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>每角色唯一管理器；由现有宿主明确调用阶段，只组织三个处理器。</summary>
    internal sealed class PlayerIntentManager
    {
        private readonly Player owner;
        private readonly PlayerInput source;
        private readonly PlayerContext context;
        private readonly InputIntentProcessor input;
        private readonly LocomotionIntentProcessor locomotion;
        private readonly PresentationIntentProcessor presentation;
        internal PlayerBlackboard Blackboard => context.Blackboard;
        internal bool HasAutomaticShotClip => context.Data.Attack.AutoShotClip != null;
        internal ShootingPresentationData ShotPresentation => context.Data.Attack.Presentation;
        internal PlayerIntentManager(Player owner, PlayerInput source, PlayerContext context)
        {
            this.owner = owner;
            this.source = source;
            this.context = context;
            input = source.IntentProcessor;
            locomotion = new LocomotionIntentProcessor(context);
            presentation = new PresentationIntentProcessor(context);
        }
        internal void Bind() => source.BindIntentManager(RefreshPermissions);
        internal void Dispose()
        {
            if (source != null) source.UnbindIntentManager(RefreshPermissions);
        }
        internal void RefreshPermissions()
        {
            PlayerContentData content = context.Content;
            input.SetControlAvailability(owner != null && owner.isActiveAndEnabled,
                content.Body != null && !content.Body.isKinematic,
                content.ViewController != null && content.ViewController.isActiveAndEnabled);
            locomotion.RefreshAvailability();
            presentation.Refresh();
        }
        internal void ResolveLocomotion(bool crouched, bool canStand) => locomotion.Resolve(crouched, canStand);
        internal void RecordPosture(bool crouched, bool canStand) => locomotion.RecordPosture(crouched, canStand);
        internal void RecordView(float yaw, float pitch) => presentation.RecordView(yaw, pitch);
        internal void ProcessLook(Vector2 sensitivity, Vector2 pitchRange, float aimMultiplier)
            => presentation.ProcessLook(sensitivity, pitchRange, aimMultiplier);
        internal void InitializeCamera(float distance, float minimum, float maximum, float fieldOfView)
            => presentation.InitializeCamera(distance, minimum, maximum, fieldOfView);
        internal void ProcessCameraScroll(float minimum, float maximum, float zoomSpeed)
            => presentation.ProcessCameraScroll(minimum, maximum, zoomSpeed);
        internal void ProcessCamera(float attackDistance, float attackFieldOfView, float explorationFieldOfView)
            => presentation.ProcessCamera(attackDistance, attackFieldOfView, explorationFieldOfView);
        internal void ProcessPose(Vector2 pitchRange) => presentation.ProcessPose(pitchRange);
        internal void RecordWeapon(UpperBodyDemand demand, WeaponActionSnapshot frame, float reloadPhase)
            => presentation.RecordWeapon(demand, frame, reloadPhase);
        internal void ClearWeaponInput() => input.ClearWeaponInput();
        internal void AcknowledgeWeaponCancellation() => presentation.AcknowledgeWeaponCancellation();
    }
}
