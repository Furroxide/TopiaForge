using System;

namespace TopiaForge.Mods.UnityUi
{
    internal static class TopiaForgeModalSize
    {
        internal static float LimitScrollBody(float preferredHeight, float canvasHeight, float fixedHeight)
        {
            if (canvasHeight <= 0f) return preferredHeight;
            var available = canvasHeight - 2f * TopiaForgeTokens.SafeMargin - fixedHeight;
            return Math.Min(preferredHeight, Math.Max(0f, available));
        }
    }
}
