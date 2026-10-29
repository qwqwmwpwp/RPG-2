using System;

namespace RPG4.PlayerMovement
{
    /// <summary>每角色的开火节拍与动作收尾；只接受宿主传入的输入、时间和配置。</summary>
    internal sealed class PlayerFireAction
    {
        internal readonly struct Frame
        {
            internal bool ShotStarted { get; }
            internal bool FadeStarted { get; }
            internal bool ShotActive { get; }
            internal bool FadingOut { get; }
            internal bool IsAutomatic { get; }
            internal float ShotPhase { get; }

            internal Frame(bool shotStarted, bool fadeStarted, bool shotActive, bool fadingOut,
                bool isAutomatic, float shotPhase)
            {
                ShotStarted = shotStarted;
                FadeStarted = fadeStarted;
                ShotActive = shotActive;
                FadingOut = fadingOut;
                IsAutomatic = isAutomatic;
                ShotPhase = shotPhase;
            }
        }

        private double nextShotTime;
        private double lastShotTime;
        private double actionEndTime;
        private float currentShotDuration;
        private bool fireHeld;
        private bool shotActive;
        private bool fadingOut;
        private bool hasShotInHold;
        private bool automatic;

        internal bool HasWeaponAction => fireHeld || shotActive || fadingOut;
        internal bool HasShotAction => shotActive || fadingOut;

        internal Frame Tick(bool firePressed, bool held, double now, float shotInterval,
            float shotDuration, float fadeOutDuration)
        {
            // 新按下与松开都切断手势；冷却内的新点按不会排队或继承连射计数。
            if (firePressed || !held) hasShotInHold = false;
            fireHeld = held;
            // 本帧最多一发；冷却内点按不排队，卡顿后不补发。
            bool shotStarted = (firePressed || held) && now >= nextShotTime;
            bool fadeStarted = false;
            if (shotStarted)
            {
                automatic = held && hasShotInHold;
                hasShotInHold = held;
                lastShotTime = now;
                nextShotTime = now + Math.Max(0.01f, shotInterval);
                currentShotDuration = Math.Max(0.01f, shotDuration);
                actionEndTime = now + currentShotDuration;
                shotActive = true;
                fadingOut = false;
            }
            else if (shotActive && now >= actionEndTime)
            {
                shotActive = false;
                fadingOut = true;
                fadeStarted = true;
                actionEndTime = now + Math.Max(0f, fadeOutDuration);
            }
            else if (fadingOut && now >= actionEndTime)
            {
                fadingOut = false;
            }

            // 松键不改变已发出的表现类型，末发与淡出沿用连射循环。
            if (!shotActive && !fadingOut) automatic = false;

            float phase = shotActive
                ? (float)Math.Min(1d, Math.Max(0d, (now - lastShotTime) / currentShotDuration)) : 0f;
            return new Frame(shotStarted, fadeStarted, shotActive, fadingOut, automatic, phase);
        }

        /// <summary>取消当前动作，保留武器冷却；仅新建运行对象时初始化冷却。</summary>
        internal void Cancel()
        {
            fireHeld = false;
            shotActive = false;
            fadingOut = false;
            hasShotInHold = false;
            automatic = false;
            lastShotTime = 0d;
            actionEndTime = 0d;
            currentShotDuration = 0f;
        }
    }
}
