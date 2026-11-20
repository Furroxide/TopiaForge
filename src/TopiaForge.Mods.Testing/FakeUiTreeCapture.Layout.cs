using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private static readonly UiCaptureViewport ReferenceViewport = new UiCaptureViewport(1920, 1080);
        internal static void VerifyComposition(FakeUiService service, FakeUiSurface surface, UiNode? content)
        {
            if (content == null) return;
            new FakeUiTreeCapture(service, surface, ReferenceViewport, UiCaptureControlState.Rest, null).VerifyComposition(content);
        }

        private void VerifyComposition(UiNode content)
        {
            var bounds = SurfaceBounds();
            var padding = 12f * layoutScale;
            var interior = bounds.Inset(padding, padding, 2f * padding, padding);
            var bodyClip = interior.Inset(0f, TextSize(UiTextStyle.Heading) + gap, 0f, 0f);
            var body = bodyClip.Inset(gap, 0f, gap, 0f);
            var origin = body.Y;
            if (surface.Body.Length != 0) origin += MeasureText(surface.Body, UiTextStyle.Body, body.Width).Height + gap;
            _ = Arrange(content, body, origin);
        }

        private UiLayoutResult Arrange(UiNode content, UiCaptureRect body, float top)
        {
            var width = body.Width / (double)layoutScale;
            var height = surface.Request.Kind == UiSurfaceKind.Hud
                ? Math.Max(0, (body.Bottom - top) / (double)layoutScale)
                : double.PositiveInfinity;
            return UiLayoutEngine.Arrange(content, new UiLayoutConstraints(width, width, 0d, height),
                MeasurePlacement, layoutScale, preferences.HighContrast);
        }

        private UiLayoutLeafMeasurement MeasurePlacement(UiNode node, UiLayoutConstraints limits, bool disabled)
        {
            if (node is UiCard || node is UiBox)
            {
                var extent = new UiLayoutExtent(limits.MinWidth, limits.MinHeight);
                var decoration = ResolveDecoration(node, extent);
                return new UiLayoutLeafMeasurement(extent, UiLayoutPaintKind.Painter, decoration.ShadowOutsets);
            }

            var pixels = MeasureLeaf(node, (float)(limits.MaxWidth * layoutScale));
            var size = new UiLayoutExtent(ToReferenceUnits(pixels.Width, limits.MaxWidth),
                ToReferenceUnits(pixels.Height, limits.MaxHeight));
            var interactive = UiLayoutTree.IsInteractive(node);
            var shadows = node is UiButton button && button.Style != UiButtonStyle.Ghost && !disabled && !preferences.HighContrast
                ? new UiLayoutOutsets(3d, 0d, 0d, 4d) : default;
            var focus = interactive && !disabled ? (preferences.HighContrast ? 5d : 4d) : 0d;
            return new UiLayoutLeafMeasurement(size, UiLayoutPaintKind.None, shadows, new UiLayoutOutsets(focus, focus, focus, focus),
                scrollLineHeight: node is UiText text ? TextSize(text.Style) / (double)layoutScale : null);
        }

        private double ToReferenceUnits(float pixels, double limit)
        {
            var units = pixels / (double)layoutScale;
            return units > limit && units - limit < 0.0001d ? limit : units;
        }

        private void PaintComposition(UiNode content, UiCaptureRect body, UiCaptureRect clip, float top, float scrollOffset)
        {
            var origin = surface.Request.Kind == UiSurfaceKind.Hud ? top + (scrollOffset * layoutScale) : top;
            UiLayoutResult layout;
            try { layout = Arrange(content, body, origin); }
            catch (UiLayoutException failure)
            {
                PaintLayoutFailure(body, clip, failure);
                return;
            }

            var backgrounds = new Dictionary<UiLayoutPlacement, UiPaintRole>(layout.Placements.Count);
            foreach (var placement in layout.Placements)
            {
                backdrop = placement.Parent == null ? UiPaintRole.Surface : backgrounds[placement.Parent];
                inheritedDisabled = placement.Disabled;
                var node = placement.Node;
                var path = placement.Path;
                if (node.Id != null) path += ":" + Uri.EscapeDataString(node.Id);
                var bounds = ScreenRect(placement.Bounds, body.X, origin);
                var boundsClip = clip;
                for (var ancestor = placement.Parent; ancestor != null; ancestor = ancestor.Parent)
                {
                    if (ancestor.Node is UiScroll or UiStack)
                        boundsClip = boundsClip.Intersect(ScreenRect(ancestor.Bounds, body.X, origin));
                }
                switch (node)
                {
                    case UiCard:
                    case UiBox:
                        PaintDecoration(placement, path, bounds, boundsClip);
                        break;
                    case UiDivider:
                        var stroke = ScreenRect(placement.PaintBounds, body.X, origin);
                        Add(path, node.Id, nameof(UiDivider), stroke, boundsClip, EmptyContent,
                            new UiCaptureNodePaint(scheme.ResolveBoundary(backdrop), scheme.GetPair(backdrop).Fill,
                                borderWidth: stroke.Height));
                        break;
                    case UiLayoutNode:
                    case UiScroll:
                    case UiSplitPane:
                    case UiPadding:
                    case UiSizedBox:
                    case UiExpanded:
                    case UiSpacer:
                    case UiPositioned:
                        Add(path, node.Id, node.GetType().Name, bounds, boundsClip, EmptyContent, default);
                        break;
                    default:
                        PaintLeaf(node, path, bounds, boundsClip);
                        break;
                }

                backgrounds.Add(placement, backdrop);
            }
        }

        private void PaintDecoration(UiLayoutPlacement placement, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var node = placement.Node;
            var style = ResolveDecoration(node, new UiLayoutExtent(placement.Bounds.Width, placement.Bounds.Height));
            var pair = scheme.GetPair(style.Fill);
            var paint = new UiCaptureNodePaint(null, pair.Fill, (float)(style.CornerRadius * layoutScale),
                (float)(style.BorderWidth * layoutScale), (float)(style.ShadowX * layoutScale),
                (float)(style.ShadowY * layoutScale), style.StyleId);
            var flags = placement.Disabled ? UiCaptureNodeFlags.Disabled : UiCaptureNodeFlags.None;
            var selected = node is UiCard { Selected: true };
            if (selected) flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            if (node is UiBox) flags |= UiCaptureNodeFlags.Decorative;
            Add(path, node.Id, node.GetType().Name, bounds, clip,
                new UiCaptureNodeContent(string.Empty, selected ? "Selected" : string.Empty, 0f, 0f, flags), paint);
            if (node is UiCard) backdrop = style.Fill;
        }

        private UiLayoutDecoration ResolveDecoration(UiNode node, UiLayoutExtent size) =>
            UiLayoutDecoration.Resolve(node, theme.Descriptor, declaredTheme, size, preferences.HighContrast);

        private void PaintLayoutFailure(UiCaptureRect body, UiCaptureRect clip, UiLayoutException failure)
        {
            var hud = surface.Request.Kind == UiSurfaceKind.Hud;
            if (hud) nodes.Clear();
            var clipped = failure.Failure == UiLayoutFailure.ClippedContent;
            var occluded = failure.Failure == UiLayoutFailure.OccludedContent;
            var flags = UiCaptureNodeFlags.LayoutDiagnostic | (clipped || occluded ? 0 : UiCaptureNodeFlags.InvalidLayout);
            nodes.Add(new UiCapturedNode(failure.Path, null, "LayoutDiagnostic", default,
                new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, flags), default,
                theme.Descriptor.Id, usedFallback: true, isRendered: false, clipped, occluded));

            if (hud)
            {
                PaintHudLayoutNotice();
                return;
            }

            var kit = UiThemePackParser.Default;
            var pair = kit.SelectScheme(useHudScheme, preferences.HighContrast).GetPair(UiPaintRole.Surface);
            var size = MeasureText(UiHudLayoutNotice.Text, UiTextStyle.Body, body.Width, descriptor: kit);
            var bounds = new UiCaptureRect(body.X, body.Y, size.Width, size.Height);
            PaintNoticeText("/fallback", "LayoutFallback", UiHudLayoutNotice.Text, UiTextStyle.Body, bounds, pair,
                bounds.Overlaps(clip), clipped: !clip.Contains(bounds));
        }

        private UiCaptureRect ScreenRect(UiLayoutRect bounds, float left, float top) => new UiCaptureRect(
            left + (float)(bounds.X * layoutScale), top + (float)(bounds.Y * layoutScale),
            (float)(bounds.Width * layoutScale), (float)(bounds.Height * layoutScale));

        private bool Enabled(bool authored) => authored && !inheritedDisabled;
    }
}
