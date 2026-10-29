using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>射击动画配置与投影；开火节拍和动作生命周期由 PlayerFireAction 维护。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Player))]
    public sealed class PlayerShootSimulation : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private AnimationClip shotClip;
        [SerializeField] private AnimationClip autoShotClip;
        [SerializeField, Min(0.01f)] private float shotInterval = 0.16f;
        [SerializeField, Min(0.01f)] private float shotAnimationDuration = 0.24f;
        [Tooltip("首发进入上半身射击状态的过渡时间。")]
        [SerializeField, Min(0f)] private float layerFadeInDuration = 0.06f;
        [Tooltip("末发完整结束后过渡到 Empty 的时间。")]
        [SerializeField, Min(0f)] private float layerFadeOutDuration = 0.12f;

        private static readonly int ShotSpeed = Animator.StringToHash("ShotSpeed");
        private static readonly int HipFire = Animator.StringToHash("UpperBody Shooting.HipFire");
        private static readonly int ADSFire = Animator.StringToHash("UpperBody Shooting.ADSFire");
        private static readonly int HipAutoFire = Animator.StringToHash("UpperBody Shooting.HipAutoFire");
        private static readonly int ADSAutoFire = Animator.StringToHash("UpperBody Shooting.ADSAutoFire");
        private static readonly int Empty = Animator.StringToHash("UpperBody Shooting.Empty");
        private const int ShootingLayer = 1;

        private Player player;
        private bool hasShotProjection;
        private bool ads;
        private bool automatic;
        private bool fadingProjection;
        private bool resetAnimator = true;

        internal float ShotInterval => shotInterval;
        internal float ShotAnimationDuration => shotAnimationDuration;
        internal float LayerFadeOutDuration => layerFadeOutDuration;

        internal void Initialize(Player owner, Animator fallbackAnimator)
        {
            player = owner;
            if (animator == null) animator = fallbackAnimator;
            ClearPresentation();
        }

        private bool AnimatorAvailable => animator != null && animator.isActiveAndEnabled
            && animator.runtimeAnimatorController != null && animator.layerCount > ShootingLayer;

        internal void TickAnimation(PlayerFireAction.Frame frame, bool wantsAds)
        {
            if (!isActiveAndEnabled || !AnimatorAvailable || shotClip == null)
            {
                // 表现不可用不反馈给逻辑；恢复时清空旧画面，只响应恢复当帧的新发。
                if (!AnimatorAvailable) resetAnimator = true;
                ClearPresentation();
                return;
            }

            if (resetAnimator) ClearPresentation();
            if (hasShotProjection && automatic && autoShotClip == null) ClearPresentation();
            bool wantsAutomatic = frame.IsAutomatic && autoShotClip != null;
            int state = wantsAutomatic ? (wantsAds ? ADSAutoFire : HipAutoFire)
                : (wantsAds ? ADSFire : HipFire);
            if (frame.ShotStarted)
            {
                if (wantsAutomatic)
                {
                    // 连射循环只在进入时过渡，后续每发不重播动画。
                    if (!hasShotProjection || !automatic || fadingProjection)
                        animator.CrossFadeInFixedTime(state, layerFadeInDuration, ShootingLayer, 0f);
                    else if (ads != wantsAds)
                        SwitchAutomaticState(state);
                }
                else
                {
                    animator.SetFloat(ShotSpeed, shotClip.length / Mathf.Max(0.01f, shotAnimationDuration));
                    if (!hasShotProjection || automatic)
                        animator.CrossFadeInFixedTime(state, layerFadeInDuration, ShootingLayer, 0f);
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
                animator.CrossFadeInFixedTime(Empty, layerFadeOutDuration, ShootingLayer, 0f);
                fadingProjection = true;
            }
            else if (frame.ShotActive && hasShotProjection && ads != wantsAds)
            {
                // 模式切换只延续已有投影，不生成新发或补播缺失期间的动作。
                if (automatic)
                    SwitchAutomaticState(wantsAds ? ADSAutoFire : HipAutoFire);
                else
                    animator.Play(wantsAds ? ADSFire : HipFire, ShootingLayer, frame.ShotPhase);
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

        internal void ClearPresentation()
        {
            resetAnimator |= hasShotProjection;
            hasShotProjection = false;
            ads = false;
            automatic = false;
            fadingProjection = false;
            if (resetAnimator && AnimatorAvailable)
            {
                animator.Play(Empty, ShootingLayer, 0f);
                resetAnimator = false;
            }
        }

        private void OnDisable()
        {
            ClearPresentation();
            // 清除输入重新武装，禁用/恢复组件时不能继续消费之前按住的左右键。
            if (player != null) player.CancelWeaponAction();
        }

        private void OnEnable()
        {
            // 初次 OnEnable 可能早于 Player.Awake；仅清理已完成初始化的拥有者。
            if (player != null) player.CancelWeaponAction();
        }

        private void OnValidate()
        {
            shotInterval = Mathf.Max(0.01f, shotInterval);
            shotAnimationDuration = Mathf.Max(0.01f, shotAnimationDuration);
            layerFadeInDuration = Mathf.Max(0f, layerFadeInDuration);
            layerFadeOutDuration = Mathf.Max(0f, layerFadeOutDuration);
        }
    }
}
