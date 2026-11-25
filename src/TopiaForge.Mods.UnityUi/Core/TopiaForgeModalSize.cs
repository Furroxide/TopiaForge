using System;

namespace TopiaForge.Mods.UnityUi
{
    internal static class TopiaForgeModalSize
    {
        internal static float LimitScrollBody(float preferredHeight, float canvasHeight, float fixedHeight, float motionReserve = 0f)
        {
            if (canvasHeight <= 0f) return preferredHeight;
            var available = canvasHeight - 2f * TopiaForgeTokens.SafeMargin - fixedHeight - motionReserve;
            return Math.Min(preferredHeight, Math.Max(0f, available));
        }
    }
}
