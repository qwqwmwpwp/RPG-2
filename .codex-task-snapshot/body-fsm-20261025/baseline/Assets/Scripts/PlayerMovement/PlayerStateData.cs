using System;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>暂由 Player 持有的可序列化状态数据，按父状态组组织配置。</summary>
    [Serializable]
    internal sealed class PlayerStateData
    {
        [SerializeField] private PlayerContentData content = new PlayerContentData();
        [SerializeField] private ExplorationState.Data exploration = new ExplorationState.Data();
        [SerializeField] private AttackState.Data attack = new AttackState.Data();
        [SerializeField] private AimState.Data aim = new AimState.Data();
        [SerializeField] private HipFireState.Data hipFire = new HipFireState.Data();

        internal PlayerContentData Content => content;
        internal ExplorationState.Data Exploration => exploration;
        internal AttackState.Data Attack => attack;
        internal AimState.Data Aim => aim;
        internal HipFireState.Data HipFire => hipFire;
    }

    /// <summary>状态使用的组件引用，在 Player Inspector 中配置。</summary>
    [Serializable]
    internal sealed class PlayerContentData
    {
        [SerializeField] private Animator animator;
        [SerializeField] private Rigidbody body;
        [SerializeField] private PlayerCameraController cameraController;
        [SerializeField] private PlayerAimPose aimPose;

        internal Animator Animator => animator;
        internal Rigidbody Body => body;
        internal PlayerCameraController CameraController => cameraController;
        internal PlayerAimPose AimPose => aimPose;

        internal void Initialize(Player player)
        {
            if (body == null) body = player.GetComponent<Rigidbody>();
            if (animator == null) animator = player.GetComponentInChildren<Animator>();
            if (aimPose == null && animator != null) aimPose = animator.GetComponent<PlayerAimPose>();
            if (aimPose != null) aimPose.Initialize(player, cameraController);
        }
    }
}
