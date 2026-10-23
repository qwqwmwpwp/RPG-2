using System;
using UnityEngine;
using HierarchicalStateMachine = RPG4.StateMachine.StateMachine;

namespace RPG4.PlayerMovement
{
    /// <summary>腰射沿观察方向持枪移动，复用瞄准的六方向腿部动画。</summary>
    internal sealed class HipFireState : AimState
    {
        [Serializable]
        internal new sealed class Data
        {
            [SerializeField, Min(0.1f)] private float moveSpeed = 6f;
            internal float MoveSpeed => moveSpeed;
        }

        internal HipFireState(HierarchicalStateMachine machine, PlayerRootState parent, PlayerContext context)
            : base(machine, parent, context, hipFire: true) { }
    }
}
