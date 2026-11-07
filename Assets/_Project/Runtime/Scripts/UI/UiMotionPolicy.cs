using UnityEngine;

namespace RPG4.UI
{
    /// <summary>
    /// Read-only UI motion preference. It never changes gameplay or business timers.
    /// </summary>
    public static class UiMotionPolicy
    {
        private const string ReduceMotionKey = "RPG4.UI.ReduceMotion";
        private static bool reduceMotion;

        static UiMotionPolicy()
        {
            Refresh();
            PlayerUiPreferences.Changed += Refresh;
        }

        /// <summary>Whether UI-only motion should be reduced for the local player.</summary>
        public static bool ReduceMotion => reduceMotion;

        private static void Refresh()
        {
            reduceMotion = PlayerPrefs.GetInt(ReduceMotionKey, 0) != 0;
        }
    }
}
