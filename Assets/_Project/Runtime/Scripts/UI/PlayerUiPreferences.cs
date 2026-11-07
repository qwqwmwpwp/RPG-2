using System;
using UnityEngine;

namespace RPG4.UI
{
    /// <summary>角色界面的本地显示偏好，不影响角色和武器规则。</summary>
    public static class PlayerUiPreferences
    {
        public static event Action Changed;
        public static float TextScale => Mathf.Clamp(PlayerPrefs.GetFloat("RPG4.UI.TextScale", 1f), 1f, 1.5f);
        public static bool HitFeedback => PlayerPrefs.GetInt("RPG4.UI.HitFeedback", 1) != 0;
        public static bool LowHealthPulse => PlayerPrefs.GetInt("RPG4.UI.LowHealthPulse", 1) != 0;

        public static void CycleTextScale()
        {
            float next = TextScale < 1.2f ? 1.25f : TextScale < 1.45f ? 1.5f : 1f;
            PlayerPrefs.SetFloat("RPG4.UI.TextScale", next);
            Save();
        }
        public static void ToggleHitFeedback()
        {
            PlayerPrefs.SetInt("RPG4.UI.HitFeedback", HitFeedback ? 0 : 1);
            Save();
        }
        public static void ToggleLowHealthPulse()
        {
            PlayerPrefs.SetInt("RPG4.UI.LowHealthPulse", LowHealthPulse ? 0 : 1);
            Save();
        }
        private static void Save()
        {
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }
}
