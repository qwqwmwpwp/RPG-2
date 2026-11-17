using System;
using UnityEngine;
using RPG4.CharacterAnimation;

namespace RPG4.PlayerMovement
{
    /// <summary>角色引用与行为参数；数据不依赖运行时状态类型。</summary>
    [Serializable]
    internal sealed class PlayerStateData
    {
        [SerializeField] private PlayerContentData content = new PlayerContentData();
        [SerializeField] private MovementData movement = new MovementData();
        [SerializeField] private ExplorationData exploration = new ExplorationData();
        [SerializeField] private AimData aim = new AimData();
        [SerializeField] private HipFireData hipFire = new HipFireData();
        [SerializeField] private CrouchData crouch = new CrouchData();
        [SerializeField] private ReloadData reload = new ReloadData();
        [SerializeField] private ConsumableMovementData consumable = new ConsumableMovementData();
        [SerializeField] private HurtData hurt = new HurtData();
        [SerializeField] private OwnerCorrectionPresentationData ownerCorrection = new OwnerCorrectionPresentationData();
        internal PlayerContentData Content => content;
        internal MovementData Movement => movement;
        internal ExplorationData Exploration => exploration;
        internal AimData Aim => aim;
        internal HipFireData HipFire => hipFire;
        internal CrouchData Crouch => crouch;
        internal ReloadData Reload => reload;
        internal ConsumableMovementData Consumable => consumable;
        internal HurtData Hurt => hurt;
        internal OwnerCorrectionPresentationData OwnerCorrection => ownerCorrection;
    }

    [Serializable]
    internal sealed class PlayerContentData
    {
        [SerializeField] private CharacterAnimationSystem animationSystem;
        [SerializeField] private Rigidbody body;
        [SerializeField] private PlayerViewController viewController;
        [SerializeField] private Transform presentationRoot;
        internal CharacterAnimationSystem AnimationSystem => animationSystem;
        internal Rigidbody Body => body;
        internal PlayerViewController ViewController => viewController;
        internal Transform EquipmentCarrier { get; private set; }
        internal Transform PresentationRoot => presentationRoot;
        internal CapsuleCollider Capsule { get; private set; }
        internal void Initialize(Player player)
        {
            if (body == null) body = player.GetComponent<Rigidbody>();
            Capsule = player.GetComponent<CapsuleCollider>();
            if (animationSystem == null) animationSystem = player.GetComponentInChildren<CharacterAnimationSystem>(true);
            EquipmentCarrier = player.GetComponentInChildren<CharacterPresentation>(true)?.EquipmentCarrier;
            if (presentationRoot == null && animationSystem != null && animationSystem.transform != player.transform)
                presentationRoot = animationSystem.transform;
        }
    }

    [Serializable]
    internal sealed class OwnerCorrectionPresentationData
    {
        [Tooltip("仅衰减网络纠错偏移，不插值相邻模拟步，也不延迟正常移动。")]
        [SerializeField, Min(0f)] private float halfLife = 0.06f;
        [SerializeField, Min(0f)] private float maximumDistance = 2f;
        [SerializeField, Range(0f, 180f)] private float maximumYaw = 60f;
        internal float HalfLife => halfLife;
        internal float MaximumDistance => maximumDistance;
        internal float MaximumYaw => maximumYaw;
    }

    [Serializable]
    internal sealed class MovementData
    {
        [Tooltip("移动加速度（米/秒²）；0 时使用旧版平滑时间。")]
        [SerializeField, Min(0f)] private float acceleration = 12f;
        [Tooltip("停步、降速和大角度反向的减速度（米/秒²）；0 时使用旧版平滑时间。")]
        [SerializeField, Min(0f)] private float deceleration = 18f;
        [Tooltip("未配置加减速度时使用的兼容平滑时间（秒）。")]
        [SerializeField, Min(0f)] private float speedSmoothTime = 0.06f;
        [Tooltip("未配置加减速度时使用的兼容方向平滑时间（秒）。")]
        [SerializeField, Min(0f)] private float directionSmoothTime = 0.04f;
        internal float Acceleration => acceleration;
        internal float Deceleration => deceleration;
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
        internal float MoveSpeed => moveSpeed;
        internal float TurnSmoothTime => turnSmoothTime;
        internal float MoveInputThreshold => moveInputThreshold;
    }

    [Serializable]
    internal sealed class AimData
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 2.5f;
        [Tooltip("瞄准/侧移时物理根跟随观察角的缓冲时间（秒）；0 为立即响应。")]
        [SerializeField, Min(0f)] private float turnSmoothTime = 0.08f;
        [SerializeField, Min(0f)] private float moveInputThreshold = 0.01f;
        internal float MoveSpeed => moveSpeed;
        internal float TurnSmoothTime => turnSmoothTime;
        internal float MoveInputThreshold => moveInputThreshold;
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
    internal sealed class ConsumableMovementData
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 1.5f;
        internal float MoveSpeed => moveSpeed;
    }

    [Serializable]
    internal sealed class HurtData
    {
        [Tooltip("受击时暂停武器操作的秒数；移动与镜头保持可控。")]
        [SerializeField, Min(0f)] private float duration = 0.45f;
        internal float Duration => Mathf.Max(0f, duration);
    }

    [Serializable]
    internal sealed class ReloadData
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 2f;
        internal float MoveSpeed => moveSpeed;
    }
}
