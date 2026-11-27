using System;

namespace TopiaForge.Mods
{
    internal static class UiStatBarLayout
    {
        internal const float Padding = 4f;
        internal const float MinimumHeight = 32f;

        internal static double ContentInset(UiLayoutDecoration chrome, UiLayoutExtent bounds, UiLayoutExtent content)
        {
            var radius = chrome.CornerRadius;
            var border = chrome.BorderWidth;
            var preferred = Math.Max(radius, border);
            if (bounds.Width >= content.Width + 2d * (preferred + Padding)) return preferred;

            // The full cap inset preserves the usual placement. In a tight bar, the centred
            // caption and cue only need to clear the arc at the edge of their shared band.
            var bandTop = Math.Max(0d, (bounds.Height - content.Height) / 2d - border - Padding);
            var arcY = Math.Max(0d, radius - bandTop);
            var arcInset = radius - Math.Sqrt((radius - arcY) * (radius + arcY));
            return Math.Min(preferred, border + arcInset);
        }
    }
}
