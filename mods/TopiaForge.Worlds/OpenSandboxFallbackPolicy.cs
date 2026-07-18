using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Decides whether the emergency current-scene Open Sandbox fallback is safe. The fallback may layer an
    /// arena over an existing gameplay scene, but must never turn a menu, boot, loader, or unknown scene into a
    /// world session.
    /// </summary>
    internal static class OpenSandboxFallbackPolicy
    {
        public static bool CanBuildInScene(
            string? sceneName,
            IEnumerable<string?> knownGameplayScenes)
        {
            if (string.IsNullOrWhiteSpace(sceneName)
                || GameScenes.IsNonGameplayScene(sceneName)
                || knownGameplayScenes == null)
            {
                return false;
            }

            foreach (var knownScene in knownGameplayScenes)
            {
                if (!string.IsNullOrWhiteSpace(knownScene)
                    && !GameScenes.IsNonGameplayScene(knownScene)
                    && string.Equals(sceneName, knownScene, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
