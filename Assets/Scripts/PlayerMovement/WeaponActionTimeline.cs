using RPG4.Backpack.Equipment;
using RPG4.PlayerIntent;

namespace RPG4.PlayerMovement
{
    /// <summary>影响攻击状态持续的动作时序；独立于 Animator 和表现组件。</summary>
    internal sealed class WeaponActionTimeline
    {
        private Weapon actionWeapon;
        private ulong observedSequence;
        private bool actionActive, fadeObserved;
        private int shotFrame = -1, fadeFrame = -1;
        internal WeaponActionSnapshot Action { get; private set; }
        private float ShotDuration => actionWeapon != null ? actionWeapon.Definition.ShotDuration : 0.01f;
        private float FadeDuration => actionWeapon != null ? actionWeapon.Definition.FadeOutDuration : 0f;
        internal void Advance(Weapon weapon, double now, int frameId)
        {
            if (!ReferenceEquals(actionWeapon, weapon))
            {
                actionWeapon = weapon;
                ClearAction();
            }
            if (weapon == null) return;
            WeaponShot shot = weapon.LastShot;
            if (shot.Sequence != observedSequence)
            {
                observedSequence = shot.Sequence;
                actionActive = true;
                fadeObserved = false;
                shotFrame = frameId;
                fadeFrame = -1; // 同帧新发替代刚产生的淡出脉冲。
            }
            if (!actionActive)
            {
                Action = new WeaponActionSnapshot(shotFrame == frameId,
                    fadeFrame == frameId, false, false, false, 0f);
                return;
            }
            double elapsed = System.Math.Max(0d, now - shot.Time);
            bool active = elapsed < ShotDuration;
            bool fading = !active && elapsed < ShotDuration + FadeDuration;
            if (!active && !fadeObserved)
            {
                fadeObserved = true;
                fadeFrame = frameId;
            }
            Action = new WeaponActionSnapshot(shotFrame == frameId,
                fadeFrame == frameId, active, fading, (active || fading) && shot.Sustained,
                active ? (float)System.Math.Min(1d, System.Math.Max(0d, elapsed / ShotDuration)) : 0f);
            if (!active && !fading) actionActive = false;
        }
        internal void ClearAction()
        {
            observedSequence = actionWeapon != null ? actionWeapon.LastShot.Sequence : 0;
            actionActive = fadeObserved = false;
            shotFrame = fadeFrame = -1;
            Action = default;
        }
    }
}
