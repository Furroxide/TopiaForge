using System;

namespace TopiaForge.ModManager.Core
{
    internal static class ModActivationPolicy
    {
        public static bool IsEnabledByDefault(ModManifest manifest)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            return !string.Equals(manifest.Category, "DevTool", StringComparison.OrdinalIgnoreCase);
        }
    }
}
