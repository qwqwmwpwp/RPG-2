using RPG4.Backpack.Items;
using RPG4.Combat;
using RPG4.Input;
using RPG4.Interaction;
using RPG4.PlayerIntent;
using RPG4.UI;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>表现侧唯一黑板读取入口；统一维护子表现、目标、事件游标及绑定生命周期。</summary>
    internal sealed class PlayerPresenter
    {
        private readonly PlayerBlackboard blackboard;
        private readonly Player owner;
        private readonly PlayerRenderPose renderPose;
        private readonly PlayerInput localInput;
        private readonly PlayerStateData data;
        private readonly PlayerWeaponRig weaponRig;
        private readonly PlayerLocomotionPresentation locomotion;
        private readonly PlayerWeaponPresentation weapon;
        private readonly PlayerLifePresentation life;
        private readonly PlayerConsumablePresentation consumable;
        private readonly List<CharacterHealthView> healthViews = new List<CharacterHealthView>();
        private readonly List<CharacterStatusHUD> statusViews = new List<CharacterStatusHUD>();
        private CrosshairUI crosshair;
        private PlayerPublishedFrame frame;
        private PlayerMotionTarget motionTarget;
        private PlayerWeaponTarget weaponTarget;
        private PlayerPoseTarget poseTarget;
        private PlayerCameraTarget cameraTarget;
        private PlayerCameraController camera;
        private readonly CharacterEquipmentPresentation equipment;
        private WeaponView weaponView => equipment.View;
        private ulong lastEventSequence;
        private int advancedFrame = -1;
        private float explorationDistance;
        private bool cameraInitialized, enabled = true, wasActive, wasAlive = true, wasReloading, shotAiming;
        private bool reloadCrouched;
        private Vector2 renderView;
        internal bool WeaponIKEnabled => enabled && poseTarget.SupportHandWeight > 0f;

        internal PlayerPresenter(Transform ownerRoot, PlayerBlackboard blackboard, PlayerStateData data, PlayerWeaponRig rig,
            RPG4.CharacterAttributes.CharacterAttributes attributes)
        {
            this.blackboard = blackboard;
            owner = ownerRoot.GetComponent<Player>();
            renderPose = new PlayerRenderPose(ownerRoot, data.Content.PresentationRoot, data.OwnerCorrection);
            localInput = ownerRoot.GetComponent<PlayerInput>();
            this.data = data;
            weaponRig = rig;
            equipment = new CharacterEquipmentPresentation(rig);
            locomotion = new PlayerLocomotionPresentation(data);
            weapon = new PlayerWeaponPresentation(data);
            life = new PlayerLifePresentation(data);
            consumable = new PlayerConsumablePresentation(data);
            data.Content.ViewController?.Initialize(ownerRoot, this);
            data.Content.AimPose?.Initialize(ownerRoot);
            RefreshHealthBindings(attributes);
        }
        internal void RefreshHealthBindings(RPG4.CharacterAttributes.CharacterAttributes attributes)
        {
            foreach (CharacterHealthView view in healthViews)
                if (view != null) view.SetExternalPresentation(false);
            healthViews.Clear(); statusViews.Clear();
            if (localInput == null || !localInput.UsesLocalInput) return;
            foreach (CharacterHealthView view in Object.FindObjectsOfType<CharacterHealthView>(true))
            {
                if (view.SourceAttributes != attributes) continue;
                view.SetExternalPresentation(true);
                view.SetPresentationActive(enabled);
                healthViews.Add(view);
                CharacterStatusHUD status = view.GetComponent<CharacterStatusHUD>();
                if (status != null) statusViews.Add(status);
            }
        }
        private void EnsureLocalCrosshair()
        {
            if (crosshair != null || localInput == null || !localInput.UsesLocalInput) return;
            PlayerInteraction interaction = localInput.GetComponent<PlayerInteraction>();
            if (interaction == null || interaction.CrosshairPrefab == null) return;
            crosshair = Object.Instantiate(interaction.CrosshairPrefab, localInput.transform, false);
            crosshair.SetState(false, false);
        }

        internal void Present(float deltaTime, bool reevaluate = false)
        {
            PlayerPublishedFrame published = blackboard.Published;
            if (published == null) return;
            frame = published;
            if (!enabled)
            {
                lastEventSequence = frame.LastEventSequence;
                return;
            }
            EnsureLocalCrosshair();
            float localZoom = localInput != null ? localInput.ConsumePresentationZoom() : 0f;
            if (camera != null && localZoom != 0f && frame.CanLook
                && !frame.WeaponDemand.AimCamera && !frame.WeaponDemand.AttackCamera)
            {
                explorationDistance = Mathf.Clamp(explorationDistance - localZoom * camera.ZoomSpeed,
                    camera.MinimumDistance, camera.MaximumDistance);
            }
            // 同一模拟版本也可能有尚未被固定步消费的新鼠标输入。
            renderView = owner != null ? owner.GetLocalRenderViewAngles() : new Vector2(frame.ViewYaw, frame.ViewPitch);
            BuildTargets();
            float step = advancedFrame == Time.frameCount ? 0f : Mathf.Max(0f, deltaTime);
            if (deltaTime > 0f) advancedFrame = Time.frameCount;
            bool active = frame.Active && frame.Alive;
            if (!wasAlive && frame.Alive || !active && wasActive) ResetOwnerPresentation();
            renderPose.Advance(step);
            if (active)
            {
                if (!wasActive) locomotion.Reset(frame.PhysicallyCrouched);
                locomotion.Apply(motionTarget, step);
                data.Content.AimPose?.ApplyPresentation(poseTarget, step, Time.frameCount);
                if (frame.Consumable.Active)
                {
                    if (!consumable.Active) weapon.Reset();
                    consumable.Apply(frame);
                }
                else
                {
                    consumable.Reset();
                    weapon.Apply(weaponTarget);
                }
                if (weaponTarget.Reloading && (!wasActive || !wasReloading || reloadCrouched != frame.Plan.Crouched))
                    weaponRig?.RestoreReloadPose(data.Reload.Clip(frame.Plan.Crouched), weaponTarget.ReloadPhase);
            }
            else if (wasActive || !wasAlive && frame.Alive)
            {
                if (frame.Alive) locomotion.Reset(frame.PhysicallyCrouched);
                consumable.Reset();
                weapon.Reset();
                data.Content.AimPose?.ResetPose();
                RestoreSocket();
            }
            if (wasReloading && !weaponTarget.Reloading || !wasAlive && frame.Alive) RestoreSocket();
            life.Apply(frame.Active, frame.Alive, frame.Time, frame.HurtUntil);
            foreach (CharacterHealthView view in healthViews)
                if (view != null) view.ApplyHealth(frame.CurrentHealth, frame.MaxHealth, frame.Alive);
            foreach (CharacterStatusHUD status in statusViews)
                if (status != null) status.ApplyState(frame.Alive, frame.PhysicallyCrouched,
                    frame.CanStand, frame.CrouchHeld, frame.CanMove, frame.ViewYaw);
            if (crosshair != null) crosshair.SetState(frame.Active && frame.InteractionVisible,
                frame.HasInteractionTarget, frame.InteractionPrompt);
            ApplyViewPose();
            ConsumeAnticipatedShots();
            ConsumeEvents();
            if (reevaluate) data.Content.AimPose?.Reevaluate();
            wasActive = active;
            wasAlive = frame.Alive;
            wasReloading = weaponTarget.Reloading;
            reloadCrouched = frame.Plan.Crouched;
        }

        private void BuildTargets()
        {
            bool allowed = frame.Active && frame.Alive && frame.CanUseWeapon && !frame.Consumable.Active;
            PlayerLocalWeaponPresentation local = owner != null ? owner.GetLocalWeaponPresentation() : default;
            bool anticipated = allowed && local.Active;
            UpperBodyDemand demand = anticipated ? local.Demand : frame.WeaponDemand;
            WeaponActionSnapshot action = anticipated ? local.Action : frame.WeaponAction;
            bool isAiming = anticipated ? local.IsAiming : frame.IsAiming;
            bool reloading = anticipated ? local.Reloading : frame.Reloading;
            ulong presentationSequence = 0;
            bool mappedShot = owner != null && owner.TryGetAnticipatedPresentationSequence(
                frame.WeaponId, frame.ShotSequence, out presentationSequence);
            bool suppressShot = anticipated
                ? weapon.ShouldSuppressAnticipation(local.WeaponId, local.ShotSequence)
                : (owner != null && owner.ShouldDeferConfirmedShotPresentation(frame.WeaponId, frame.ShotSequence))
                    || (mappedShot && weapon.ShouldSuppressAnticipation(frame.WeaponId, presentationSequence));
            if (suppressShot)
            {
                action = default;
                demand = new UpperBodyDemand(demand.AimCamera || reloading, demand.AimCamera,
                    false, reloading, demand.MoveSpeed, demand.AnimationSpeed);
            }
            bool posing = allowed && (demand.AimCamera || demand.ShotPose);
            float layerWeight = allowed && (demand.ShotPose || demand.Reloading) ? 1f : 0f;
            if (!demand.ShotPose || action.ShotActive) shotAiming = isAiming;
            bool aiming = demand.ShotPose ? shotAiming : isAiming;
            bool automatic = demand.ShotPose && action.IsAutomatic && data.Attack.AutoShotClip != null;
            ShootingPresentationData settings = data.Attack.Presentation;
            ShotPresentationData shot = settings.Get(aiming, automatic);
            poseTarget = new PlayerPoseTarget(posing, allowed && demand.Strafe && frame.CanLook,
                reloading, renderView.x, renderView.y, settings.YawOffset + shot.YawOffset,
                settings.WaistPitchOffset + shot.WaistPitchOffset, posing ? 1f : 0f, layerWeight,
                localInput != null && localInput.UsesLocalInput);
            motionTarget = new PlayerMotionTarget(frame.Plan, frame.CanMove, frame.IsAiming,
                frame.PlanarVelocity, frame.BodyYaw);
            weaponTarget = anticipated ? new PlayerWeaponTarget(frame, layerWeight, local, suppressShot)
                : new PlayerWeaponTarget(frame, layerWeight, mappedShot, presentationSequence, suppressShot);
            if (camera != null)
            {
                bool attack = allowed && (demand.AimCamera || demand.AttackCamera);
                cameraTarget = new PlayerCameraTarget(attack,
                    attack ? camera.AttackDistance : explorationDistance,
                    attack ? camera.AttackFieldOfView : camera.ExplorationFieldOfView, explorationDistance);
            }
        }

        private void ConsumeAnticipatedShots()
        {
            if (owner == null) return;
            while (owner.TryDequeueAnticipatedShot(out PlayerAnticipatedShot shot))
            {
                if (!frame.Active || !frame.Alive || shot.WeaponId != frame.WeaponId) continue;
                if (weaponView != null && weaponView.PlayAnticipatedShot(new ItemId(shot.WeaponId), shot.RequestId, shot.Ordinal))
                    owner.MarkAnticipatedShotPresented(shot);
            }
        }

        private void ConsumeEvents()
        {
            var events = frame.Events;
            if (events.Count > 0 && lastEventSequence != 0 && events[0].Sequence > lastEventSequence + 1)
                Debug.LogWarning("Player 表现事件超过保留窗口，持续表现已从最新黑板版本恢复。");
            foreach (PlayerPresentationEvent entry in events)
            {
                if (entry.Sequence <= lastEventSequence) continue;
                lastEventSequence = entry.Sequence;
                // 历史枪声/火光不补播；真实扣弹和伤害不经过此表现通道。
                if (entry.Kind == PlayerEventKind.Shot && frame.Active && frame.Alive
                    && entry.WeaponId == frame.WeaponId && frame.Time - entry.Time <= 0.25d)
                {
                    if (owner == null || owner.ShouldPresentConfirmedShot(entry.WeaponId, entry.ShotSequence))
                        weaponView?.PlayShot(new ItemId(entry.WeaponId), entry.ShotSequence);
                    if (crosshair != null) crosshair.ShowShotFeedback(entry.Success, entry.Obstructed);
                }
                else if (entry.Kind == PlayerEventKind.WeaponCancelled && !frame.Reloading)
                    RestoreSocket();
                else if (entry.Kind == PlayerEventKind.InteractionFeedback && frame.Time - entry.Time <= 2.5d)
                {
                    if (crosshair != null) crosshair.ShowFeedback(entry.Message, entry.Success);
                }
                else if (entry.Kind == PlayerEventKind.Hurt && frame.Time - entry.Time <= 1.25d)
                {
                    foreach (CharacterHealthView view in healthViews)
                        if (view != null) view.ShowDamage(entry.Amount);
                    foreach (CharacterStatusHUD status in statusViews)
                        if (status != null) status.ShowDamageDirection(entry.Direction);
                }
                else if (entry.Kind == PlayerEventKind.Revived)
                {
                    foreach (CharacterHealthView view in healthViews)
                        if (view != null) view.ResetFeedback();
                }
            }
        }

        internal void ApplyViewPose()
        {
            if (enabled && frame != null) data.Content.ViewController?.Apply(renderView.x, renderView.y);
        }
        internal void BeforeOwnerReconcile() => renderPose.BeforeReconcile();
        internal void AfterOwnerReconcile(bool discontinuity)
        {
            if (renderPose.AfterReconcile(discontinuity)) camera?.ResetRenderHistory();
        }
        internal void ResetOwnerPresentation()
        {
            renderPose.Reset();
            weaponView?.ResetAnticipatedShots();
            camera?.ResetRenderHistory();
        }
        internal void RegisterCamera(PlayerCameraController value)
        {
            camera = value;
            if (!cameraInitialized)
            {
                explorationDistance = Mathf.Clamp(value.InitialDistance, value.MinimumDistance, value.MaximumDistance);
                cameraInitialized = true;
            }
            if (frame != null) BuildTargets();
        }
        internal void UnregisterCamera(PlayerCameraController value) { if (camera == value) camera = null; }
        internal void ApplyCamera(PlayerCameraController value, float deltaTime)
        {
            if (enabled && frame != null && camera == value) value.Apply(cameraTarget, deltaTime);
        }
        internal void SetEnabled(bool value)
        {
            if (enabled == value) return;
            enabled = value;
            ResetOwnerPresentation();
            equipment.SetVisible(value);
            PlayerPublishedFrame published = blackboard.Published;
            lastEventSequence = published != null ? published.LastEventSequence : 0;
            if (frame != null && frame.Alive) locomotion.Reset(frame.PhysicallyCrouched);
            weapon.Reset();
            life.Reset();
            consumable.Reset();
            data.Content.AimPose?.ResetPose();
            if (crosshair != null) crosshair.SetState(false, false);
            foreach (CharacterHealthView view in healthViews)
                if (view != null) view.SetPresentationActive(value);
            foreach (CharacterStatusHUD status in statusViews)
                if (status != null) status.SetPresentationActive(value);
            wasActive = wasReloading = false;
            frame = null;
            advancedFrame = -1;
            if (value) Present(0f, true);
        }
        internal void Dispose()
        {
            SetEnabled(false);
            ReleaseEquipment();
            if (crosshair != null) Object.Destroy(crosshair.gameObject);
            foreach (CharacterHealthView view in healthViews)
                if (view != null) view.SetExternalPresentation(false);
            healthViews.Clear();
            statusViews.Clear();
            camera = null;
        }
        private void RestoreSocket() => weaponRig?.SwitchSocketByString("To_Hand_R_Socket,IK_ON_Left_Handle");

        internal void RefreshEquipment(RuntimeItem item) => equipment.Refresh(item);
        internal void ReleaseEquipment() => equipment.Release();
    }
}
