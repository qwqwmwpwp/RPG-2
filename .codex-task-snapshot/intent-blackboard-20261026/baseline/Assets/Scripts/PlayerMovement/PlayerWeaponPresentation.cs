using UnityEngine;

namespace RPG4.PlayerMovement
{
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
        internal PlayerWeaponPresentation(PlayerContext context) { this.context = context; }

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
            animator.SetFloat(ReloadSpeed, clip.length / context.Data.Reload.Duration);
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
            resetAnimator |= reloadProjection;
            reloadProjection = false;
            ClearPresentation();
        }
        private bool AnimatorAvailable => context.AnimatorIsAvailable && context.Content.Animator.layerCount > ShootingLayer;

        internal void Fire(PlayerFireAction.Frame frame, bool wantsAds)
        {
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
            if (frame.ShotStarted)
            {
                if (wantsAutomatic)
                {
                    // 连射循环只在进入时过渡，后续每发不重播动画。
                    if (!hasShotProjection || !automatic || fadingProjection)
                        animator.CrossFadeInFixedTime(state, context.Data.Attack.LayerFadeInDuration, ShootingLayer, 0f);
                    else if (ads != wantsAds)
                        SwitchAutomaticState(state);
                }
                else
                {
                    animator.SetFloat(ShotSpeed, context.Data.Attack.ShotClip.length / Mathf.Max(0.01f, context.Data.Attack.ShotAnimationDuration));
                    if (!hasShotProjection || automatic)
                        animator.CrossFadeInFixedTime(state, context.Data.Attack.LayerFadeInDuration, ShootingLayer, 0f);
                    else
                        animator.Play(state, ShootingLayer, 0f);
                }
                hasShotProjection = true;
                ads = wantsAds;
                automatic = wantsAutomatic;
                fadingProjection = false;
            }
            else if (frame.FadeStarted && hasShotProjection)
            {
                animator.CrossFadeInFixedTime(Empty, context.Data.Attack.LayerFadeOutDuration, ShootingLayer, 0f);
                fadingProjection = true;
            }
            else if (frame.ShotActive && hasShotProjection && ads != wantsAds)
            {
                // 模式切换只延续已有投影，不生成新发或补播缺失期间的动作。
                if (automatic)
                    SwitchAutomaticState(wantsAds ? ADSAutoFire : HipAutoFire);
                else
                    animator.Play(wantsAds ? ADSFire : HipFireAnimation, ShootingLayer, frame.ShotPhase);
                ads = wantsAds;
            }
            if (!frame.ShotActive && !frame.FadingOut)
            {
                hasShotProjection = false;
                automatic = false;
                fadingProjection = false;
            }
        }

        private void SwitchAutomaticState(int state)
        {
            Animator animator = context.Content.Animator;
            AnimatorStateInfo source = animator.GetCurrentAnimatorStateInfo(ShootingLayer);
            if (animator.IsInTransition(ShootingLayer))
            {
                AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(ShootingLayer);
                if (IsAutomaticState(next)) source = next;
            }
            float phase = IsAutomaticState(source) ? source.normalizedTime : 0f;
            animator.Play(state, ShootingLayer, phase);
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
