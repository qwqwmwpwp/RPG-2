using System;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色引用与行为参数；数据不依赖运行时状态类型。</summary>
    [Serializable]
    internal sealed class PlayerStateData
    {
        [SerializeField] private PlayerContentData content = new PlayerContentData();
        [SerializeField] private ExplorationData exploration = new ExplorationData();
        [SerializeField] private AttackData attack = new AttackData();
        [SerializeField] private AimData aim = new AimData();
        [SerializeField] private HipFireData hipFire = new HipFireData();
        [SerializeField] private CrouchData crouch = new CrouchData();
        [SerializeField] private ReloadData reload = new ReloadData();
        internal PlayerContentData Content => content;
        internal ExplorationData Exploration => exploration;
        internal AttackData Attack => attack;
        internal AimData Aim => aim;
        internal HipFireData HipFire => hipFire;
        internal CrouchData Crouch => crouch;
        internal ReloadData Reload => reload;
    }

    [Serializable]
    internal sealed class PlayerContentData
    {
        [SerializeField] private Animator animator;
        [SerializeField] private Rigidbody body;
        [SerializeField] private PlayerViewController viewController;
        [SerializeField] private PlayerAimPose aimPose;
        internal Animator Animator => animator;
        internal Rigidbody Body => body;
        internal PlayerViewController ViewController => viewController;
        internal PlayerAimPose AimPose => aimPose;
        internal CapsuleCollider Capsule { get; private set; }
        internal void Initialize(Player player)
        {
            if (body == null) body = player.GetComponent<Rigidbody>();
            Capsule = player.GetComponent<CapsuleCollider>();
            if (animator == null) animator = player.GetComponentInChildren<Animator>();
            if (aimPose == null && animator != null) aimPose = animator.GetComponent<PlayerAimPose>();
            if (viewController != null) viewController.Initialize(player, aimPose);
        }
    }

    [Serializable]
    internal sealed class ExplorationData
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 4f;
        [SerializeField, Min(0.01f)] private float turnSmoothTime = 0.12f;
        [SerializeField, Min(0f)] private float moveInputThreshold = 0.01f;
        [SerializeField, Min(0f)] private float animationTransitionDuration = 0.25f;
        internal float MoveSpeed => moveSpeed;
        internal float TurnSmoothTime => turnSmoothTime;
        internal float MoveInputThreshold => moveInputThreshold;
        internal float AnimationTransitionDuration => animationTransitionDuration;
    }

    [Serializable]
    internal sealed class AttackData
    {
        [SerializeField] private AnimationClip shotClip;
        [SerializeField] private AnimationClip autoShotClip;
        [InspectorName("射击间隔下限（秒）")]
        [Tooltip("实际开火间隔取此值与射击持续时间中的较大值，避免提前打断动画。")]
        [SerializeField, Min(0.01f)] private float shotInterval = 0.16f;
        [InspectorName("射击持续时间（秒）")]
        [Tooltip("单发动画或连射一轮的播放时长；播放速度为动画时长除以此值。")]
        [SerializeField, Min(0.01f)] private float shotAnimationDuration = 0.24f;
        [InspectorName("射击表现")]
        [SerializeField] private ShootingPresentationData presentation = new ShootingPresentationData();
        [SerializeField, Min(0f)] private float layerFadeOutDuration = 0.12f;
        internal AnimationClip ShotClip => shotClip;
        internal AnimationClip AutoShotClip => autoShotClip;
        internal float ShotInterval => shotInterval;
        internal float ShotAnimationDuration => shotAnimationDuration;
        internal ShootingPresentationData Presentation => presentation;
        internal float LayerFadeOutDuration => layerFadeOutDuration;
        internal void Validate()
        {
            shotInterval = Mathf.Max(0.01f, shotInterval);
            shotAnimationDuration = Mathf.Max(0.01f, shotAnimationDuration);
            presentation.Validate();
            layerFadeOutDuration = Mathf.Max(0f, layerFadeOutDuration);
        }
    }

    [Serializable]
    internal sealed class ShootingPresentationData
    {
        [InspectorName("角色 Y 轴旋转偏差总控（度）")]
        [Tooltip("与当前模式的整体 Y 轴旋转偏差相加。")]
        [SerializeField] private float yawOffset;
        [InspectorName("腰部 X 轴旋转偏差总控（度）")]
        [Tooltip("与当前模式的腰部 X 轴旋转偏差相加。")]
        [SerializeField] private float waistPitchOffset;

        [InspectorName("普通单发")]
        [SerializeField] private ShotPresentationData hipFire = new ShotPresentationData();
        [InspectorName("普通连射")]
        [SerializeField] private ShotPresentationData hipAutoFire = new ShotPresentationData();
        [InspectorName("瞄准单发")]
        [SerializeField] private ShotPresentationData adsFire = new ShotPresentationData();
        [InspectorName("瞄准连射")]
        [SerializeField] private ShotPresentationData adsAutoFire = new ShotPresentationData();

        internal float YawOffset => yawOffset;
        internal float WaistPitchOffset => waistPitchOffset;
        internal ShotPresentationData Get(bool aiming, bool automatic) => automatic
            ? (aiming ? adsAutoFire : hipAutoFire)
            : (aiming ? adsFire : hipFire);

        internal void Validate()
        {
            hipFire.Validate();
            hipAutoFire.Validate();
            adsFire.Validate();
            adsAutoFire.Validate();
        }
    }

    [Serializable]
    internal sealed class ShotPresentationData
    {
        [InspectorName("动画缓入时长（秒）")]
        [Tooltip("进入此动画或切换到此模式的混合时长；0 为立即切换。")]
        [SerializeField, Min(0f)] private float fadeInDuration = 0.06f;
        [InspectorName("整体 Y 轴旋转偏差（度）")]
        [Tooltip("相对摄像机水平朝向，绕 Y 轴旋转可见骨架；0 沿用准心方向。")]
        [SerializeField] private float yawOffset;
        [InspectorName("腰部 X 轴旋转偏差（度）")]
        [Tooltip("绕角色横向 X 轴叠加到腰部：负数上仰、正数下俯；不参与腰胸俯仰比例分摊。")]
        [SerializeField] private float waistPitchOffset;

        internal float FadeInDuration => fadeInDuration;
        internal float YawOffset => yawOffset;
        internal float WaistPitchOffset => waistPitchOffset;
        internal void Validate() => fadeInDuration = Mathf.Max(0f, fadeInDuration);
    }

    [Serializable]
    internal sealed class AimData
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 2.5f;
        [SerializeField, Min(0f)] private float moveInputThreshold = 0.01f;
        [SerializeField] private string aimAnimation = "Base Layer.AimLocomotion";
        [SerializeField, Min(0f)] private float animationTransitionDuration = 0.15f;
        [SerializeField, Min(0f)] private float animationParameterSmoothTime = 0.1f;
        internal float MoveSpeed => moveSpeed;
        internal float MoveInputThreshold => moveInputThreshold;
        internal string AimAnimation => aimAnimation;
        internal float AnimationTransitionDuration => animationTransitionDuration;
        internal float AnimationParameterSmoothTime => animationParameterSmoothTime;
    }

    [Serializable]
    internal sealed class HipFireData
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 6f;
        internal float MoveSpeed => moveSpeed;
    }

    [Serializable]
    internal sealed class CrouchData
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 2f;
        [SerializeField, Min(0.1f)] private float height = 1.2f;
        internal float MoveSpeed => moveSpeed;
        internal float Height => height;
    }

    [Serializable]
    internal sealed class ReloadData
    {
        [SerializeField, Min(0.01f)] private float duration = 1.6f;
        [SerializeField, Min(0.1f)] private float moveSpeed = 2f;
        [SerializeField] private AnimationClip reloadClip;
        [SerializeField] private AnimationClip crouchReloadClip;
        internal float Duration => Mathf.Max(0.01f, duration);
        internal float MoveSpeed => moveSpeed;
        internal AnimationClip Clip(bool crouched) => crouched ? crouchReloadClip : reloadClip;
    }
}
