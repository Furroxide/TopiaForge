using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        internal UiTreeCapture CaptureStinger(UiCaptureViewport viewport)
        {
            if (viewport == null) throw new ArgumentNullException(nameof(viewport));
            var active = ActiveStinger;
            return active != null ? FakeUiStingerCapture.Capture(this, active, viewport)
                : new UiTreeCapture(new List<UiCapturedNode>(), Accessibility.HighContrast);
        }
    }

    internal static class FakeUiStingerCapture
    {
        internal static bool FitsSupportedViewport(UiStingerRequest request, UiThemeDescriptor theme)
        {
            var halfHeight = 1080f / 2f;
            var cap = UiStingerLayout.HeightBeforeCrosshair(halfHeight * (0.5f - UiStingerLayout.HeightFraction));
            return Arrange(request, theme, UiStingerLayout.MaximumWidth, 1f).Fits(cap);
        }

        internal static UiTreeCapture Capture(FakeUiService service, FakeStinger stinger, UiCaptureViewport viewport)
        {
            var highContrast = service.Accessibility.HighContrast;
            var descriptor = stinger.Theme.Descriptor;
            var scheme = descriptor.SelectScheme(hud: true, highContrast);
            var pair = scheme.GetPair(stinger.Request.Role);
            var paint = new UiCaptureNodePaint(pair.On, pair.Fill);
            var layoutScale = viewport.ScreenScale * service.Accessibility.UiScale;
            var scale = viewport.ScreenScale * Math.Max(1f, service.Accessibility.UiScale);
            var safeMargin = 18f * layoutScale;
            var maxWidth = Math.Min(UiStingerLayout.MaximumWidth * scale, viewport.Width - 2f * safeMargin);
            var layout = Arrange(stinger.Request, stinger.Typography, maxWidth, scale);
            var centerY = viewport.Height * UiStingerLayout.HeightFraction;
            var maxHeight = UiStingerLayout.HeightBeforeCrosshair((viewport.Height / 2f - centerY) / scale) * scale;
            var panel = new UiCaptureRect((viewport.Width - layout.Width) / 2f, centerY - layout.Height / 2f,
                layout.Width, layout.Height);
            var elapsed = service.ClockTime - stinger.ShownAt;
            var state = elapsed >= stinger.Request.DurationSeconds ? "exiting" :
                elapsed < service.MotionSeconds(descriptor.Motion.BaseMs) ? "entering" : "shown";
            var decoration = UiLayoutDecoration.ResolveStinger(stinger.Request, descriptor, stinger.Declared,
                new UiLayoutExtent(layout.Width / layoutScale, layout.Height / layoutScale), highContrast);
            var panelPaint = new UiCaptureNodePaint(null, pair.Fill, (float)decoration.CornerRadius * layoutScale,
                (float)decoration.BorderWidth * layoutScale, (float)decoration.ShadowX * layoutScale,
                (float)decoration.ShadowY * layoutScale, styleId: decoration.StyleId);
            var announcement = stinger.Request.Subtitle.Length == 0
                ? stinger.Request.Title : stinger.Request.Title + ". " + stinger.Request.Subtitle;
            var flags = UiCaptureNodeFlags.RequiresLabel;
            if (!layout.Fits(maxHeight) || panel.Y < safeMargin) flags |= UiCaptureNodeFlags.InvalidLayout;
            var nodes = new List<UiCapturedNode>
            {
                new UiCapturedNode("/", null, "Stinger", panel,
                    new UiCaptureNodeContent(string.Empty, announcement, 0, 0, flags, state: state),
                    panelPaint, descriptor.Id, stinger.Theme.UsedFallback, true, false, false, announcement)
            };

            var paddingX = UiStingerLayout.PaddingX * scale;
            var top = panel.Y + UiStingerLayout.PaddingY * scale;
            if (stinger.Request.Glyph is { } glyph)
            {
                var size = UiStingerLayout.GlyphSize * scale;
                var bounds = new UiCaptureRect(panel.X + paddingX, top + (layout.RowHeight - size) / 2f, size, size);
                nodes.Add(new UiCapturedNode("/glyph", null, "StingerGlyph", bounds,
                    new UiCaptureNodeContent(string.Empty, glyph.ToString(), 0f, 0f,
                        UiCaptureNodeFlags.SemanticMark), paint, descriptor.Id, stinger.Theme.UsedFallback, true, false, false));
            }

            AddText("/title", "StingerTitle", stinger.Request.Title, UiTextStyle.Banner, layout.Title,
                panel.X + paddingX + layout.Leading, top);
            if (stinger.Request.Subtitle.Length != 0)
                AddText("/subtitle", "StingerSubtitle", stinger.Request.Subtitle, UiTextStyle.Title, layout.Subtitle,
                    panel.X + paddingX, top + layout.RowHeight + UiStingerLayout.LineGap * scale);

            return new UiTreeCapture(nodes, highContrast, usesDeclaredStingerFaces: stinger.UsesDeclaredFaces);

            void AddText(string path, string kind, string raw, UiTextStyle style, FakeUiTreeCapture.Size text, float x, float y)
            {
                var role = stinger.Typography.GetText(style);
                var height = role.ReferenceSize * scale;
                var visible = role.IsUppercase ? raw.ToUpperInvariant() : raw;
                var metrics = stinger.UsesDeclaredFaces ? stinger.Typography : UiThemePackParser.Default;
                var measured = service.ResolveTextMetrics(metrics, metrics.GetText(style).Font).PredictHeight(height);
                var bounds = new UiCaptureRect(x, y, text.Width, text.Height);
                nodes.Add(new UiCapturedNode(path, null, kind, bounds,
                    new UiCaptureNodeContent(visible, raw, measured, height, UiCaptureNodeFlags.RequiresLabel),
                    paint, descriptor.Id, stinger.Theme.UsedFallback, true, !panel.Contains(bounds), false));
            }
        }

        private static StingerLayout Arrange(UiStingerRequest request, UiThemeDescriptor descriptor, float maxWidth, float scale)
        {
            var glyph = request.Glyph.HasValue;
            var leading = glyph ? (UiStingerLayout.GlyphSize + UiStingerLayout.GlyphGap) * scale : 0f;
            var padding = 2f * UiStingerLayout.PaddingX * scale;
            var title = FakeUiTreeCapture.MeasurePlainText(request.Title, UiTextStyle.Banner, maxWidth, descriptor, scale, wrap: false);
            var subtitle = FakeUiTreeCapture.MeasurePlainText(request.Subtitle, UiTextStyle.Title, maxWidth, descriptor, scale, wrap: false);
            var width = Math.Max(0, Math.Min(maxWidth, MathF.Ceiling(Math.Max(title.Width + leading, subtitle.Width)) + padding + 2f * scale));
            var titleWidth = UiStingerLayout.TitleWidth(width / scale, glyph) * scale;
            title = FakeUiTreeCapture.MeasurePlainText(request.Title, UiTextStyle.Banner, titleWidth, descriptor, scale);
            subtitle = FakeUiTreeCapture.MeasurePlainText(request.Subtitle, UiTextStyle.Title, width - padding, descriptor, scale);
            var rowHeight = Math.Max(title.Height, glyph ? UiStingerLayout.GlyphSize * scale : 0f);
            var gap = request.Subtitle.Length != 0 ? UiStingerLayout.LineGap * scale : 0f;
            var height = rowHeight + gap + subtitle.Height + 2f * UiStingerLayout.PaddingY * scale;
            return new StingerLayout(width, height, leading, rowHeight, title, subtitle);
        }

        private readonly struct StingerLayout
        {
            internal StingerLayout(float width, float height, float leading, float rowHeight,
                FakeUiTreeCapture.Size title, FakeUiTreeCapture.Size subtitle)
            {
                Width = width;
                Height = height;
                Leading = leading;
                RowHeight = rowHeight;
                Title = title;
                Subtitle = subtitle;
            }

            internal float Width { get; }
            internal float Height { get; }
            internal float Leading { get; }
            internal float RowHeight { get; }
            internal FakeUiTreeCapture.Size Title { get; }
            internal FakeUiTreeCapture.Size Subtitle { get; }

            internal bool Fits(float height) =>
                Title.Lines <= UiStingerLayout.MaximumLines && Subtitle.Lines <= UiStingerLayout.MaximumLines && Height <= height &&
                Title.Width + Leading <= Width && Subtitle.Width <= Width;
        }
    }
}
