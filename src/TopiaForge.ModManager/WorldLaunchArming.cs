using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    /// <summary>Only direct startup may opt into the manager's remembered selection.</summary>
    internal static class WorldLaunchArming
    {
        public static LaunchSelection? Resolve(RuntimeStartupSelection startup, LaunchSelection? remembered, bool autoLoad)
            => startup.Requested == null && !startup.SafeMode && autoLoad ? remembered : null;
    }
}
