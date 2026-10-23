using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>纯射击动画：共享腰射/ADS节拍，由 Player 按输入→模式→动画顺序驱动。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Player))]
    public sealed class PlayerShootSimulation : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private AnimationClip shotClip;
        [SerializeField, Min(0.01f)] private float shotInterval = 0.16f;
        [SerializeField, Min(0.01f)] private float shotAnimationDuration = 0.24f;
        [Tooltip("首发进入上半身射击状态的过渡时间。")]
        [SerializeField, Min(0f)] private float layerFadeInDuration = 0.06f;
        [Tooltip("末发完整结束后过渡到 Empty 的时间。")]
        [SerializeField, Min(0f)] private float layerFadeOutDuration = 0.12f;

        private static readonly int ShotSpeed = Animator.StringToHash("ShotSpeed");
        private static readonly int HipFire = Animator.StringToHash("UpperBody Shooting.HipFire");
        private static readonly int ADSFire = Animator.StringToHash("UpperBody Shooting.ADSFire");
        private static readonly int Empty = Animator.StringToHash("UpperBody Shooting.Empty");
        private const int ShootingLayer = 1;

        private Player player;
        private double nextShotTime;
        private double lastShotTime;
        private double presentationEndTime;
        private bool fireHeld;
        private bool playShot;
        private bool animationActive;
        private bool fadingOut;
        private bool ads;
        private bool resetAnimator = true;

        internal bool HasShotPresentation => fireHeld || playShot || animationActive || fadingOut;

        internal void Initialize(Player owner, Animator fallbackAnimator)
        {
            player = owner;
            if (animator == null) animator = fallbackAnimator;
            Stop();
        }

        private bool AnimatorAvailable => animator != null && animator.isActiveAndEnabled
            && animator.runtimeAnimatorController != null && animator.layerCount > ShootingLayer;

        internal void PrepareInput(bool canUseWeapon, float deltaTime)
        {
            if (!isActiveAndEnabled || !canUseWeapon || deltaTime <= 0f || shotClip == null
                || !AnimatorAvailable)
            {
                Stop();
                return;
            }

            double now = Time.timeAsDouble;
            if (resetAnimator) Stop();
            // 点按与松开可落在同一帧，不能只依靠 FireHeld；不跨帧缓存点按。
            fireHeld = player.Input.FireHeld;
            playShot = (player.Input.FirePressed || fireHeld) && now >= nextShotTime;
            if (playShot)
            {
                lastShotTime = now;
                nextShotTime = now + Mathf.Max(0.01f, shotInterval);
                presentationEndTime = now + Mathf.Max(0.01f, shotAnimationDuration);
            }
            else if (animationActive && now >= presentationEndTime)
            {
                animationActive = false;
                fadingOut = true;
                presentationEndTime = now + layerFadeOutDuration;
                animator.CrossFadeInFixedTime(Empty, layerFadeOutDuration, ShootingLayer, 0f);
            }
            else if (fadingOut && now >= presentationEndTime)
            {
                fadingOut = false;
            }
        }

        internal void TickAnimation()
        {
            if (!isActiveAndEnabled || !AnimatorAvailable) return;
            bool wantsAds = player.Input.AimHeld;
            int state = wantsAds ? ADSFire : HipFire;
            if (playShot)
            {
                animator.SetFloat(ShotSpeed, shotClip.length / Mathf.Max(0.01f, shotAnimationDuration));
                if (!animationActive && !fadingOut)
                    animator.CrossFadeInFixedTime(state, layerFadeInDuration, ShootingLayer, 0f);
                else
                    animator.Play(state, ShootingLayer, 0f);
                animationActive = true;
                fadingOut = false;
                playShot = false;
                ads = wantsAds;
            }
            else if (animationActive && ads != wantsAds)
            {
                // 两个状态复用同一片段；切换保留当前单发进度，绝不改变 nextShotTime。
                float phase = Mathf.Clamp01((float)(Time.timeAsDouble - lastShotTime)
                    / Mathf.Max(0.01f, shotAnimationDuration));
                animator.Play(state, ShootingLayer, phase);
                ads = wantsAds;
            }
        }

        internal void Stop()
        {
            bool hadPresentation = HasShotPresentation;
            fireHeld = false;
            playShot = false;
            animationActive = false;
            fadingOut = false;
            ads = false;
            nextShotTime = 0d;
            lastShotTime = 0d;
            presentationEndTime = 0d;
            resetAnimator |= hadPresentation;
            if (resetAnimator && AnimatorAvailable)
            {
                animator.Play(Empty, ShootingLayer, 0f);
                resetAnimator = false;
            }
        }

        private void OnDisable()
        {
            Stop();
            // 清除输入重新武装，禁用/恢复组件时不能继续消费之前按住的左右键。
            if (player != null) player.Stop();
        }

        private void OnEnable()
        {
            // 初次 OnEnable 可能早于 Player.Awake；仅清理已完成初始化的拥有者。
            if (player != null && player.Input != null) player.Input.Clear();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused && player != null) player.Stop();
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
