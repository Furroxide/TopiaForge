using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Projects a HUD's authored rectangle into its usable physical viewport around the dock pivot.</summary>
    internal static class UiHudViewport
    {
        internal const double SubtitleFraction = 0.12d;
        internal const float SafeMargin = 24f;
        internal const float SlotGap = 8f;

        internal static float ScreenScale(double width, double height) =>
            (float)Math.Sqrt((width / 1920d) * (height / 1080d));

        internal static UiLayoutRect SafeArea(UiLayoutRect canvas, double layoutScale)
        {
            var margin = SafeMargin * layoutScale;
            return new UiLayoutRect(canvas.X + margin, canvas.Y + margin, Math.Max(0d, canvas.Width - 2d * margin),
                Math.Max(0d, canvas.Height * (1d - SubtitleFraction) - 2d * margin));
        }

        internal static void AddGameHudReservations(UiLayoutRect screen, List<UiLayoutRect> occupied)
        {
            // LibSurfaces records the game HUD at 1920 × 1080. Use the screen scale independently of the mod's scale.
            var scale = ScreenScale(screen.Width, screen.Height);
            occupied.Add(new UiLayoutRect(screen.X + 24d * scale, screen.Y + 20d * scale,
                280d * scale, 48d * scale).Intersect(screen));
            occupied.Add(new UiLayoutRect(screen.X + (screen.Width - 280d * scale) / 2d, screen.Y + 16d * scale,
                280d * scale, 36d * scale).Intersect(screen));
            occupied.Add(new UiLayoutRect(screen.Right - 464d * scale, screen.Y + 20d * scale,
                440d * scale, 72d * scale).Intersect(screen));
        }

        internal static UiHudAnchor Anchor(UiHudAnchor anchor, UiHudVisibility visibility) => anchor != UiHudAnchor.Default
            ? anchor : visibility == UiHudVisibility.Always ? UiHudAnchor.Left : UiHudAnchor.BottomLeft;

        internal static UiLayoutRect Fit(UiLayoutRect area, double x, double y, double pivotX, double pivotY,
            UiLayoutExtent requested)
        {
            var width = FitAxis(x, pivotX, requested.Width, area.X, area.Right);
            var height = FitAxis(y, pivotY, requested.Height, area.Y, area.Bottom);
            return AtPivot(x, y, pivotX, pivotY, new UiLayoutExtent(width, height));
        }

        internal static UiLayoutRect AtPivot(double x, double y, double pivotX, double pivotY, UiLayoutExtent size) =>
            new UiLayoutRect(x - size.Width * pivotX, y - size.Height * pivotY, size.Width, size.Height);

        private static double FitAxis(double origin, double pivot, double wanted, double start, double end)
        {
            // Unity's anchors use floats. Snap a raster-subpixel discrepancy at the safe edge.
            const double tolerance = 0.001d;
            if (origin < start - tolerance || origin > end + tolerance) return 0d;
            if (pivot > 0d) wanted = Math.Min(wanted, Math.Max(0d, origin - start) / pivot);
            if (pivot < 1d) wanted = Math.Min(wanted, Math.Max(0d, end - origin) / (1d - pivot));
            return wanted;
        }
    }

    internal static class UiHudLayoutNotice
    {
        internal const string Text = "Panel does not fit at this scale";
        internal const float PreferredWidth = 360f;
    }
}
