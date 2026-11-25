using System;
using System.Collections.Generic;
using System.Globalization;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private readonly FakeUiService service;
        private readonly FakeUiSurface surface;
        private readonly FakeUiThemeResolution theme;
        private readonly UiThemeScheme scheme;
        private readonly UiAccessibilityPreferences preferences;
        private readonly UiCaptureViewport viewport;
        private readonly UiCaptureControlState controlState;
        private readonly float layoutScale;
        private readonly float readableScale;
        private readonly float gap;
        private readonly bool useHudScheme;
        private readonly List<UiCapturedNode> nodes = new List<UiCapturedNode>();

        internal FakeUiTreeCapture(FakeUiService service, FakeUiSurface surface, UiCaptureViewport viewport,
            UiCaptureControlState controlState, bool? hudScheme)
        {
            this.service = service;
            this.surface = surface;
            this.viewport = viewport;
            this.controlState = controlState;
            preferences = service.Accessibility;
            theme = service.ResolveTheme(surface.Request.Theme);
            useHudScheme = hudScheme ?? surface.Request.Kind == UiSurfaceKind.Hud;
            scheme = theme.Descriptor.SelectScheme(useHudScheme, preferences.HighContrast);
            layoutScale = viewport.ScreenScale * preferences.UiScale;
            readableScale = viewport.ScreenScale * Math.Max(1f, preferences.UiScale);
            gap = 8f * layoutScale;
        }

        internal UiTreeCapture Capture(float scrollOffset)
        {
            var bounds = SurfaceBounds();
            var screen = new UiCaptureRect(0, 0, viewport.Width, viewport.Height);
            var clip = bounds.Intersect(screen);
            var surfacePair = scheme.GetPair(UiPaintRole.Surface);
            Add("/", surface.Id, surface.Request.Kind.ToString(), bounds, clip, EmptyContent,
                new UiCaptureNodePaint(null, surfacePair.Fill));

            var padding = 12f * layoutScale;
            var interior = bounds.Inset(padding, padding, padding, padding);
            var titleHeight = TextSize(UiTextStyle.Heading);
            var title = new UiCaptureRect(interior.X, interior.Y, interior.Width, titleHeight);
            Text("/title", null, "SurfaceTitle", title, clip, surface.Request.Title, UiTextStyle.Heading, UiTone.Neutral,
                UiCaptureNodeFlags.RequiresLabel, surface.Request.Kind == UiSurfaceKind.Hud ? UiPaintRole.Surface : UiPaintRole.Raised);
            var bodyClip = interior.Inset(0, titleHeight + gap, 12f * layoutScale, 0).Intersect(clip);
            var bodyLayout = bodyClip.Inset(8f * layoutScale, 0, 8f * layoutScale, 0);
            var top = bodyClip.Y - (scrollOffset * layoutScale);
            var body = surface.Body;
            if (body.Length > 0)
            {
                var bodySize = MeasureText(body, UiTextStyle.Body, bodyLayout.Width);
                var bodyBounds = new UiCaptureRect(bodyLayout.X, top, bodySize.Width, bodySize.Height);
                Text("/body", null, "SurfaceBody", bodyBounds, bodyClip, body, UiTextStyle.Body, UiTone.Neutral,
                    UiCaptureNodeFlags.ScrollableClip);
                top += bodySize.Height + gap;
            }

            if (surface.Content != null)
            {
                var size = Measure(surface.Content, bodyLayout.Width);
                PaintNode(surface.Content, "/0", new UiCaptureRect(bodyLayout.X, top, bodyLayout.Width, size.Height), bodyClip);
            }

            return new UiTreeCapture(nodes, preferences.HighContrast, ControlHeight);
        }

        private void PaintNode(UiNode node, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            if (node.Id != null) path += ":" + Uri.EscapeDataString(node.Id);
            switch (node)
            {
                case UiText text:
                    Text(path, text.Id, nameof(UiText), bounds, clip, text.Text, text.Style, text.Tone,
                        UiCaptureNodeFlags.ScrollableClip);
                    return;
                case UiCountdown countdown:
                    Text(path, countdown.Id, nameof(UiCountdown), bounds, clip, CountdownText(countdown),
                        UiTextStyle.Body, countdown.Tone, UiCaptureNodeFlags.ScrollableClip);
                    return;
                case UiProgress progress:
                    PaintProgress(progress, path, bounds, clip);
                    return;
                case UiLayoutNode layout:
                    Add(path, null, node.GetType().Name, bounds, clip, EmptyContent, default);
                    PaintLinear(layout, path, bounds, clip);
                    return;
                case UiSplitPane split:
                    Add(path, null, nameof(UiSplitPane), bounds, clip, EmptyContent, default);
                    PaintSplit(split, path, bounds, clip);
                    return;
                case UiScroll scroll:
                    Add(path, null, nameof(UiScroll), bounds, clip, EmptyContent, default);
                    var view = bounds.Inset(0, 0, 12f * layoutScale, 0);
                    var size = Measure(scroll.Content, view.Width);
                    PaintNode(scroll.Content, path + "/0", new UiCaptureRect(view.X, view.Y, view.Width, size.Height), view.Intersect(clip));
                    return;
                default:
                    PaintControl(node, path, bounds, clip);
                    return;
            }
        }

        private void PaintLinear(UiLayoutNode layout, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var row = layout is UiRow;
            var x = bounds.X;
            var y = bounds.Y;
            for (var index = 0; index < layout.Children.Count; index++)
            {
                var child = layout.Children[index];
                var size = Measure(child, bounds.Width, wrap: !row);
                if (row) y = bounds.Y + (bounds.Height - size.Height) / 2f;
                var width = row ? size.Width : bounds.Width;
                PaintNode(child, ChildPath(path, index), new UiCaptureRect(x, y, width, size.Height), clip);
                if (row) x += width + gap;
                else y += size.Height + gap;
            }
        }

        private void PaintSplit(UiSplitPane split, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var splitGap = 12f * layoutScale;
            if (split.Orientation == UiSplitOrientation.Horizontal)
            {
                var width = Math.Max(0f, bounds.Width - splitGap);
                var firstWidth = width * split.PrimaryFraction;
                var secondWidth = width - firstWidth;
                PaintNode(split.Primary, path + "/0", new UiCaptureRect(bounds.X, bounds.Y, firstWidth,
                    Measure(split.Primary, firstWidth).Height), clip);
                PaintNode(split.Secondary, path + "/1", new UiCaptureRect(bounds.X + firstWidth + splitGap, bounds.Y,
                    secondWidth, Measure(split.Secondary, secondWidth).Height), clip);
                return;
            }

            var first = Measure(split.Primary, bounds.Width).Height;
            var second = Measure(split.Secondary, bounds.Width).Height;
            var height = bounds.Height - splitGap;
            var firstHeight = Math.Max(first, height * split.PrimaryFraction);
            PaintNode(split.Primary, path + "/0", new UiCaptureRect(bounds.X, bounds.Y, bounds.Width, first), clip);
            PaintNode(split.Secondary, path + "/1", new UiCaptureRect(bounds.X, bounds.Y + firstHeight + splitGap,
                bounds.Width, second), clip);
        }

        private UiCaptureRect SurfaceBounds()
        {
            var surfaceRequest = surface.Request;
            var margin = 18f * layoutScale;
            var availableWidth = Math.Max(0f, viewport.Width - 2f * margin);
            var availableHeight = Math.Max(0f, viewport.Height - 2f * margin);
            if (surfaceRequest.Kind == UiSurfaceKind.FullscreenTool)
                return new UiCaptureRect(margin, margin, availableWidth, availableHeight);

            var width = Math.Min(surfaceRequest.Width * layoutScale, availableWidth);
            var height = Math.Min(surfaceRequest.Height * layoutScale, availableHeight);
            var centerX = (viewport.Width - width) / 2f;
            var centerY = (viewport.Height - height) / 2f;
            if (surfaceRequest.Kind != UiSurfaceKind.Hud) return new UiCaptureRect(centerX, centerY, width, height);

            var anchor = surfaceRequest.HudAnchor == UiHudAnchor.Default
                ? surfaceRequest.HudVisibility == UiHudVisibility.Always ? UiHudAnchor.Left : UiHudAnchor.BottomLeft
                : surfaceRequest.HudAnchor;
            var right = viewport.Width - margin - width;
            if (anchor == UiHudAnchor.Bottom || anchor == UiHudAnchor.BottomLeft || anchor == UiHudAnchor.BottomRight)
                height = Math.Min(height, Math.Max(0, viewport.Height * 0.88f - 2f * margin));
            var bottom = viewport.Height * 0.88f - margin - height;
            return anchor switch
            {
                UiHudAnchor.TopLeft => new UiCaptureRect(margin, margin, width, height),
                UiHudAnchor.Top => new UiCaptureRect(centerX, margin, width, height),
                UiHudAnchor.TopRight => new UiCaptureRect(right, margin, width, height),
                UiHudAnchor.Left => new UiCaptureRect(margin, centerY, width, height),
                UiHudAnchor.Right => new UiCaptureRect(right, centerY, width, height),
                UiHudAnchor.BottomLeft => new UiCaptureRect(margin, bottom, width, height),
                UiHudAnchor.Bottom => new UiCaptureRect(centerX, bottom, width, height),
                UiHudAnchor.BottomRight => new UiCaptureRect(right, bottom, width, height),
                _ => throw new InvalidOperationException("The captured HUD anchor is invalid.")
            };
        }

        private void Add(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect clip,
            UiCaptureNodeContent content, UiCaptureNodePaint paint)
        {
            if ((content.Flags & UiCaptureNodeFlags.ScrollableClip) != 0 &&
                (bounds.X < clip.X - 0.001f || bounds.Right > clip.Right + 0.001f))
            {
                content = new UiCaptureNodeContent(content.Text, content.SemanticLabel, content.TextHeight,
                    content.ExpectedTextHeight, content.Flags & ~UiCaptureNodeFlags.ScrollableClip);
            }
            var rendered = surface.IsVisible && bounds.Overlaps(clip);
            nodes.Add(new UiCapturedNode(path, id, kind, bounds, content, paint, theme.Descriptor.Id,
                theme.UsedFallback, rendered, rendered && !clip.Contains(bounds), occluded: false));
        }

        private string CountdownText(UiCountdown countdown)
        {
            var remaining = surface.CapturedCountdownSeconds(countdown).ToString(CultureInfo.InvariantCulture);
            return countdown.Label.Length == 0 ? remaining : countdown.Label + " " + remaining;
        }

        private static string ChildPath(string path, int index) => path + "/" + index.ToString(CultureInfo.InvariantCulture);

        private static readonly UiCaptureNodeContent EmptyContent = new UiCaptureNodeContent(
            string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.None);
    }

    internal enum UiCaptureControlState { Rest, Hovered, Focused, Selected, Pressed, SelectedPressed }
}
