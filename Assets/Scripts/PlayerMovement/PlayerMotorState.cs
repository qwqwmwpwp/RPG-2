using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>Only motor state is restored/replayed; weapon, damage and presentation are never rewound.</summary>
    public struct PlayerMotorState
    {
        public Vector3 Position, Velocity;
        public float Yaw, VerticalVelocity, TurnVelocity;
        public bool Crouched;
    }
}
