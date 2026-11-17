using UnityEngine;

namespace RPG4.PlayerIntent
{
    /// <summary>原始暂存只在周期末整理为 prepared；整个下一周期共享同一 current。</summary>
    public sealed class PlayerInputBuffer
    {
        private PlayerInputFrame raw, prepared;
        public PlayerInputFrame Current { get; private set; }
        public ulong Version { get; private set; }
        public bool CycleActive { get; private set; }
        public bool CurrentAllowed { get; private set; }
        private bool preparedAllowed;

        public bool Accumulate(PlayerInputFrame frame)
        {
            if (!Finite(frame.Move) || !Finite(frame.Look) || !Finite(frame.Zoom)
                || frame.ViewAngles.HasValue && !Finite(frame.ViewAngles.Value)) return false;
            Vector2 look = frame.ViewAngles.HasValue ? frame.Look : raw.Look + frame.Look;
            float zoom = raw.Zoom + frame.Zoom;
            if (!Finite(look) || !Finite(zoom)) return false;
            raw = new PlayerInputFrame(Vector2.ClampMagnitude(frame.Move, 1f), look, zoom,
                frame.CrouchHeld, frame.AimHeld, raw.FirePressed || frame.FirePressed, frame.FireHeld,
                raw.ReloadPressed || frame.ReloadPressed, raw.InteractPressed || frame.InteractPressed,
                frame.ViewAngles ?? raw.ViewAngles, raw.QuickUsePressed || frame.QuickUsePressed);
            return true;
        }
        public void BeginCycle()
        {
            if (CycleActive) return;
            CycleActive = true;
            Publish(prepared, preparedAllowed);
        }
        public void CompleteCycle(bool allowed)
        {
            if (!CycleActive) return;
            prepared = allowed ? raw : default;
            preparedAllowed = allowed;
            raw = new PlayerInputFrame(raw.Move, crouchHeld: raw.CrouchHeld,
                aimHeld: raw.AimHeld, fireHeld: raw.FireHeld);
            CycleActive = false;
        }
        /// <summary>网络入口已经完成周期准备，不再对同一输入加一层延迟。</summary>
        public void Publish(PlayerInputFrame frame, bool allowed = true)
        { Current = frame; CurrentAllowed = allowed; Version++; }
        public void Clear()
        {
            raw = prepared = default;
            preparedAllowed = false;
            // 生命周期中断可撤回未来输入，但不能改写正在运行的周期快照。
            if (!CycleActive) Publish(default, false);
        }
        public void ClearWeaponInput()
        {
            raw = WithoutWeapon(raw);
            prepared = WithoutWeapon(prepared);
        }
        public static PlayerInputFrame WithoutWeapon(PlayerInputFrame frame)
            => new PlayerInputFrame(frame.Move, frame.Look, frame.Zoom, frame.CrouchHeld,
                interactPressed: frame.InteractPressed, viewAngles: frame.ViewAngles,
                quickUsePressed: frame.QuickUsePressed);
        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
