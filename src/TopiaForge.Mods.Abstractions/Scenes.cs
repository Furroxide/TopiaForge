using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Shared classification of the game's scenes so every mod (and the manager) agrees on what counts as
    /// "the menu" versus a playable scene. Pure string logic — safe to use off the Unity main thread and
    /// from the test harness.
    /// </summary>
    public static class GameScenes
    {
        /// <summary>The verified main-menu scene of the current game build.</summary>
        public const string MainMenuSceneName = "TestCityStartMenu";

        /// <summary>True if <paramref name="sceneName"/> is the game's main-menu scene.</summary>
        public static bool IsMainMenuScene(string sceneName)
        {
            return string.Equals(sceneName, MainMenuSceneName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for menu/boot/loader/splash scenes — anywhere gameplay (and therefore a world/gamemode
        /// session) must not be active.
        /// </summary>
        public static bool IsNonGameplayScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return false;
            }

            return sceneName.IndexOf("StartMenu", StringComparison.OrdinalIgnoreCase) >= 0
                || sceneName.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0
                || sceneName.IndexOf("Boot", StringComparison.OrdinalIgnoreCase) >= 0
                || sceneName.IndexOf("Loader", StringComparison.OrdinalIgnoreCase) >= 0
                || sceneName.IndexOf("Splash", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
