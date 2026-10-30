using RPG4.PlayerIntent;
using RPG4.Backpack.Equipment;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>仅翻译意图与执行回报，所有结果写入共享黑板；不执行动画或骨架。</summary>
    internal sealed class PresentationIntentProcessor
    {
        private readonly PlayerContext context;
        private readonly PlayerBlackboard blackboard;
        internal PresentationIntentProcessor(PlayerContext context, PlayerBlackboard blackboard)
        {
            this.context = context;
            this.blackboard = blackboard;
        }

        internal void RecordView(float yaw, float pitch)
        {
            blackboard.ViewYaw = yaw;
            blackboard.ViewPitch = pitch;
        }
        internal void ProcessLook(Vector2 sensitivity, Vector2 pitchRange, float aimMultiplier)
        {
            if (!blackboard.CanLook) return;
            float multiplier = blackboard.IsAiming ? aimMultiplier : 1f;
            blackboard.ViewYaw = Mathf.DeltaAngle(0f,
                blackboard.ViewYaw + blackboard.Look.x * sensitivity.x * multiplier);
            blackboard.ViewPitch = Mathf.Clamp(blackboard.ViewPitch
                - blackboard.Look.y * sensitivity.y * multiplier, pitchRange.x, pitchRange.y);
        }
        internal void InitializeCamera(float distance, float minimum, float maximum, float fieldOfView)
        {
            blackboard.CameraExplorationDistance = Mathf.Clamp(distance, minimum, maximum);
            blackboard.CameraTargetDistance = blackboard.CameraExplorationDistance;
            blackboard.CameraTargetFieldOfView = fieldOfView;
        }
        internal void ProcessCameraScroll(float minimum, float maximum, float zoomSpeed)
        {
            if (!blackboard.CanLook || blackboard.UseAttackCamera) return;
            blackboard.CameraExplorationDistance = Mathf.Clamp(blackboard.CameraExplorationDistance
                - blackboard.Zoom * zoomSpeed, minimum, maximum);
        }
        internal void ProcessCamera(float attackDistance, float attackFieldOfView, float explorationFieldOfView)
        {
            blackboard.CameraTargetDistance = blackboard.UseAttackCamera ? attackDistance : blackboard.CameraExplorationDistance;
            blackboard.CameraTargetFieldOfView = blackboard.UseAttackCamera ? attackFieldOfView : explorationFieldOfView;
        }
        internal void ProcessPose(Vector2 pitchRange)
        {
            blackboard.AimPosePitch = blackboard.AimPoseEnabled
                ? Mathf.Clamp(blackboard.ViewPitch, pitchRange.x, pitchRange.y) : 0f;
        }
        internal void RecordWeapon(UpperBodyDemand demand, WeaponActionSnapshot frame, float reloadPhase)
        {
            blackboard.WeaponDemand = demand;
            blackboard.WeaponAction = frame;
            blackboard.ReloadPhase = reloadPhase;
            Refresh();
        }
        internal void Refresh()
        {
            UpperBodyDemand demand = blackboard.WeaponDemand;
            bool allowed = context.CanUseWeapon && Time.deltaTime > 0f;
            if (!allowed && (demand.Strafe || demand.AimCamera || demand.ShotPose || demand.Reloading || demand.AttackCamera))
                blackboard.WeaponCancellationRequested = true;
            allowed &= !blackboard.WeaponCancellationRequested;
            blackboard.FollowView = allowed && demand.Strafe;
            blackboard.IsAiming = allowed && demand.AimCamera;
            blackboard.UseAttackCamera = allowed && (demand.AimCamera || demand.AttackCamera);
            blackboard.AimPoseEnabled = allowed && (demand.AimCamera || demand.ShotPose);
            blackboard.SupportHandWeight = blackboard.AimPoseEnabled ? 1f : 0f;
            blackboard.WeaponIKEnabled = blackboard.SupportHandWeight > 0f;
            if (!blackboard.AimPoseEnabled) blackboard.AimPosePitch = 0f;
            blackboard.Reloading = allowed && demand.Reloading;
            blackboard.UpperBodyLayerWeight = allowed && (demand.ShotPose || demand.Reloading) ? 1f : 0f;
        }
        internal void AcknowledgeWeaponCancellation() => blackboard.WeaponCancellationRequested = false;
    }

    /// <summary>上身动画投影；Animator 信息只用于表现连续性，不决定武器逻辑。</summary>
    internal sealed class PlayerWeaponPresentation
    {
        private static readonly int ShotSpeed = Animator.StringToHash("ShotSpeed");
        private static readonly int HipFireAnimation = Animator.StringToHash("UpperBody Shooting.HipFire");
        private static readonly int ADSFire = Animator.StringToHash("UpperBody Shooting.ADSFire");
        private static readonly int HipAutoFire = Animator.StringToHash("UpperBody Shooting.HipAutoFire");
        private static readonly int ADSAutoFire = Animator.StringToHash("UpperBody Shooting.ADSAutoFire");
        private static readonly int Empty = Animator.StringToHash("UpperBody Shooting.Empty");
        private static readonly int ReloadAnimation = Animator.StringToHash("UpperBody Shooting.Reload");
        private static readonly int CrouchReloadAnimation = Animator.StringToHash("UpperBody Shooting.CrouchReload");
        private static readonly int ReloadSpeed = Animator.StringToHash("ReloadSpeed");
        private const int ShootingLayer = 1;
        private readonly PlayerContext context;
        private bool hasShotProjection;
        private bool ads;
        private bool automatic;
        private bool fadingProjection;
        private bool resetAnimator = true;
        private bool reloadProjection;
        private bool crouchReload;
        private Weapon actionWeapon;
        private ulong observedSequence;
        private bool actionActive;
        private bool fadeObserved;
        private int shotFrame = -1;
        private int fadeFrame = -1;
        private Weapon projectedWeapon;
        private ulong projectedSequence;
        private int projectedFadeFrame = -1;
        internal WeaponActionSnapshot Action { get; private set; }
        private float ShotDuration => context.Weapon != null ? context.Weapon.Definition.ShotDuration : 0.01f;
        private float FadeDuration => context.Weapon != null ? context.Weapon.Definition.FadeOutDuration : 0f;
        internal PlayerWeaponPresentation(PlayerContext context) { this.context = context; }

        // 包络只投影武器已发生的射击；取消后记住序号，旧记录不会复活。
        internal void AdvanceAction(double now)
        {
            Weapon weapon = context.Weapon;
            if (!ReferenceEquals(actionWeapon, weapon))
            {
                actionWeapon = weapon;
                ClearAction();
            }
            if (weapon == null) return;
            WeaponShot shot = weapon.LastShot;
            if (shot.Sequence != observedSequence)
            {
                observedSequence = shot.Sequence;
                actionActive = true;
                fadeObserved = false;
                shotFrame = Time.frameCount;
                fadeFrame = -1; // 同帧新发替代刚产生的淡出脉冲。
            }
            if (!actionActive)
            {
                Action = new WeaponActionSnapshot(shotFrame == Time.frameCount,
                    fadeFrame == Time.frameCount, false, false, false, 0f);
                return;
            }
            double elapsed = System.Math.Max(0d, now - shot.Time);
            bool active = elapsed < ShotDuration;
            bool fading = !active && elapsed < ShotDuration + FadeDuration;
            if (!active && !fadeObserved)
            {
                fadeObserved = true;
                fadeFrame = Time.frameCount;
            }
            Action = new WeaponActionSnapshot(shotFrame == Time.frameCount,
                fadeFrame == Time.frameCount, active, fading, (active || fading) && shot.Sustained,
                active ? Mathf.Clamp01((float)(elapsed / ShotDuration)) : 0f);
            if (!active && !fading) actionActive = false;
        }
        internal void ClearAction()
        {
            observedSequence = actionWeapon != null ? actionWeapon.LastShot.Sequence : 0;
            actionActive = fadeObserved = false;
            shotFrame = fadeFrame = -1;
            Action = default;
        }

        internal void ApplyLayerWeight()
        {
            Animator animator = context.Content.Animator;
            if (animator != null && animator.runtimeAnimatorController != null && animator.layerCount > 1)
                animator.SetLayerWeight(ShootingLayer, context.ReadState.Weapon.UpperBodyLayerWeight);
        }
        internal void Sync()
        {
            ApplyLayerWeight();
            IPlayerWeaponState weaponState = context.ReadState.Weapon;
            if (weaponState.Reloading) Reload(context.ReadState.Locomotion.Plan.Crouched, weaponState.ReloadPhase);
            else Fire(weaponState.WeaponAction, weaponState.IsAiming);
        }

        internal void Reload(bool crouched, float phase)
        {
            if (!AnimatorAvailable)
            {
                reloadProjection = false;
                resetAnimator = true;
                return;
            }
            AnimationClip clip = context.Data.Reload.Clip(crouched);
            if (clip == null) return;
            Animator animator = context.Content.Animator;
            animator.SetFloat(ReloadSpeed, clip.length / context.Weapon.Definition.ReloadDuration);
            if (!reloadProjection || crouchReload != crouched)
            {
                // 逻辑时间控制完成；站蹲互切保留进度，不重复从头换弹。
                animator.Play(crouched ? CrouchReloadAnimation : ReloadAnimation, ShootingLayer, phase);
                reloadProjection = true;
                crouchReload = crouched;
                resetAnimator = false;
            }
        }

        internal void Cancel()
        {
            ClearAction();
            resetAnimator |= reloadProjection;
            reloadProjection = false;
            ClearPresentation();
        }
        private bool AnimatorAvailable => context.AnimatorIsAvailable && context.Content.Animator.layerCount > ShootingLayer;

        internal void Fire(WeaponActionSnapshot frame, bool wantsAds)
        {
            Weapon weapon = context.Weapon;
            ulong sequence = weapon != null ? weapon.LastShot.Sequence : 0;
            bool shotStarted = frame.ShotStarted && (!ReferenceEquals(projectedWeapon, weapon)
                || projectedSequence != sequence);
            bool fadeStarted = frame.FadeStarted && projectedFadeFrame != Time.frameCount;
            if (shotStarted)
            {
                projectedWeapon = weapon;
                projectedSequence = sequence;
                projectedFadeFrame = -1;
            }
            if (fadeStarted) projectedFadeFrame = Time.frameCount;
            Animator animator = context.Content.Animator;
            if (!AnimatorAvailable || context.Data.Attack.ShotClip == null)
            {
                // 表现不可用不反馈给逻辑；恢复时清空旧画面，只响应恢复当帧的新发。
                if (!AnimatorAvailable) resetAnimator = true;
                ClearPresentation();
                return;
            }

            if (resetAnimator) ClearPresentation();
            if (hasShotProjection && automatic && context.Data.Attack.AutoShotClip == null) ClearPresentation();
            bool wantsAutomatic = frame.IsAutomatic && context.Data.Attack.AutoShotClip != null;
            int state = wantsAutomatic ? (wantsAds ? ADSAutoFire : HipAutoFire)
                : (wantsAds ? ADSFire : HipFireAnimation);
            float fadeInDuration = context.Data.Attack.Presentation.Get(wantsAds, wantsAutomatic).FadeInDuration;
            // A single shot must reach its full pose before its logical duration ends.
            if (!wantsAutomatic && fadeInDuration >= ShotDuration)
                fadeInDuration = 0f;
            if (shotStarted)
            {
                AnimationClip clip = wantsAutomatic ? context.Data.Attack.AutoShotClip : context.Data.Attack.ShotClip;
                animator.SetFloat(ShotSpeed, clip.length / ShotDuration);
                if (wantsAutomatic)
                {
                    // 连射循环只在进入时过渡，后续每发不重播动画。
                    if (!hasShotProjection || !automatic || fadingProjection)
                        animator.CrossFadeInFixedTime(state, fadeInDuration, ShootingLayer, 0f);
                    else if (ads != wantsAds)
                        SwitchAutomaticState(state, fadeInDuration);
                }
                else
                {
                    if (!hasShotProjection || automatic || fadingProjection || ads != wantsAds)
                        animator.CrossFadeInFixedTime(state, fadeInDuration, ShootingLayer, 0f);
                    else
                        animator.Play(state, ShootingLayer, 0f);
                }
                hasShotProjection = true;
                ads = wantsAds;
                automatic = wantsAutomatic;
                fadingProjection = false;
            }
            else if (fadeStarted && hasShotProjection)
            {
                animator.CrossFadeInFixedTime(Empty, FadeDuration, ShootingLayer, 0f);
                fadingProjection = true;
            }
            else if (frame.ShotActive && hasShotProjection && ads != wantsAds)
            {
                // 模式切换只延续已有投影，不生成新发或补播缺失期间的动作。
                if (automatic)
                    SwitchAutomaticState(wantsAds ? ADSAutoFire : HipAutoFire, fadeInDuration);
                else
                    // Fixed-time offsets use seconds after the state's speed multiplier.
                    animator.CrossFadeInFixedTime(wantsAds ? ADSFire : HipFireAnimation,
                        fadeInDuration, ShootingLayer, frame.ShotPhase * ShotDuration);
                ads = wantsAds;
            }
            if (!frame.ShotActive && !frame.FadingOut)
            {
                hasShotProjection = false;
                automatic = false;
                fadingProjection = false;
            }
        }

        private void SwitchAutomaticState(int state, float fadeInDuration)
        {
            Animator animator = context.Content.Animator;
            AnimatorStateInfo source = animator.GetCurrentAnimatorStateInfo(ShootingLayer);
            if (animator.IsInTransition(ShootingLayer))
            {
                AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(ShootingLayer);
                if (IsAutomaticState(next)) source = next;
            }
            float phase = IsAutomaticState(source) ? source.normalizedTime : 0f;
            // Preserve loop phase in the duration-scaled state, including multiple loops.
            animator.CrossFadeInFixedTime(state, fadeInDuration, ShootingLayer,
                phase * ShotDuration);
        }

        private static bool IsAutomaticState(AnimatorStateInfo state) => state.fullPathHash == HipAutoFire
            || state.fullPathHash == ADSAutoFire;

        private void ClearPresentation()
        {
            resetAnimator |= hasShotProjection;
            hasShotProjection = false;
            ads = false;
            automatic = false;
            fadingProjection = false;
            if (resetAnimator && AnimatorAvailable)
            {
                context.Content.Animator.Play(Empty, ShootingLayer, 0f);
                resetAnimator = false;
            }
        }

    }
}
