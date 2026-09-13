using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Global TopiaForgeTheme preferences are process-wide; the observer sets them only through the bounded
    // accessibility operations and always resets them at cleanup.
    internal static class NativeAccessibility
    {
        internal static OperationResult<bool> Apply(string operation)
        {
            switch (operation)
            {
                case "accessibility-high-contrast": TopiaForgeTheme.HighContrast = true; break;
                case "accessibility-scale-150": TopiaForgeTheme.UiScale = 1.5f; break;
                case "accessibility-reduced-motion": TopiaForgeTheme.ReducedMotion = true; break;
                case "accessibility-reset": Reset(); break;
                default: return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Unknown accessibility operation.");
            }
            return OperationResult<bool>.Success(true);
        }

        internal static void Reset()
        {
            TopiaForgeTheme.HighContrast = false;
            TopiaForgeTheme.UiScale = 1f;
            TopiaForgeTheme.ReducedMotion = false;
        }

        internal static Dictionary<string, object?> Observe() => NativeFactShapes.Accessibility(
            TopiaForgeTheme.HighContrast, TopiaForgeTheme.UiScale, TopiaForgeTheme.ReducedMotion, TopiaForgeTheme.EffectiveMotion);
    }
}
