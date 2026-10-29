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
        [SerializeField, Min(0.01f)] private float shotInterval = 0.16f;
        [SerializeField, Min(0.01f)] private float shotAnimationDuration = 0.24f;
        [SerializeField, Min(0f)] private float layerFadeInDuration = 0.06f;
        [SerializeField, Min(0f)] private float layerFadeOutDuration = 0.12f;
        internal AnimationClip ShotClip => shotClip;
        internal AnimationClip AutoShotClip => autoShotClip;
        internal float ShotInterval => shotInterval;
        internal float ShotAnimationDuration => shotAnimationDuration;
        internal float LayerFadeInDuration => layerFadeInDuration;
        internal float LayerFadeOutDuration => layerFadeOutDuration;
        internal void Validate()
        {
            shotInterval = Mathf.Max(0.01f, shotInterval);
            shotAnimationDuration = Mathf.Max(0.01f, shotAnimationDuration);
            layerFadeInDuration = Mathf.Max(0f, layerFadeInDuration);
            layerFadeOutDuration = Mathf.Max(0f, layerFadeOutDuration);
        }
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
