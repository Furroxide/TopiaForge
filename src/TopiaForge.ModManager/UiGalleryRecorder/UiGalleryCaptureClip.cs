using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    // Apply masks in hierarchy order, starting at the recorded widget. A viewport can hide a
    // scrollable part of the widget, but it cannot repair clipping already caused by a fixed mask.
    internal struct UiGalleryCaptureClip
    {
        private const float ScreenTolerance = 0.01f;
        private UiVerticalReveal reveal;

        internal UiGalleryCaptureClip(UiCaptureRect bounds, float? scrollLineHeight = null)
        {
            Visible = bounds;
            reveal = new UiVerticalReveal(bounds.Y, bounds.Height, scrollLineHeight);
            FixedClipped = false;
            ScrollClipped = false;
        }

        internal UiCaptureRect Visible { get; private set; }
        internal bool HasVisibleArea => Visible.Width > 0 && Visible.Height > 0;
        internal bool FixedClipped { get; private set; }
        internal bool ScrollClipped { get; private set; }
        internal bool AnyClipping => FixedClipped || ScrollClipped;

        internal void Apply(UiCaptureRect mask, UiCaptureRect? viewport = null, UiCaptureRect? scrollContent = null)
        {
            var x = Visible.Width > 0 &&
                (Visible.X < mask.X - ScreenTolerance || Visible.Right > mask.Right + ScreenTolerance ||
                    Visible.X >= mask.Right || Visible.Right <= mask.X);
            var y = Visible.Height > 0 &&
                (Visible.Y < mask.Y - ScreenTolerance || Visible.Bottom > mask.Bottom + ScreenTolerance ||
                    Visible.Y >= mask.Bottom || Visible.Bottom <= mask.Y);
            FixedClipped |= x;
            if (viewport is { } frame && scrollContent is { } body && body.Height > frame.Height)
            {
                // The content can already be scrolled. Express both legal endpoints relative to this snapshot.
                var topShift = frame.Y - body.Y;
                var bottomShift = topShift - (body.Height - frame.Height);
                var reachable = reveal.Project(body.Y, body.Bottom, tolerance: ScreenTolerance) &&
                    reveal.Project(mask.Y, mask.Bottom, bottomShift, topShift, ScreenTolerance);
                FixedClipped |= !reachable;
                ScrollClipped |= y;
            }
            else
            {
                FixedClipped |= y || !reveal.Project(mask.Y, mask.Bottom, tolerance: ScreenTolerance);
            }
            Visible = Visible.Intersect(mask);
        }
    }
}
