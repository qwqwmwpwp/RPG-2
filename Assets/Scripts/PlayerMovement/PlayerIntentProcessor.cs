using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    /// <summary>逻辑观察角与武器需求；不访问任何表现对象。</summary>
    internal sealed class PlayerIntentProcessor
    {
        private readonly PlayerContext context;
        private readonly PlayerBlackboard blackboard;
        internal PlayerIntentProcessor(PlayerContext context, PlayerBlackboard blackboard)
        {
            this.context = context;
            this.blackboard = blackboard;
        }
        internal void RecordView(float yaw, float pitch)
        {
            blackboard.ViewYaw = yaw;
            blackboard.ViewPitch = pitch;
        }
        internal void ProcessLook(Vector2 pitchRange)
        {
            if (!blackboard.CanLook || !blackboard.InputAllowed) return;
            RecordView(Mathf.DeltaAngle(0f, blackboard.ViewYaw + blackboard.Look.x),
                Mathf.Clamp(blackboard.ViewPitch + blackboard.Look.y,
                    pitchRange.x, pitchRange.y));
        }
        internal void ApplyRecoil(float pitch, float yaw, Vector2 pitchRange)
        {
            if (!blackboard.CanLook || !blackboard.IsAlive) return;
            RecordView(Mathf.DeltaAngle(0f, blackboard.ViewYaw + yaw),
                Mathf.Clamp(blackboard.ViewPitch - pitch, pitchRange.x, pitchRange.y));
        }
        internal void RecordWeapon(UpperBodyDemand demand, WeaponActionSnapshot frame, float reloadPhase)
        {
            blackboard.WeaponDemand = demand;
            blackboard.WeaponAction = frame;
            blackboard.ReloadPhase = reloadPhase;
            Refresh();
        }
        internal void Refresh()
        {
            UpperBodyDemand demand = blackboard.WeaponDemand;
            bool allowed = context.CanUseWeapon;
            if (!allowed && (demand.Strafe || demand.AimCamera || demand.ShotPose || demand.Reloading || demand.AttackCamera))
                blackboard.WeaponCancellationRequested = true;
            allowed &= !blackboard.WeaponCancellationRequested;
            blackboard.IsAiming = allowed && demand.AimCamera;
            blackboard.Reloading = allowed && demand.Reloading;
        }
        internal void AcknowledgeWeaponCancellation() => blackboard.WeaponCancellationRequested = false;
    }
}
