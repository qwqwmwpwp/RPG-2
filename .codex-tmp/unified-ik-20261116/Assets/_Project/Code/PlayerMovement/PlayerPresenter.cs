using RPG4.Backpack.Items;
using RPG4.Combat;
using RPG4.CharacterAnimation;
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
        private readonly CharacterAnimationSystem animationSystem;
        private readonly PlayerAnimationAdapter animationAdapter;
        private ulong animationEventSequence;
        private readonly List<CharacterHealthView> healthViews = new List<CharacterHealthView>();
        private readonly List<CharacterStatusHUD> statusViews = new List<CharacterStatusHUD>();
        private CrosshairUI crosshair;
        private PlayerPublishedFrame frame;
        private CharacterAnimationState animationState;
        private PlayerCameraTarget cameraTarget;
        private PlayerCameraController camera;
        private readonly CharacterEquipmentPresentation equipment;
        private WeaponView weaponView => equipment.View;
        private ulong lastEventSequence;
        private int advancedFrame = -1;
        private float explorationDistance;
        private bool cameraInitialized, enabled = true, wasActive, wasAlive = true;
        private Vector2 renderView;
        private Vector3 sampledPosition, presentationVelocity, beforeReconcilePosition;
        private double sampledTime, beforeReconcileTime;
        private float sampleAge, beforeReconcileYaw;
        private bool hasMotionSample, reconciling;
        private Vector3 PhysicalPosition => data.Content.Body != null ? data.Content.Body.position : owner.transform.position;
        private float PhysicalYaw => data.Content.Body != null ? data.Content.Body.rotation.eulerAngles.y : owner.transform.eulerAngles.y;
        internal bool WeaponIKEnabled => enabled && animationSystem != null && animationSystem.WeaponIKEnabled;

        internal PlayerPresenter(Transform ownerRoot, PlayerBlackboard blackboard, PlayerStateData data,
            RPG4.CharacterAttributes.CharacterAttributes attributes)
        {
            this.blackboard = blackboard;
            owner = ownerRoot.GetComponent<Player>();
            renderPose = new PlayerRenderPose(ownerRoot, data.Content.PresentationRoot, data.OwnerCorrection);
            localInput = ownerRoot.GetComponent<PlayerInput>();
            this.data = data;
            animationSystem = data.Content.AnimationSystem;
            animationAdapter = new PlayerAnimationAdapter(owner, localInput);
            equipment = new CharacterEquipmentPresentation(data.Content.EquipmentCarrier);
            data.Content.ViewController?.Initialize(ownerRoot, this);
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
            float step = advancedFrame == Time.frameCount ? 0f : Mathf.Max(0f, deltaTime);
            if (deltaTime > 0f) advancedFrame = Time.frameCount;
            bool active = frame.Active && frame.Alive;
            if (!wasAlive && frame.Alive || !active && wasActive) ResetOwnerPresentation();
            renderPose.Advance(step);
            if (active) SampleMovement(step);
            else
            {
                hasMotionSample = false;
                presentationVelocity = Vector3.zero;
            }
            BuildTargets();
            animationSystem?.Present(animationState, step, Time.frameCount, reevaluate);
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
            wasActive = active;
            wasAlive = frame.Alive;
        }

        private void SampleMovement(float deltaTime)
        {
            Vector3 position = PhysicalPosition;
            double elapsed = frame.Time - sampledTime;
            sampleAge += deltaTime;
            if (!hasMotionSample || elapsed < 0d || elapsed > 0.5d)
            {
                sampledPosition = position;
                sampledTime = frame.Time;
                hasMotionSample = true;
                presentationVelocity = Vector3.zero;
                sampleAge = 0f;
                SendAnimationEvent(CharacterAnimationEventKind.MotionDiscontinuity);
                return;
            }
            if (elapsed <= 0d)
            {
                if (sampleAge > 0.15f) presentationVelocity = Vector3.zero;
                return;
            }
            Vector3 displacement = position - sampledPosition;
            displacement.y = 0f;
            float maximumSpeed = Mathf.Max(data.Exploration.MoveSpeed, data.HipFire.MoveSpeed, frame.PlanarVelocity.magnitude);
            float maximumDistance = Mathf.Max(data.OwnerCorrection.MaximumDistance, maximumSpeed * (float)elapsed * 2f);
            bool discontinuity = displacement.sqrMagnitude > maximumDistance * maximumDistance;
            presentationVelocity = discontinuity ? Vector3.zero : displacement / (float)elapsed;
            if (discontinuity) SendAnimationEvent(CharacterAnimationEventKind.MotionDiscontinuity);
            sampledPosition = position;
            sampledTime = frame.Time;
            sampleAge = 0f;
        }

        private void BuildTargets()
        {
            Transform visual = data.Content.PresentationRoot;
            float renderYawOffset = visual != null ? Mathf.DeltaAngle(PhysicalYaw, visual.eulerAngles.y) : 0f;
            animationState = animationAdapter.Build(frame, presentationVelocity, PhysicalYaw, renderView, renderYawOffset, equipment);
            UpperBodyDemand demand = animationAdapter.Demand;
            if (camera != null)
            {
                bool allowed = frame.Active && frame.Alive && frame.CanUseWeapon && !frame.Consumable.Active;
                bool attack = allowed && (demand.AimCamera || demand.AttackCamera);
                cameraTarget = new PlayerCameraTarget(attack,
                    attack ? camera.AttackDistance : explorationDistance,
                    attack ? camera.AttackFieldOfView : camera.ExplorationFieldOfView, explorationDistance);
            }
        }
        private void SendAnimationEvent(CharacterAnimationEventKind kind, float value = 0f, double? time = null)
            => animationSystem?.SubmitEvent(new CharacterAnimationEvent(++animationEventSequence,
                time ?? (frame != null ? frame.Time : 0d), kind, value));

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
                else if (entry.Kind == PlayerEventKind.WeaponCancelled && !animationState.Reload.Active)
                    SendAnimationEvent(CharacterAnimationEventKind.WeaponCancelled, time: entry.Time);
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
        internal void BeforeOwnerReconcile()
        {
            renderPose.BeforeReconcile();
            beforeReconcilePosition = PhysicalPosition;
            beforeReconcileYaw = PhysicalYaw;
            beforeReconcileTime = blackboard.Published != null ? blackboard.Published.Time : 0d;
            reconciling = true;
        }
        internal void AfterOwnerReconcile(bool discontinuity)
        {
            bool reset = renderPose.AfterReconcile(discontinuity) || discontinuity || !reconciling;
            if (reset)
            {
                hasMotionSample = false;
                camera?.ResetRenderHistory();
            }
            else
            {
                // 将采样参考一起平移到重放后的基线，纠错位移不计为正常行走。
                sampledPosition += PhysicalPosition - beforeReconcilePosition;
                if (blackboard.Published != null) sampledTime += blackboard.Published.Time - beforeReconcileTime;
                SendAnimationEvent(CharacterAnimationEventKind.RebaseYaw, Mathf.DeltaAngle(beforeReconcileYaw, PhysicalYaw));
            }
            reconciling = false;
        }
        internal void ResetOwnerPresentation()
        {
            animationAdapter.Reset();
            SendAnimationEvent(CharacterAnimationEventKind.Reset);
            renderPose.Reset();
            hasMotionSample = reconciling = false;
            presentationVelocity = Vector3.zero;
            sampleAge = 0f;
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
            if (frame != null && frame.Alive) SendAnimationEvent(CharacterAnimationEventKind.MotionDiscontinuity);
            SendAnimationEvent(CharacterAnimationEventKind.Reset);
            if (frame != null)
            {
                var idle = new CharacterAnimationState { Alive = frame.Alive, BodyYaw = PhysicalYaw, ViewYaw = renderView.x };
                animationSystem?.Present(idle, 0f, Time.frameCount, true);
            }
            if (crosshair != null) crosshair.SetState(false, false);
            foreach (CharacterHealthView view in healthViews)
                if (view != null) view.SetPresentationActive(value);
            foreach (CharacterStatusHUD status in statusViews)
                if (status != null) status.SetPresentationActive(value);
            wasActive = false;
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

        internal void RefreshEquipment(RuntimeItem item) => equipment.Refresh(item);
        internal void ReleaseEquipment() => equipment.Release();
    }
}
