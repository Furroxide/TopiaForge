using System;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiLayoutRenderer
        {
            private sealed class NativeArtLeaf
            {
                private const float GlyphFloor = 18f;
                private const float BadgePadding = 8f;
                private const float StatusGap = 4f;

                private readonly TopiaForgeContainer host;
                private readonly UiNode node;
                private readonly TopiaForgeLabel? label;
                private readonly TMP_Text? text;
                private readonly Image? glyph;
                private readonly TopiaForgeRoundedImage? shadow;
                private readonly TopiaForgeRoundedImage? fill;
                private readonly TopiaForgeRoundedImage? border;
                private readonly NativePip[]? marks;
                private readonly UiSurfaceArtwork? artwork;
                private readonly TopiaForgeContainer? imageFrame;
                private readonly UiNativeStatusPaint? statusPaint;

                internal NativeArtLeaf(TopiaForgeContainer host, UiNode node, UiSurfaceArtwork? artwork)
                {
                    this.host = host;
                    this.node = node;
                    this.artwork = artwork;
                    if (GalleryCaptureEnabled && node is (UiBadge or UiPips))
                        statusPaint = host.Go.AddComponent<UiNativeStatusPaint>();
                    switch (node)
                    {
                        case UiIcon:
                            glyph = AddGraphic<Image>(host, "Glyph");
                            break;
                        case UiImage image:
                            imageFrame = host.CreateChild("Image frame");
                            imageFrame.Go.AddComponent<RectMask2D>();
                            fill = AddGraphic<TopiaForgeRoundedImage>(imageFrame, "Image placeholder");
                            border = AddGraphic<TopiaForgeRoundedImage>(imageFrame, "Image boundary");
                            glyph = AddGraphic<Image>(imageFrame, "Sprite");
                            if (!image.Decorative)
                                label = imageFrame.Label(image.SemanticLabel, TopiaForgeTextStyle.Body).PlainText().AlignTopLeft();
                            break;
                        case UiBadge badge:
                            shadow = AddGraphic<TopiaForgeRoundedImage>(host, "Badge shadow");
                            fill = AddGraphic<TopiaForgeRoundedImage>(host, "Badge fill");
                            border = AddGraphic<TopiaForgeRoundedImage>(host, "Badge boundary");
                            glyph = AddGraphic<Image>(host, "Badge glyph");
                            label = host.Label(badge.Text, TopiaForgeTextStyle.Label).PlainText().AlignTopLeft();
                            break;
                        case UiPips pips:
                            label = host.Label(pips.DisplayLabel, TopiaForgeTextStyle.Label).PlainText().AlignTopLeft();
                            marks = new NativePip[pips.Total];
                            for (var index = 0; index < marks.Length; index++) marks[index] = new NativePip(host);
                            break;
                    }
                    text = label == null ? null : label.Go.GetComponent<TMP_Text>();
                }

                private float ReadableGlyphScale => Math.Max(1f, host.Host.EffectiveUiScale) / host.Host.EffectiveUiScale;

                internal UiLayoutLeafMeasurement Measure(UiLayoutConstraints constraints, UiThemeDescriptor effective,
                    UiThemeDescriptor? declared, bool highContrast, bool protectImageLabels)
                {
                    if (node is UiIcon icon)
                    {
                        var size = IconExtent(icon);
                        return new UiLayoutLeafMeasurement(new UiLayoutExtent(size, size),
                            icon.Decorative ? UiLayoutPaintKind.Painter : UiLayoutPaintKind.Protected,
                            localPaintBounds: new UiLayoutRect(0, 0, size, size));
                    }

                    if (node is UiImage image)
                    {
                        var frame = artwork!.Resolve(image).Frame;
                        var kind = protectImageLabels && IsMissingSemanticImage
                            ? UiLayoutPaintKind.Protected : UiLayoutPaintKind.Image;
                        return new UiLayoutLeafMeasurement(new UiLayoutExtent(frame.Width, frame.Height), kind,
                            localPaintBounds: new UiLayoutRect(0, 0, frame.Width, frame.Height));
                    }

                    UiLayoutExtent extent;
                    if (node is UiBadge badge)
                    {
                        var symbol = UiBadgePresentation.Glyph(badge, highContrast).HasValue ? GlyphFloor * ReadableGlyphScale : 0f;
                        var left = BadgePadding + symbol + (symbol > 0f ? StatusGap : 0f);
                        var size = MeasureTitle(constraints.MaxWidth - left - BadgePadding);
                        if (size.x <= 0f || size.y <= 0f)
                            throw new ArgumentException("The badge label has no readable bounds.");
                        extent = new UiLayoutExtent(left + size.x + BadgePadding,
                            2 * BadgePadding + Math.Max(symbol, size.y));
                    }
                    else
                    {
                        var track = new UiPipTrack((UiPips)node, (float)constraints.MaxWidth);
                        var size = MeasureTitle(constraints.MaxWidth);
                        extent = new UiLayoutExtent(Math.Max(track.Width, size.x), size.y + StatusGap + track.Height);
                    }

                    var style = UiLayoutDecoration.Resolve(node, effective, declared, extent, highContrast);
                    return new UiLayoutLeafMeasurement(extent, UiLayoutPaintKind.Protected, style.ShadowOutsets);
                }

                private Vector2 MeasureTitle(double width)
                {
                    if (width <= 0) throw new ArgumentException("The status label needs a positive allocation.");
                    var preferred = text!.GetPreferredValues(text.text, (float)width, 0f);
                    return preferred;
                }

                private float IconExtent(UiIcon icon) => icon.Size * (icon.Decorative ? 1f : ReadableGlyphScale);

                internal bool IsMissingSemanticImage =>
                    node is UiImage { Decorative: false } image && artwork!.Resolve(image).Sprite == null;

                internal bool NeedsImageNotice(UiLayoutRect allocated)
                {
                    if (!IsMissingSemanticImage || node is not UiImage image) return false;
                    var frame = artwork!.Resolve(image).Frame;
                    var bounds = UiImageProjection.FixedBox(allocated, frame.Width, frame.Height);
                    var width = bounds.Width - 2 * BadgePadding;
                    var height = bounds.Height - 2 * BadgePadding;
                    if (width <= 0 || height <= 0) return true;
                    var size = MeasureTitle(width);
                    return size.x > width || size.y > height;
                }

                internal void Apply(UiLayoutRect bounds, UiPaintRole backdrop, UiThemeScheme scheme,
                    TopiaForgeResolvedTheme theme, UiThemeDescriptor effective, UiThemeDescriptor? declared)
                {
                    if (node is UiIcon icon)
                    {
                        var size = Math.Min(IconExtent(icon), Math.Min(bounds.Width, bounds.Height));
                        PaintGlyph(glyph!, icon.Glyph, scheme.ResolveForeground(icon.Tone, backdrop), 0, 0, size);
                        return;
                    }

                    if (node is UiImage image)
                    {
                        PaintImage(image, bounds, backdrop, scheme, theme.HighContrast);
                        return;
                    }

                    var style = UiLayoutDecoration.Resolve(node, effective, declared,
                        new UiLayoutExtent(bounds.Width, bounds.Height), theme.HighContrast);
                    if (node is UiBadge badge) PaintBadge(badge, bounds, backdrop, scheme, theme, style);
                    else PaintPips((UiPips)node, bounds, backdrop, scheme, theme, style);
                    statusPaint?.Record(style);
                }

                private void PaintImage(UiImage node, UiLayoutRect bounds, UiPaintRole backdrop, UiThemeScheme scheme, bool highContrast)
                {
                    var image = artwork!.Resolve(node);
                    var available = new UiLayoutRect(0, 0, bounds.Width, bounds.Height);
                    var box = UiImageProjection.FixedBox(available, image.Frame.Width, image.Frame.Height);
                    Place(imageFrame!.Rect, box);
                    var sprite = image.Sprite;
                    var hasSprite = sprite != null && box.HasArea;
                    glyph!.enabled = hasSprite;
                    fill!.enabled = !hasSprite;
                    border!.enabled = !hasSprite;
                    var needsNotice = !hasSprite && NeedsImageNotice(box);
                    if (label != null) label.SetShown(!hasSprite && !needsNotice);

                    if (hasSprite)
                    {
                        var position = image.Frame.Place((float)box.Width, (float)box.Height);
                        var width = position.Width / position.SourceWidth;
                        var height = position.Height / position.SourceHeight;
                        glyph.sprite = sprite;
                        glyph.color = node.Tint is UiTone tone ? ColorOf(scheme.ResolveForeground(tone, backdrop)) : Color.white;
                        Place(glyph.rectTransform, new UiLayoutRect(position.X - position.SourceLeft * width,
                            position.Y - position.SourceTop * height, width, height));
                        return;
                    }

                    var pair = scheme.GetPair(UiPaintRole.Sunken);
                    PaintShape(fill, box, pair.Fill, 0);
                    PaintShape(border, box, scheme.ResolveBoundary(backdrop), 0, highContrast ? 3f : 2f);
                    if (label == null || needsNotice) return;

                    label.SetResolvedInk(ColorOf(pair.On));
                    Place(label.Rect, new UiLayoutRect(BadgePadding, BadgePadding,
                        box.Width - 2 * BadgePadding, box.Height - 2 * BadgePadding));
                }

                private void PaintBadge(UiBadge badge, UiLayoutRect bounds, UiPaintRole backdrop, UiThemeScheme scheme,
                    TopiaForgeResolvedTheme theme, UiLayoutDecoration style)
                {
                    var pair = UiStatusColors.Badge(scheme, badge, backdrop);
                    var width = bounds.Width;
                    var height = bounds.Height;
                    var radius = (float)style.CornerRadius;
                    var stroke = Math.Max((float)style.BorderWidth,
                        badge.Presentation == UiBadgeStyle.Outlined ? (theme.HighContrast ? 3f : 2f) : 0f);
                    fill!.enabled = badge.Presentation == UiBadgeStyle.Filled;
                    if (fill.enabled) PaintShape(fill, new UiLayoutRect(0, 0, width, height), pair.Fill, radius);
                    border!.enabled = stroke > 0f;
                    if (border.enabled) PaintShape(border, new UiLayoutRect(0, 0, width, height),
                        UiStatusColors.Boundary(scheme, badge.Role, backdrop), radius, stroke);
                    PaintShadow(shadow!, new UiLayoutRect(0, 0, width, height), style, radius, scheme.GetPair(backdrop).On, theme.Shadow.a);

                    var symbol = UiBadgePresentation.Glyph(badge, theme.HighContrast);
                    var left = (double)BadgePadding;
                    glyph!.enabled = symbol.HasValue;
                    if (symbol is UiGlyph mark)
                    {
                        var size = GlyphFloor * ReadableGlyphScale;
                        PaintGlyph(glyph, mark, pair.On, left, (height - size) / 2, size);
                        left += size + StatusGap;
                    }
                    var textHeight = MeasureTitle(width - left - BadgePadding).y;
                    label!.SetResolvedInk(ColorOf(pair.On));
                    Place(label.Rect, new UiLayoutRect(left, (height - textHeight) / 2, width - left - BadgePadding, textHeight));
                }

                private void PaintPips(UiPips pips, UiLayoutRect bounds, UiPaintRole backdrop, UiThemeScheme scheme,
                    TopiaForgeResolvedTheme theme, UiLayoutDecoration style)
                {
                    var labelHeight = MeasureTitle(bounds.Width).y;
                    label!.SetResolvedInk(ColorOf(scheme.GetPair(backdrop).On));
                    Place(label.Rect, new UiLayoutRect(0, 0, bounds.Width, labelHeight));
                    var track = new UiPipTrack(pips, (float)bounds.Width);
                    var edge = UiStatusColors.Boundary(scheme, pips.Role, backdrop);
                    var inside = UiStatusColors.PipFill(scheme, pips.Role, theme.HighContrast);
                    var baseColor = scheme.GetPair(UiPaintRole.Sunken).Fill;
                    for (var index = 0; index < marks!.Length; index++)
                    {
                        var mark = track.Mark(index);
                        var y = labelHeight + StatusGap + mark.Y;
                        var rect = new UiLayoutRect(mark.X, y, mark.Width, mark.Height);
                        var radius = Math.Min((float)style.CornerRadius, Math.Min(mark.Width, mark.Height) / 2f);
                        var stroke = Math.Max((float)style.BorderWidth, theme.HighContrast ? 3f : 2f);
                        var fill = mark.Fill(stroke, radius);
                        var native = marks[index];
                        PaintShadow(native.Shadow, rect, style, radius, scheme.GetPair(backdrop).On, theme.Shadow.a);
                        PaintShape(native.Empty, rect, baseColor, radius);
                        native.FillClip.SetShown(fill.VisibleBounds.HasArea);
                        if (fill.VisibleBounds.HasArea)
                        {
                            // Clip the full rounded interior so a narrow or partial fill never crosses the arc of the inset border.
                            Place(native.FillClip.Rect, fill.VisibleBounds.Translate(0f, labelHeight + StatusGap));
                            PaintShape(native.Fill, new UiLayoutRect(0f, 0f, fill.Interior.Width, fill.Interior.Height), inside, fill.Radius);
                        }
                        PaintShape(native.Border, rect, edge, radius, stroke);
                    }
                }

                private static void PaintGlyph(Image image, UiGlyph glyph, UiThemeColor ink, double left, double top, double size)
                {
                    image.sprite = TopiaForgeSprites.Glyph(glyph);
                    image.color = ColorOf(ink);
                    Place(image.rectTransform, new UiLayoutRect(left, top, size, size));
                }

                private static void PaintShape(TopiaForgeRoundedImage target, UiLayoutRect rect, UiThemeColor color,
                    float radius, float stroke = 0f)
                {
                    Place(target.rectTransform, rect);
                    target.color = ColorOf(color);
                    target.SetShape(radius, stroke);
                }

                private static void PaintShadow(TopiaForgeRoundedImage target, UiLayoutRect rect, UiLayoutDecoration style,
                    float radius, UiThemeColor color, float opacity)
                {
                    target.enabled = style.ShadowX != 0 || style.ShadowY != 0;
                    if (!target.enabled) return;
                    PaintShape(target, rect.Translate(style.ShadowX, style.ShadowY), color, radius);
                    var ink = target.color;
                    ink.a = opacity;
                    target.color = ink;
                }

                private readonly struct NativePip
                {
                    internal NativePip(TopiaForgeContainer host)
                    {
                        Shadow = AddGraphic<TopiaForgeRoundedImage>(host, "Pip shadow");
                        Empty = AddGraphic<TopiaForgeRoundedImage>(host, "Pip track");
                        FillClip = host.CreateChild("Pip fill clip");
                        FillClip.Go.AddComponent<RectMask2D>();
                        Fill = AddGraphic<TopiaForgeRoundedImage>(FillClip, "Pip fill");
                        Border = AddGraphic<TopiaForgeRoundedImage>(host, "Pip boundary");
                    }

                    internal TopiaForgeRoundedImage Shadow { get; }
                    internal TopiaForgeRoundedImage Empty { get; }
                    internal TopiaForgeContainer FillClip { get; }
                    internal TopiaForgeRoundedImage Fill { get; }
                    internal TopiaForgeRoundedImage Border { get; }
                }
            }
        }
    }

    /// <summary>Retains the status style actually painted on the native artwork for Gallery capture.</summary>
    internal sealed class UiNativeStatusPaint : MonoBehaviour
    {
        internal string? StyleId { get; private set; }
        internal UiPaintRole? Role { get; private set; }

        internal void Record(UiLayoutDecoration style)
        {
            StyleId = style.StyleId;
            Role = style.Fill;
        }
    }
}
