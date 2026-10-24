using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The active scene as session follow reads it (WS3.8): its name, and whether it is a gameplay level or the main menu.
    /// The runtime tells it each scene that becomes active; a frame reads it without allocating. Main thread only.
    /// </summary>
    internal sealed class FollowedScene
    {
        /// <summary>Gets the active scene's name, or null before any scene was active.</summary>
        internal string? Name { get; private set; }

        /// <summary>Gets whether the active scene is a gameplay level, where a world session may run.</summary>
        internal bool IsGameplay { get; private set; }

        /// <summary>Gets whether the active scene is the game's main menu.</summary>
        internal bool IsMainMenu { get; private set; }

        /// <summary>Gets a number that moves each time another scene becomes active.</summary>
        internal int Version { get; private set; }

        /// <summary>Records the scene that became active; the same scene again changes nothing.</summary>
        internal void Activated(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName) || string.Equals(sceneName, Name, StringComparison.Ordinal)) return;
            Name = sceneName;
            IsMainMenu = GameScenes.IsMainMenuScene(sceneName);
            IsGameplay = !GameScenes.IsNonGameplayScene(sceneName);
            Version++;
        }
    }
}
