using System;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private const float OverlayScrimOpacity = .72f;

        private UiTreeCapture CaptureOverlay(float scrollOffset)
        {
            var screen = new UiCaptureRect(0f, 0f, viewport.Width, viewport.Height);
            var fill = scheme.GetPair(UiPaintRole.Surface).Fill;
            var progress = (float)surface.OverlayMotionProgress;
            var opacity = overlayState switch
            {
                "entering" => EaseOverlay(progress),
                "exiting" => 1f - progress,
                "shown" => 1f,
                _ => 0f
            };
            Add("/scrim", null, "OverlayScrim", screen, screen,
                new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.Decorative, state: overlayState),
                new UiCaptureNodePaint(null, fill), fillOpacity: OverlayScrimOpacity * opacity);

            var panel = SurfaceBounds();
            if (overlayState == "entering")
            {
                var slack = Math.Max(0f, viewport.Height - 18f * layoutScale - panel.Bottom);
                panel = new UiCaptureRect(panel.X, panel.Y + Math.Min(slack, 24f * layoutScale * (1f - opacity)),
                    panel.Width, panel.Height);
            }

            var extent = new UiLayoutExtent(panel.Width / layoutScale, panel.Height / layoutScale);
            var decoration = UiLayoutDecoration.ResolveComponent(theme.Descriptor, UiThemeTarget.Overlay, extent, preferences.HighContrast);
            var panelPaint = new UiCaptureNodePaint(null, fill, (float)decoration.CornerRadius * layoutScale,
                (float)decoration.BorderWidth * layoutScale, (float)decoration.ShadowX * layoutScale,
                (float)decoration.ShadowY * layoutScale, styleId: decoration.StyleId);
            Add("/", surface.Id, "Overlay", panel, screen,
                new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.None, state: overlayState),
                panelPaint, fillOpacity: 1f);

            var interior = OverlayInterior(panel);
            var title = OverlayTitle(interior);
            Text("/title", null, "OverlayTitle", title, interior, surface.Request.Title, UiTextStyle.Title, UiTone.Neutral,
                UiCaptureNodeFlags.RequiresLabel, UiPaintRole.Surface);
            var body = OverlayBody(interior, title);
            if (surface.Body.Length > 0)
                Text("/body", null, "OverlayBody", body, interior, surface.Body, UiTextStyle.Body, UiTone.Neutral,
                    UiCaptureNodeFlags.None, UiPaintRole.Surface);
            var content = OverlayContent(interior, title, body);
            if (surface.Content != null) PaintComposition(surface.Content, content, content, content.Y, scrollOffset);
            return new UiTreeCapture(nodes, preferences.HighContrast, ControlHeight);
        }

        private bool VerifyOverlay(UiNode? content)
        {
            var interior = OverlayInterior(SurfaceBounds());
            var title = OverlayTitle(interior);
            var body = OverlayBody(interior, title);
            if (!interior.Contains(title) || surface.Body.Length > 0 && !interior.Contains(body))
                throw new UiLayoutException(UiLayoutFailure.ClippedContent, "/title", "The overlay title and body cannot fit the safe area.");
            if (content == null) return true;
            var placement = OverlayContent(interior, title, body);
            var (layout, placeholderOverlap) = ArrangePresentation(content, placement, placement.Y);
            if (!placeholderOverlap && !HasUnfittingImage(layout, placement.X, placement.Y)) return true;
            if (surface.Receiver == null && !ImageNoticeFits(placement, placement))
                throw new UiLayoutException(UiLayoutFailure.ClippedContent, "/fallback", "The image fallback notice cannot fit.");
            return false;
        }

        private UiCaptureRect OverlayInterior(UiCaptureRect panel)
        {
            var padding = 24f * layoutScale;
            return panel.Inset(padding, padding, padding, padding);
        }

        private UiCaptureRect OverlayTitle(UiCaptureRect interior)
        {
            var title = MeasureText(surface.Request.Title, UiTextStyle.Title, interior.Width);
            return new UiCaptureRect(interior.X, interior.Y, title.Width, title.Height);
        }

        private UiCaptureRect OverlayBody(UiCaptureRect interior, UiCaptureRect title)
        {
            var body = MeasureText(surface.Body, UiTextStyle.Body, interior.Width);
            return new UiCaptureRect(interior.X, title.Bottom + gap, body.Width, body.Height);
        }

        private UiCaptureRect OverlayContent(UiCaptureRect interior, UiCaptureRect title, UiCaptureRect body)
        {
            var start = (surface.Body.Length == 0 ? title.Bottom : body.Bottom) + gap;
            return new UiCaptureRect(interior.X, start, interior.Width, Math.Max(0f, interior.Bottom - start));
        }

        private float EaseOverlay(float t)
        {
            var tail = t - 1f;
            var value = theme.Descriptor.Motion.Ease == UiThemeEase.OutBack
                ? 1f + 2.70158f * tail * tail * tail + 1.70158f * tail * tail
                : 1f + tail * tail * tail;
            return Math.Clamp(value, 0f, 1f);
        }
    }
}
