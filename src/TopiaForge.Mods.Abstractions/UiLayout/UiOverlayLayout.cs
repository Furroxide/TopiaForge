using System;

namespace TopiaForge.Mods
{
    internal static class UiOverlayLayout
    {
        internal const int MaximumLive = 4;
        internal const float ScrimOpacity = 0.72f;
        internal const float EnterTravel = 24f;

        internal static UiLayoutExtent Clamp(UiLayoutExtent requested, UiLayoutExtent safe, float margin)
        {
            // The resting centre stays fixed, including in reduced motion. Reserve room for the entire entrance.
            return new UiLayoutExtent(
                Math.Min(requested.Width, Math.Max(0, safe.Width - 2 * margin)),
                Math.Min(requested.Height, Math.Max(0, safe.Height - 2 * (margin + EnterTravel))));
        }
    }
}
