using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>上身动画执行器；仅消费桥接目标，不维护武器逻辑时序。</summary>
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
        private readonly PlayerStateData data;
        private bool hasShotProjection, ads, automatic, fadingProjection;
        private bool resetAnimator = true, reloadProjection, crouchReload;
        private ulong projectedWeapon, projectedSequence;
        private float ShotDuration = 0.01f, FadeDuration, reloadDuration = 1f;
        private Animator Animator => data.Content.Animator;
        private bool AnimatorAvailable => Animator != null && Animator.runtimeAnimatorController != null
            && Animator.isActiveAndEnabled && Animator.layerCount > ShootingLayer;
        internal PlayerWeaponPresentation(PlayerStateData data) { this.data = data; }
        internal void Apply(PlayerWeaponTarget target)
        {
            ShotDuration = Mathf.Max(0.01f, target.ShotDuration);
            FadeDuration = Mathf.Max(0f, target.FadeDuration);
            reloadDuration = Mathf.Max(0.01f, target.ReloadDuration);
            if (AnimatorAvailable) Animator.SetLayerWeight(ShootingLayer, target.LayerWeight);
            if (target.Reloading) Reload(target.Crouched, target.ReloadPhase);
            else
            {
                reloadProjection = false;
                Fire(target);
            }
        }
        internal void Reload(bool crouched, float phase)
        {
            if (!AnimatorAvailable)
            {
                reloadProjection = false;
                resetAnimator = true;
                return;
            }
            AnimationClip clip = data.Reload.Clip(crouched);
            if (clip == null) return;
            Animator animator = Animator;
            animator.SetFloat(ReloadSpeed, clip.length / reloadDuration);
            if (!reloadProjection || crouchReload != crouched)
            {
                // 逻辑时间控制完成；站蹲互切保留进度，不重复从头换弹。
                animator.Play(crouched ? CrouchReloadAnimation : ReloadAnimation, ShootingLayer, phase);
                reloadProjection = true;
                crouchReload = crouched;
                resetAnimator = false;
            }
        }

        internal void Reset()
        {
            resetAnimator = true;
            reloadProjection = false;
            ClearPresentation();
            if (AnimatorAvailable) Animator.SetLayerWeight(ShootingLayer, 0f);
        }

        private void Fire(PlayerWeaponTarget target)
        {
            WeaponActionSnapshot frame = target.WeaponAction;
            bool wantsAds = target.IsAiming;
            bool shotStarted = frame.ShotActive && (!hasShotProjection || projectedWeapon != target.WeaponId
                || projectedSequence != target.ShotSequence);
            bool fadeStarted = frame.FadingOut && hasShotProjection && !fadingProjection;
            if (shotStarted)
            {
                projectedWeapon = target.WeaponId;
                projectedSequence = target.ShotSequence;
            }
            Animator animator = Animator;
            if (!AnimatorAvailable || data.Attack.ShotClip == null)
            {
                // 表现不可用不反馈给逻辑；恢复时按当前活动阶段重建动画。
                if (!AnimatorAvailable) resetAnimator = true;
                ClearPresentation();
                return;
            }

            if (resetAnimator) ClearPresentation();
            if (hasShotProjection && automatic && data.Attack.AutoShotClip == null) ClearPresentation();
            bool wantsAutomatic = frame.IsAutomatic && data.Attack.AutoShotClip != null;
            int state = wantsAutomatic ? (wantsAds ? ADSAutoFire : HipAutoFire)
                : (wantsAds ? ADSFire : HipFireAnimation);
            float fadeInDuration = data.Attack.Presentation.Get(wantsAds, wantsAutomatic).FadeInDuration;
            // A single shot must reach its full pose before its logical duration ends.
            if (!wantsAutomatic && fadeInDuration >= ShotDuration)
                fadeInDuration = 0f;
            if (shotStarted)
            {
                float startOffset = frame.ShotPhase * ShotDuration;
                AnimationClip clip = wantsAutomatic ? data.Attack.AutoShotClip : data.Attack.ShotClip;
                animator.SetFloat(ShotSpeed, clip.length / ShotDuration);
                if (wantsAutomatic)
                {
                    // 连射循环只在进入时过渡，后续每发不重播动画。
                    if (!hasShotProjection || !automatic || fadingProjection)
                        animator.CrossFadeInFixedTime(state, fadeInDuration, ShootingLayer, startOffset);
                    else if (ads != wantsAds)
                        SwitchAutomaticState(state, fadeInDuration);
                }
                else
                {
                    if (!hasShotProjection || automatic || fadingProjection || ads != wantsAds)
                        animator.CrossFadeInFixedTime(state, fadeInDuration, ShootingLayer, startOffset);
                    else
                        animator.Play(state, ShootingLayer, frame.ShotPhase);
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
            Animator animator = Animator;
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
                Animator.Play(Empty, ShootingLayer, 0f);
                resetAnimator = false;
            }
        }

    }
}
