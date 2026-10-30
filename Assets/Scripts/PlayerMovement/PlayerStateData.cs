using System;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>角色引用与行为参数；数据不依赖运行时状态类型。</summary>
    [Serializable]
    internal sealed class PlayerStateData
    {
        [SerializeField] private PlayerContentData content = new PlayerContentData();
        [SerializeField] private MovementData movement = new MovementData();
        [SerializeField] private ExplorationData exploration = new ExplorationData();
        [SerializeField] private AttackData attack = new AttackData();
        [SerializeField] private AimData aim = new AimData();
        [SerializeField] private HipFireData hipFire = new HipFireData();
        [SerializeField] private CrouchData crouch = new CrouchData();
        [SerializeField] private ReloadData reload = new ReloadData();
        [SerializeField] private HurtData hurt = new HurtData();
        internal PlayerContentData Content => content;
        internal MovementData Movement => movement;
        internal ExplorationData Exploration => exploration;
        internal AttackData Attack => attack;
        internal AimData Aim => aim;
        internal HipFireData HipFire => hipFire;
        internal CrouchData Crouch => crouch;
        internal ReloadData Reload => reload;
        internal HurtData Hurt => hurt;
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
        }
    }

    [Serializable]
    internal sealed class MovementData
    {
        [Tooltip("移动加减速缓冲时间（秒）；0 为立即响应。")]
        [SerializeField, Min(0f)] private float speedSmoothTime = 0.06f;
        [Tooltip("移动方向缓冲时间（秒）；反向时沿角度过渡，保持移动强度。0 为立即响应。")]
        [SerializeField, Min(0f)] private float directionSmoothTime = 0.04f;
        internal float SpeedSmoothTime => speedSmoothTime;
        internal float DirectionSmoothTime => directionSmoothTime;
    }

    [Serializable]
    internal sealed class ExplorationData
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 4f;
        [Tooltip("自由移动时角色转向的缓冲时间（秒）；0 为立即响应。")]
        [SerializeField, Min(0f)] private float turnSmoothTime = 0.12f;
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
        [InspectorName("射击表现")]
        [SerializeField] private ShootingPresentationData presentation = new ShootingPresentationData();
        internal AnimationClip ShotClip => shotClip;
        internal AnimationClip AutoShotClip => autoShotClip;
        internal ShootingPresentationData Presentation => presentation;
        internal void Validate()
        {
            presentation.Validate();
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
        [Tooltip("瞄准/侧移时物理根跟随观察角的缓冲时间（秒）；0 为立即响应。")]
        [SerializeField, Min(0f)] private float turnSmoothTime = 0.08f;
        [SerializeField, Min(0f)] private float moveInputThreshold = 0.01f;
        [SerializeField] private string aimAnimation = "Base Layer.AimLocomotion";
        [SerializeField, Min(0f)] private float animationTransitionDuration = 0.15f;
        [SerializeField, Min(0f)] private float animationParameterSmoothTime = 0.1f;
        [Tooltip("瞄准时观察角速度达到此值后播放左右转身动画（度/秒）。")]
        [SerializeField, Min(0f)] private float turnAnimationThreshold = 35f;
        [Tooltip("允许播放原地转身动画的最大移动输入/动画强度；超过后立即衔接移动动画。")]
        [SerializeField, Range(0f, 1f)] private float turnAnimationMoveThreshold = 0.15f;
        [SerializeField, Min(0f)] private float turnAnimationTransitionDuration = 0.08f;
        internal float MoveSpeed => moveSpeed;
        internal float TurnSmoothTime => turnSmoothTime;
        internal float MoveInputThreshold => moveInputThreshold;
        internal string AimAnimation => aimAnimation;
        internal float AnimationTransitionDuration => animationTransitionDuration;
        internal float AnimationParameterSmoothTime => animationParameterSmoothTime;
        internal float TurnAnimationThreshold => turnAnimationThreshold;
        internal float TurnAnimationMoveThreshold => turnAnimationMoveThreshold;
        internal float TurnAnimationTransitionDuration => turnAnimationTransitionDuration;
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
    internal sealed class HurtData
    {
        [Tooltip("受击时暂停武器操作的秒数；移动与镜头保持可控。")]
        [SerializeField, Min(0f)] private float duration = 0.45f;
        [SerializeField, Min(0f)] private float transitionDuration = 0.08f;
        internal float Duration => Mathf.Max(0f, duration);
        internal float TransitionDuration => Mathf.Max(0f, transitionDuration);
    }

    [Serializable]
    internal sealed class ReloadData
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 2f;
        [SerializeField] private AnimationClip reloadClip;
        [SerializeField] private AnimationClip crouchReloadClip;
        internal float MoveSpeed => moveSpeed;
        internal AnimationClip Clip(bool crouched) => crouched ? crouchReloadClip : reloadClip;
    }
}
