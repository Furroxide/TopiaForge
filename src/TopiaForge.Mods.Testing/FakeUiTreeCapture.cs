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
        private readonly string? overlayState;
        private readonly UiThemeDescriptor? declaredTheme;
        private readonly FakeUiImages images;
        private readonly List<UiCapturedNode> nodes = new List<UiCapturedNode>();
        private UiPaintRole backdrop = UiPaintRole.Surface;
        private bool inheritedDisabled;

        internal FakeUiTreeCapture(FakeUiService service, FakeUiSurface surface, UiCaptureViewport viewport,
            UiCaptureControlState controlState, bool? hudScheme, FakeUiImages? stagedImages = null)
        {
            this.service = service;
            this.surface = surface;
            this.viewport = viewport;
            this.controlState = controlState;
            preferences = service.Accessibility;
            images = stagedImages ?? surface.Images;
            theme = service.ResolveTheme(surface.Request.Theme, surface.Receiver);
            declaredTheme = service.DeclaredTheme(surface.Request.Theme, surface.Receiver);
            if (surface.Receiver == null) declaredTheme ??= UiThemePackParser.Default;
            useHudScheme = hudScheme ?? surface.Request.Kind is UiSurfaceKind.Hud or UiSurfaceKind.Overlay;
            if (surface.Request.Kind == UiSurfaceKind.Overlay) overlayState = surface.OverlayVisualState;
            scheme = theme.Descriptor.SelectScheme(useHudScheme, preferences.HighContrast);
            layoutScale = viewport.ScreenScale * preferences.UiScale;
            readableScale = viewport.ScreenScale * Math.Max(1f, preferences.UiScale);
            gap = 8f * layoutScale;
        }

        internal UiTreeCapture Capture(float scrollOffset)
        {
            if (surface.Request.Kind == UiSurfaceKind.Overlay) return CaptureOverlay(scrollOffset);
            var bounds = SurfaceBounds();
            var screen = new UiCaptureRect(0, 0, viewport.Width, viewport.Height);
            var clip = bounds.Intersect(screen);
            var surfacePair = scheme.GetPair(UiPaintRole.Surface);
            if (surface.Request.Kind == UiSurfaceKind.Hud && surface.Content == null)
            {
                PaintBodyOnlyHud(bounds, clip, surfacePair);
                return new UiTreeCapture(nodes, preferences.HighContrast, ControlHeight);
            }
            Add("/", surface.Id, surface.Request.Kind.ToString(), bounds, clip, EmptyContent,
                new UiCaptureNodePaint(null, surfacePair.Fill));

            var padding = 12f * layoutScale;
            var interior = bounds.Inset(padding, padding, padding, padding);
            var titleHeight = TextSize(UiTextStyle.Heading);
            var title = new UiCaptureRect(interior.X, interior.Y, interior.Width, titleHeight);
            Text("/title", null, "SurfaceTitle", title, clip, surface.Request.Title, UiTextStyle.Heading, UiTone.Neutral,
                UiCaptureNodeFlags.RequiresLabel, surface.Request.Kind == UiSurfaceKind.Hud ? UiPaintRole.Surface : UiPaintRole.Raised);
            if (PaintGraphConnections(bounds, clip, surfacePair.Fill))
                return new UiTreeCapture(nodes, preferences.HighContrast, ControlHeight);
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

            if (surface.Content != null) PaintComposition(surface.Content, bodyLayout, bodyClip, top, scrollOffset);

            return new UiTreeCapture(nodes, preferences.HighContrast, ControlHeight);
        }

        private void PaintLeaf(UiNode node, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            switch (node)
            {
                case UiText text:
                    Text(path, text.Id, nameof(UiText), bounds, clip, text.Text, text.Style, text.Tone,
                        UiCaptureNodeFlags.ScrollableClip);
                    return;
                case UiCountdown { UsesStatusFormat: true } timer:
                    PaintStatusCountdown(timer, path, bounds, clip);
                    return;
                case UiCountdown countdown:
                    PaintLegacyCountdown(countdown, path, bounds, clip);
                    return;
                case UiProgress { Thresholds: not null } meter:
                    PaintStatusProgress(meter, path, bounds, clip);
                    return;
                case UiProgress progress:
                    PaintProgress(progress, path, bounds, clip);
                    return;
                case UiStatBar stat:
                    PaintStatBar(stat, path, bounds, clip);
                    return;
                case UiKeyGlyph glyph:
                    PaintKeyGlyph(glyph, path, bounds, clip);
                    return;
                case UiActionSlot slot:
                    PaintActionSlot(slot, path, bounds, clip);
                    return;
                case UiIcon or UiImage or UiBadge or UiPips:
                    PaintArt(node, path, bounds, clip);
                    return;
                default:
                    PaintControl(node, path, bounds, clip);
                    return;
            }
        }

        private UiCaptureRect SurfaceBounds()
        {
            var surfaceRequest = surface.Request;
            if (surfaceRequest.Kind == UiSurfaceKind.Hud) return CaptureRect(HudBounds(surface));
            var margin = 18f * layoutScale;
            var availableWidth = Math.Max(0f, viewport.Width - 2f * margin);
            var availableHeight = Math.Max(0f, viewport.Height - 2f * margin);
            if (surfaceRequest.Kind == UiSurfaceKind.FullscreenTool)
                return new UiCaptureRect(margin, margin, availableWidth, availableHeight);

            var width = Math.Min(surfaceRequest.Width * layoutScale, availableWidth);
            var height = Math.Min(surfaceRequest.Height * layoutScale, availableHeight);
            var centerX = (viewport.Width - width) / 2f;
            var centerY = (viewport.Height - height) / 2f;
            return new UiCaptureRect(centerX, centerY, width, height);
        }

        private void Add(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect clip,
            UiCaptureNodeContent content, UiCaptureNodePaint paint, bool usedFallback = false, bool? render = null, float? fillOpacity = null)
        {
            if ((content.Flags & UiCaptureNodeFlags.ScrollableClip) != 0 &&
                (bounds.X < clip.X - 0.001f || bounds.Right > clip.Right + 0.001f))
            {
                content = content.WithFlags(content.Flags & ~UiCaptureNodeFlags.ScrollableClip);
            }
            var shown = surface.Request.Kind == UiSurfaceKind.Overlay ? overlayState != null : surface.IsVisible;
            var rendered = render ?? (shown && bounds.Overlaps(clip));
            nodes.Add(new UiCapturedNode(path, id, kind, bounds, content, paint, theme.Descriptor.Id,
                usedFallback || theme.UsedFallback, rendered, rendered && !clip.Contains(bounds), occluded: false, fillOpacity: fillOpacity));
        }

        private string CountdownText(UiCountdown countdown) =>
            UiCountdownFormatting.Text(countdown.Label, surface.CapturedCountdownSeconds(countdown), countdown.Format);

        private static string ChildPath(string path, int index) => path + "/" + index.ToString(CultureInfo.InvariantCulture);

        private static readonly UiCaptureNodeContent EmptyContent = new UiCaptureNodeContent(
            string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.None);
    }

    internal enum UiCaptureControlState { Rest, Hovered, Focused, Selected, Pressed, SelectedPressed }
}
