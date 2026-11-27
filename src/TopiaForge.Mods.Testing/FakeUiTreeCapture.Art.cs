using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private const float GlyphFloor = 18f;
        private const float ArtPadding = 8f;
        private const float StatusGap = 4f;

        private float ArtGlyphSize => GlyphFloor * readableScale;
        private float ArtPaddingPixels => ArtPadding * layoutScale;

        private UiLayoutLeafMeasurement MeasureArt(UiNode node, UiLayoutConstraints limits)
        {
            if (node is UiImage image) return MeasureImage(image, protectLabel: true);
            if (node is UiIcon icon)
            {
                var size = icon.Size * (icon.Decorative ? 1f : readableScale / layoutScale);
                return new UiLayoutLeafMeasurement(new UiLayoutExtent(size, size),
                    icon.Decorative ? UiLayoutPaintKind.Painter : UiLayoutPaintKind.Protected,
                    localPaintBounds: new UiLayoutRect(0, 0, size, size));
            }

            var availableWidth = (float)(limits.MaxWidth * layoutScale);
            Size pixels;
            if (node is UiBadge badge)
            {
                var symbol = UiBadgePresentation.Glyph(badge, preferences.HighContrast).HasValue ? ArtGlyphSize : 0f;
                var insets = 2 * ArtPaddingPixels + symbol + (symbol > 0 ? StatusGap * layoutScale : 0f);
                var label = MeasureStatusLabel(badge.Text, availableWidth - insets);
                pixels = new Size(insets + label.Width, 2 * ArtPaddingPixels + Math.Max(symbol, label.Height));
            }
            else
            {
                var pips = (UiPips)node;
                var track = new UiPipTrack(pips, (float)limits.MaxWidth);
                var label = MeasureStatusLabel(pips.DisplayLabel, availableWidth);
                pixels = new Size(Math.Max(track.Width * layoutScale, label.Width), label.Height + (StatusGap + track.Height) * layoutScale);
            }

            var sizeInUnits = new UiLayoutExtent(ToReferenceUnits(pixels.Width, limits.MaxWidth),
                ToReferenceUnits(pixels.Height, limits.MaxHeight));
            var decoration = ResolveDecoration(node, sizeInUnits);
            return new UiLayoutLeafMeasurement(sizeInUnits, UiLayoutPaintKind.Protected, decoration.ShadowOutsets);
        }

        private UiLayoutLeafMeasurement MeasureImage(UiImage image, bool protectLabel)
        {
            var art = images[image];
            var box = new UiCaptureRect(0, 0, art.Width * layoutScale, art.Height * layoutScale);
            var kind = protectLabel && !image.Decorative && !art.IsReady && PlaceholderFits(image, box)
                ? UiLayoutPaintKind.Protected : UiLayoutPaintKind.Image;
            return new UiLayoutLeafMeasurement(new UiLayoutExtent(art.Width, art.Height), kind,
                localPaintBounds: new UiLayoutRect(0, 0, art.Width, art.Height));
        }

        private Size MeasureStatusLabel(string text, float width)
        {
            if (width <= 0f) throw new ArgumentException("The status label needs a positive allocation.", nameof(width));
            var label = MeasureText(text, UiTextStyle.Label, width);
            if (label.Width <= 0f || label.Height <= 0f)
                throw new ArgumentException("The status label has no readable bounds.", nameof(text));
            return label;
        }

        private bool PlaceholderFits(UiImage image, UiCaptureRect bounds)
        {
            var interior = bounds.Inset(ArtPaddingPixels, ArtPaddingPixels, ArtPaddingPixels, ArtPaddingPixels);
            if (interior.Width <= 0f || interior.Height <= 0f) return false;
            var text = MeasureText(image.SemanticLabel, UiTextStyle.Body, interior.Width);
            return text.Width <= interior.Width + 0.001f && text.Height <= interior.Height + 0.001f;
        }

        private bool HasUnfittingImage(UiLayoutResult layout, float left, float top)
        {
            foreach (var placement in layout.Placements)
            {
                if (!VisibleTabPage(placement) || placement.Node is not UiImage { Decorative: false } image) continue;
                var art = images[image];
                if (!art.IsReady && !PlaceholderFits(image,
                    ScreenRect(UiImageProjection.FixedBox(placement.Bounds, art.Width, art.Height), left, top)))
                    return true;
            }
            return false;
        }

        private bool ImageNoticeFits(UiCaptureRect body, UiCaptureRect clip) => clip.Contains(ImageNoticeBounds(body));

        private UiCaptureRect ImageNoticeBounds(UiCaptureRect body)
        {
            var text = MeasureText(UiImageNotice.Text, UiTextStyle.Body, body.Width, descriptor: UiThemePackParser.Default);
            return new UiCaptureRect(body.X, body.Y, text.Width, text.Height);
        }

        private void PaintImageFallback(UiLayoutResult layout, UiCaptureRect body, UiCaptureRect clip, float origin)
        {
            // Keep the shell and title. A readable notice takes the place of the body and controls.
            if (nodes.Count > 2) nodes.RemoveRange(2, nodes.Count - 2);
            foreach (var placement in layout.Placements)
            {
                if (!VisibleTabPage(placement) || placement.Node is not UiImage image) continue;
                var art = images[image];
                if (art.IsReady) continue;
                var bounds = ScreenRect(UiImageProjection.FixedBox(placement.Bounds, art.Width, art.Height), body.X, origin);
                var path = placement.Path + (image.Id == null ? string.Empty : ":" + Uri.EscapeDataString(image.Id));
                Add(path, image.Id, nameof(UiImage), bounds, clip, ImageContent(image, missing: true), default,
                    usedFallback: true, render: false);
            }

            var kit = UiThemePackParser.Default;
            var pair = kit.SelectScheme(useHudScheme, preferences.HighContrast).GetPair(UiPaintRole.Surface);
            var textHeight = kit.GetText(UiTextStyle.Body).ReferenceSize * readableScale;
            var noticeArea = body;
            var fits = ImageNoticeFits(noticeArea, clip);
            if (surface.Request.Kind == UiSurfaceKind.Hud)
            {
                fits = TryLayoutHudImageNotice(pair, out noticeArea);
                if (!fits && surface.IsVisible)
                {
                    PaintQueuedHudNotice();
                    return;
                }
            }
            var boundsOfNotice = fits ? ImageNoticeBounds(noticeArea) : new UiCaptureRect(body.X, body.Y, 0f, 0f);
            var flags = UiCaptureNodeFlags.RequiresLabel;
            if (!fits) flags |= UiCaptureNodeFlags.InvalidLayout | UiCaptureNodeFlags.LayoutDiagnostic;
            nodes.Add(new UiCapturedNode("/fallback", null, "ImageFallback", boundsOfNotice,
                new UiCaptureNodeContent(UiImageNotice.Text, UiImageNotice.Text, textHeight, textHeight, flags),
                new UiCaptureNodePaint(pair.On, pair.Fill), kit.Id, usedFallback: true,
                isRendered: surface.IsVisible && fits, clipped: !fits, occluded: false));
        }

        private bool TryLayoutHudImageNotice(UiThemePair pair, out UiCaptureRect noticeArea)
        {
            noticeArea = default;
            if (HudNoticeBounds() is not { } bounds)
            {
                nodes.RemoveRange(0, 2);
                return false;
            }

            var padding = 12f * layoutScale;
            var kit = UiThemePackParser.Default;
            var title = surface.Request.Title;
            var panel = CaptureRect(bounds);
            var interior = panel.Inset(padding, padding, padding, padding);
            var titleSize = MeasureText(title, UiTextStyle.Heading, interior.Width, descriptor: kit);
            var titleHeight = kit.GetText(UiTextStyle.Heading).ReferenceSize * readableScale;
            nodes[0] = new UiCapturedNode("/", surface.Id, nameof(UiSurfaceKind.Hud), panel, EmptyContent,
                new UiCaptureNodePaint(null, pair.Fill), kit.Id, usedFallback: true, surface.IsVisible, clipped: false, occluded: false);
            nodes[1] = new UiCapturedNode("/title", null, "SurfaceTitle",
                new UiCaptureRect(interior.X, interior.Y, titleSize.Width, titleSize.Height),
                new UiCaptureNodeContent(title, title, titleHeight, titleHeight, UiCaptureNodeFlags.RequiresLabel),
                new UiCaptureNodePaint(pair.On, pair.Fill), kit.Id, usedFallback: true, surface.IsVisible, clipped: false, occluded: false);
            noticeArea = interior.Inset(0f, titleSize.Height + gap, 0f, 0f);
            return true;
        }

        private bool TryMeasureHudImageNotice(UiLayoutRect authored, IReadOnlyList<UiLayoutRect> occupied,
            out UiLayoutRect panel)
        {
            var padding = 12f * layoutScale;
            var bodyClip = CaptureRect(authored).Inset(padding, padding + TextSize(UiTextStyle.Heading) + gap, 2f * padding, padding);
            var current = ImageNoticeFits(bodyClip.Inset(gap, 0f, gap, 0f), bodyClip) ? authored : default;
            var safe = HudSafeArea();
            return UiHudNoticePlacement.TryPlaceMeasured(safe, current, HudDockOrigin(surface, safe),
                UiImageNotice.RemoteHudWidth * layoutScale, occupied, UiHudViewport.SlotGap * layoutScale,
                width => MeasureHudNoticeHeight(UiImageNotice.Text, width), out panel, out _,
                preferredHeight: UiImageNotice.RemoteHudHeight * layoutScale);
        }

        private void PaintArt(UiNode node, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            switch (node)
            {
                case UiIcon icon:
                    var size = icon.Size * (icon.Decorative ? layoutScale : readableScale);
                    var square = new UiCaptureRect(bounds.X, bounds.Y, Math.Min(size, bounds.Width), Math.Min(size, bounds.Height));
                    var flags = ArtFlags(icon.Decorative) | (icon.Decorative ? 0 : UiCaptureNodeFlags.SemanticMark);
                    Add(path, icon.Id, nameof(UiIcon), square, clip,
                        new UiCaptureNodeContent(string.Empty, icon.SemanticLabel, 0f, 0f, flags),
                        new UiCaptureNodePaint(scheme.ResolveForeground(icon.Tone, backdrop), scheme.GetPair(backdrop).Fill));
                    break;
                case UiImage image: PaintImage(image, path, bounds, clip); break;
                case UiBadge badge: PaintBadge(badge, path, bounds, clip); break;
                case UiPips pips: PaintPips(pips, path, bounds, clip); break;
            }
        }

        private void PaintImage(UiImage image, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var art = images[image];
            if (art.IsReady && art.Frame is UiImageFrame frame)
            {
                Add(path, image.Id, nameof(UiImage), bounds, clip, ImageContent(image, missing: false), default);
                var draw = frame.Place(bounds.Width, bounds.Height);
                var sprite = new UiCaptureRect(bounds.X + draw.X, bounds.Y + draw.Y, draw.Width, draw.Height);
                var pair = image.Tint is UiTone tint
                    ? new UiCaptureNodePaint(scheme.ResolveForeground(tint, backdrop), scheme.GetPair(backdrop).Fill)
                    : default;
                var spriteContent = image.Tint.HasValue && !image.Decorative ? ReadableMark : DecorativeArt;
                Add(path + "/sprite", null, "ImageSprite", sprite, clip, spriteContent, pair);
                return;
            }

            var placeholder = scheme.GetPair(UiPaintRole.Sunken);
            Add(path, image.Id, nameof(UiImage), bounds, clip, ImageContent(image, missing: true),
                new UiCaptureNodePaint(null, placeholder.Fill, cornerRadius: 0f, borderWidth: StatusBoundaryWidth),
                usedFallback: true);
            Add(path + "/edge", null, "ImageBoundary", bounds, clip, image.Decorative ? DecorativeArt : MeaningfulEdge,
                new UiCaptureNodePaint(scheme.ResolveBoundary(backdrop), scheme.GetPair(backdrop).Fill,
                    cornerRadius: 0f, borderWidth: StatusBoundaryWidth), usedFallback: true);
            if (image.Decorative) return;
            var inside = bounds.Inset(ArtPaddingPixels, ArtPaddingPixels, ArtPaddingPixels, ArtPaddingPixels);
            var measured = MeasureText(image.SemanticLabel, UiTextStyle.Body, inside.Width);
            var text = new UiCaptureRect(inside.X, inside.Y, measured.Width, measured.Height);
            Add(path + "/label", null, "ImagePlaceholder", text, clip,
                TextContent(image.SemanticLabel, UiTextStyle.Body, ArtFlags(false)),
                new UiCaptureNodePaint(placeholder.On, placeholder.Fill), usedFallback: true);
        }

        private void PaintBadge(UiBadge badge, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var style = ResolveDecoration(badge, new UiLayoutExtent(bounds.Width / layoutScale, bounds.Height / layoutScale));
            var colors = UiStatusColors.Badge(scheme, badge, backdrop);
            var stroke = Math.Max((float)style.BorderWidth * layoutScale,
                badge.Presentation == UiBadgeStyle.Outlined ? StatusBoundaryWidth : 0f);
            Add(path, badge.Id, nameof(UiBadge), bounds, clip, StatusContent(badge.Text),
                DecorationPaint(style, null, badge.Presentation == UiBadgeStyle.Filled ? colors.Fill : (UiThemeColor?)null,
                    stroke, badge.Style, badge.Role));
            if (stroke > 0f)
            {
                Add(path + "/edge", null, "BadgeBoundary", bounds, clip, MeaningfulEdge,
                    DecorationPaint(style, UiStatusColors.Boundary(scheme, badge.Role, backdrop), scheme.GetPair(backdrop).Fill, stroke));
            }

            var left = ArtPaddingPixels;
            if (UiBadgePresentation.Glyph(badge, preferences.HighContrast) is UiGlyph glyph)
            {
                var symbol = new UiCaptureRect(bounds.X + left, bounds.Y + (bounds.Height - ArtGlyphSize) / 2f, ArtGlyphSize, ArtGlyphSize);
                Add(path + "/glyph:" + glyph, null, "BadgeGlyph", symbol, clip, ReadableMark, new UiCaptureNodePaint(colors.On, colors.Fill));
                left += ArtGlyphSize + StatusGap * layoutScale;
            }
            var measured = MeasureStatusLabel(badge.Text, bounds.Width - left - ArtPaddingPixels);
            var label = new UiCaptureRect(bounds.X + left, bounds.Y + (bounds.Height - measured.Height) / 2f, measured.Width, measured.Height);
            Add(path + "/label", null, "BadgeLabel", label, clip,
                TextContent(badge.Text, UiTextStyle.Label, ArtFlags(false)), new UiCaptureNodePaint(colors.On, colors.Fill));
        }

        private void PaintPips(UiPips pips, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var style = ResolveDecoration(pips, new UiLayoutExtent(bounds.Width / layoutScale, bounds.Height / layoutScale));
            Add(path, pips.Id, nameof(UiPips), bounds, clip, StatusContent(pips.DisplayLabel),
                DecorationPaint(style, null, null, Math.Max((float)style.BorderWidth * layoutScale, StatusBoundaryWidth),
                    pips.Style, pips.Role));
            var behind = scheme.GetPair(backdrop);
            var measured = MeasureStatusLabel(pips.DisplayLabel, bounds.Width);
            var label = new UiCaptureRect(bounds.X, bounds.Y, measured.Width, measured.Height);
            Add(path + "/label", null, "PipsLabel", label, clip,
                TextContent(pips.DisplayLabel, UiTextStyle.Label, ArtFlags(false)), new UiCaptureNodePaint(behind.On, behind.Fill));

            var track = new UiPipTrack(pips, bounds.Width / layoutScale);
            var stroke = Math.Max((float)style.BorderWidth, preferences.HighContrast ? 3f : 2f);
            var edge = UiStatusColors.Boundary(scheme, pips.Role, backdrop);
            var fill = UiStatusColors.PipFill(scheme, pips.Role, preferences.HighContrast);
            var empty = scheme.GetPair(UiPaintRole.Sunken).Fill;
            for (var index = 0; index < pips.Total; index++)
            {
                var mark = track.Mark(index);
                var box = new UiCaptureRect(bounds.X + mark.X * layoutScale,
                    label.Bottom + (StatusGap + mark.Y) * layoutScale, mark.Width * layoutScale, mark.Height * layoutScale);
                var radius = Math.Min((float)style.CornerRadius, Math.Min(mark.Width, mark.Height) / 2f);
                var markPath = ChildPath(path, index);
                Add(markPath, null, "PipTrack", box, clip, DecorativeArt, new UiCaptureNodePaint(null, empty, radius * layoutScale));
                Add(markPath + "/edge", null, "PipBoundary", box, clip, MeaningfulEdge,
                    new UiCaptureNodePaint(edge, behind.Fill, radius * layoutScale, stroke * layoutScale));
                var filled = mark.Fill(stroke, radius);
                if (filled.VisibleBounds.HasArea)
                {
                    var fraction = ScreenRect(filled.VisibleBounds.Translate(0f, StatusGap), bounds.X, label.Bottom);
                    Add(markPath + "/fill", null, "PipFill", fraction, clip, ReadableMark,
                        new UiCaptureNodePaint(fill, empty, filled.Radius * layoutScale));
                }
            }
        }

        private float StatusBoundaryWidth => (preferences.HighContrast ? 3f : 2f) * layoutScale;

        private UiCaptureNodePaint DecorationPaint(UiLayoutDecoration style, UiThemeColor? ink, UiThemeColor? fill, float border,
            UiStyleRef? requestedStyle = null, UiPaintRole? requestedRole = null) =>
            new UiCaptureNodePaint(ink, fill, (float)style.CornerRadius * layoutScale, border,
                (float)style.ShadowX * layoutScale, (float)style.ShadowY * layoutScale, style.StyleId,
                requestedStyle?.Id, requestedRole, requestedRole.HasValue ? style.Fill : null);

        private UiCaptureNodeContent ImageContent(UiImage image, bool missing) =>
            new UiCaptureNodeContent(string.Empty, image.SemanticLabel, 0f, 0f,
                ArtFlags(image.Decorative) | (missing ? UiCaptureNodeFlags.MissingAsset : 0));

        private UiCaptureNodeContent StatusContent(string label) => new UiCaptureNodeContent(string.Empty, label, 0f, 0f,
            ArtFlags(false) | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue);

        private UiCaptureNodeFlags ArtFlags(bool decorative) =>
            UiCaptureNodeFlags.ScrollableClip | (inheritedDisabled ? UiCaptureNodeFlags.Disabled : 0) |
            (decorative ? UiCaptureNodeFlags.Decorative : UiCaptureNodeFlags.RequiresLabel);

        private static readonly UiCaptureNodeContent DecorativeArt =
            new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.Decorative);
        private static readonly UiCaptureNodeContent ReadableMark =
            new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.SemanticMark);
        private static readonly UiCaptureNodeContent MeaningfulEdge =
            new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.BoundaryMark);
    }
}
