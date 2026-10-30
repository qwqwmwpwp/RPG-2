using RPG4.Backpack.Items;
using RPG4.Combat;
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
        private readonly PlayerStateData data;
        private readonly PlayerWeaponRig weaponRig;
        private readonly PlayerLocomotionPresentation locomotion;
        private readonly PlayerWeaponPresentation weapon;
        private readonly PlayerLifePresentation life;
        private readonly List<CharacterHealthView> healthViews = new List<CharacterHealthView>();
        private readonly CrosshairUI crosshair;
        private PlayerPublishedFrame frame;
        private PlayerMotionTarget motionTarget;
        private PlayerWeaponTarget weaponTarget;
        private PlayerPoseTarget poseTarget;
        private PlayerCameraTarget cameraTarget;
        private PlayerCameraController camera;
        private WeaponView weaponView;
        private ulong lastEventSequence;
        private int advancedFrame = -1, zoomFrame = -1;
        private float explorationDistance;
        private bool cameraInitialized, enabled = true, wasActive, wasAlive = true, wasReloading, shotAiming;
        private bool reloadCrouched;
        internal bool WeaponIKEnabled => enabled && poseTarget.SupportHandWeight > 0f;

        internal PlayerPresenter(Transform ownerRoot, PlayerBlackboard blackboard, PlayerStateData data, PlayerWeaponRig rig,
            RPG4.CharacterAttributes.CharacterAttributes attributes)
        {
            this.blackboard = blackboard;
            this.data = data;
            weaponRig = rig;
            locomotion = new PlayerLocomotionPresentation(data);
            weapon = new PlayerWeaponPresentation(data);
            life = new PlayerLifePresentation(data);
            data.Content.ViewController?.Initialize(ownerRoot, this);
            data.Content.AimPose?.Initialize(ownerRoot);
            weaponRig?.PrepareEquipment();
            PlayerInteraction interaction = ownerRoot.GetComponent<PlayerInteraction>();
            if (interaction != null && interaction.CrosshairPrefab != null)
            {
                crosshair = Object.Instantiate(interaction.CrosshairPrefab, ownerRoot, false);
                crosshair.SetState(false, false);
            }
            foreach (CharacterHealthView view in Object.FindObjectsOfType<CharacterHealthView>(true))
            {
                if (view.SourceAttributes != attributes) continue;
                view.SetExternalPresentation(true);
                view.SetPresentationActive(enabled);
                healthViews.Add(view);
            }
        }

        internal void Present(float deltaTime, bool reevaluate = false)
        {
            PlayerPublishedFrame published = blackboard.Published;
            if (published == null) return;
            bool changed = frame == null || frame.Version != published.Version;
            frame = published;
            if (!enabled)
            {
                lastEventSequence = frame.LastEventSequence;
                return;
            }
            if (changed) BuildTargets();
            float step = advancedFrame == Time.frameCount ? 0f : Mathf.Max(0f, deltaTime);
            if (deltaTime > 0f) advancedFrame = Time.frameCount;
            bool active = frame.Active && frame.Alive;
            if (active)
            {
                if (!wasActive) locomotion.Reset(frame.PhysicallyCrouched);
                locomotion.Apply(motionTarget, step);
                data.Content.AimPose?.ApplyPresentation(poseTarget, step, Time.frameCount);
                weapon.Apply(weaponTarget);
                if (frame.Reloading && (!wasActive || !wasReloading || reloadCrouched != frame.Plan.Crouched))
                    weaponRig?.RestoreReloadPose(data.Reload.Clip(frame.Plan.Crouched), frame.ReloadPhase);
            }
            else if (wasActive || !wasAlive && frame.Alive)
            {
                if (frame.Alive) locomotion.Reset(frame.PhysicallyCrouched);
                weapon.Reset();
                data.Content.AimPose?.ResetPose();
                RestoreSocket();
            }
            if (wasReloading && !frame.Reloading || !wasAlive && frame.Alive) RestoreSocket();
            life.Apply(frame.Active, frame.Alive, frame.Time, frame.HurtUntil);
            foreach (CharacterHealthView view in healthViews)
                if (view != null) view.ApplyHealth(frame.CurrentHealth, frame.MaxHealth, frame.Alive);
            if (crosshair != null) crosshair.SetState(frame.Active && frame.InteractionVisible,
                frame.HasInteractionTarget, frame.InteractionPrompt);
            ApplyViewPose();
            ConsumeEvents();
            if (reevaluate) data.Content.AimPose?.Reevaluate();
            wasActive = active;
            wasAlive = frame.Alive;
            wasReloading = frame.Reloading;
            reloadCrouched = frame.Plan.Crouched;
        }

        private void BuildTargets()
        {
            bool allowed = frame.Active && frame.Alive && frame.CanUseWeapon;
            UpperBodyDemand demand = frame.WeaponDemand;
            bool posing = allowed && (demand.AimCamera || demand.ShotPose);
            float layerWeight = allowed && (demand.ShotPose || demand.Reloading) ? 1f : 0f;
            if (!demand.ShotPose || frame.WeaponAction.ShotActive) shotAiming = frame.IsAiming;
            bool aiming = demand.ShotPose ? shotAiming : frame.IsAiming;
            bool automatic = demand.ShotPose && frame.WeaponAction.IsAutomatic && data.Attack.AutoShotClip != null;
            ShootingPresentationData settings = data.Attack.Presentation;
            ShotPresentationData shot = settings.Get(aiming, automatic);
            poseTarget = new PlayerPoseTarget(posing, allowed && demand.Strafe && frame.CanLook,
                frame.Reloading, frame.ViewYaw, frame.ViewPitch, settings.YawOffset + shot.YawOffset,
                settings.WaistPitchOffset + shot.WaistPitchOffset, posing ? 1f : 0f, layerWeight);
            motionTarget = new PlayerMotionTarget(frame.Plan, frame.CanMove, frame.IsAiming,
                frame.PlanarVelocity, frame.BodyYaw);
            weaponTarget = new PlayerWeaponTarget(frame, layerWeight);
            if (camera != null)
            {
                bool attack = allowed && (demand.AimCamera || demand.AttackCamera);
                if (zoomFrame != Time.frameCount && frame.CanLook && !attack)
                    explorationDistance = Mathf.Clamp(explorationDistance - frame.Zoom * camera.ZoomSpeed,
                        camera.MinimumDistance, camera.MaximumDistance);
                zoomFrame = Time.frameCount;
                cameraTarget = new PlayerCameraTarget(attack,
                    attack ? camera.AttackDistance : explorationDistance,
                    attack ? camera.AttackFieldOfView : camera.ExplorationFieldOfView, explorationDistance);
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
                    weaponView?.PlayShot(new ItemId(entry.WeaponId), entry.ShotSequence);
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
            if (enabled && frame != null) data.Content.ViewController?.Apply(frame.ViewYaw, frame.ViewPitch);
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
            PlayerPublishedFrame published = blackboard.Published;
            lastEventSequence = published != null ? published.LastEventSequence : 0;
            if (frame != null && frame.Alive) locomotion.Reset(frame.PhysicallyCrouched);
            weapon.Reset();
            life.Reset();
            data.Content.AimPose?.ResetPose();
            if (crosshair != null) crosshair.SetState(false, false);
            foreach (CharacterHealthView view in healthViews)
                if (view != null) view.SetPresentationActive(value);
            weaponRig?.SetVisible(value);
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
            camera = null;
        }
        private void RestoreSocket() => weaponRig?.SwitchSocketByString("To_Hand_R_Socket,IK_ON_Left_Handle");

        internal bool CanPrepareEquipment(GameObject prefab, out string error)
        {
            error = "角色缺少武器挂点与左手目标。";
            return weaponRig != null && weaponRig.CanPrepareEquipment(prefab, out error);
        }
        internal bool TryPrepareEquipment(GameObject prefab, out GameObject model, out string error)
        {
            model = null;
            error = "角色缺少武器挂点与左手目标。";
            return weaponRig != null && weaponRig.TryPrepareEquipment(prefab, out model, out error);
        }
        internal void BindEquipment(GameObject model, RuntimeItem item)
        {
            weaponRig.BindEquipment(model);
            weaponView = model.GetComponentInChildren<WeaponView>(true);
            weaponView?.Bind(item);
            weaponRig.SetVisible(enabled);
        }
        internal void RefreshEquipment(RuntimeItem item) => weaponView?.ApplyItemState(item);
        internal void ReleaseEquipment()
        {
            weaponView?.Unbind();
            weaponView = null;
            weaponRig?.UnbindEquipment();
        }
        internal bool TryGetShotOrigins(out Vector3 bodyOrigin, out Vector3 muzzleOrigin)
        {
            bodyOrigin = muzzleOrigin = default;
            if (!enabled || weaponView == null || weaponView.Muzzle == null) return false;
            bodyOrigin = weaponView.transform.position;
            muzzleOrigin = weaponView.Muzzle.position;
            return true;
        }
    }
}
