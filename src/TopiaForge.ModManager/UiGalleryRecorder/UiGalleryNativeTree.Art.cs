using System;
using System.Globalization;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private static readonly UiCaptureNodeContent ArtEdge = State(UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.BoundaryMark);
        private static readonly UiCaptureNodeContent ArtMark = State(UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.SemanticMark);
        private static readonly UiCaptureNodeContent ArtDecoration = State(UiCaptureNodeFlags.Decorative);

        private bool CaptureArt(UiNode fixture, RectTransform artwork, string path, UiThemeColor? backdrop)
        {
            if (onlyUnavailableImages && fixture is not UiImage) return true;
            var start = observations.Count;
            var captured = fixture switch
            {
                UiIcon icon => CaptureIcon(icon, artwork, path, backdrop),
                UiImage image => CaptureImage(image, artwork, path, backdrop),
                UiBadge badge => CaptureBadge(badge, artwork, path, backdrop),
                UiPips pips => CapturePips(pips, artwork, path, backdrop),
                _ => false,
            };
            if (captured && !onlyUnavailableImages && observations.Count > start)
            {
                var protectsContent = fixture switch
                {
                    UiImage image => !image.Decorative &&
                        (observations[start].Content.Flags & UiCaptureNodeFlags.MissingAsset) != 0,
                    UiIcon icon => !icon.Decorative,
                    _ => true,
                };
                RegisterGroup(start, protectsContent);
            }
            return captured;
        }

        private bool CaptureImageNotice(UiSurfaceRequest fixture, RectTransform notice, string? queuedNotice)
        {
            var background = notice.Find("Layout fallback background")?.GetComponent<Graphic>();
            var label = notice.Find("Label")?.GetComponent<TMP_Text>();
            if (background == null || label == null) return false;
            if (queuedNotice != null)
            {
                if (label.isActiveAndEnabled || fixture.Kind != UiSurfaceKind.Hud) return false;
                AddQueuedHudNotice(queuedNotice);
                return true;
            }

            var flags = UiCaptureNodeFlags.RequiresLabel;
            if (!label.isActiveAndEnabled || Clip(label.rectTransform, Bounds(label.rectTransform)).AnyClipping)
                flags |= UiCaptureNodeFlags.LayoutDiagnostic;
            var fill = Over(background, null);
            if (fixture.Kind == UiSurfaceKind.Hud && label.isActiveAndEnabled)
            {
                Add("/", fixture.Id, fixture.Kind.ToString(), notice.parent.GetComponent<RectTransform>(), EmptyContent,
                    new UiCaptureNodePaint(null, fill), localFallback: true, kitPaint: true);
                if (!Text("/title", null, "SurfaceTitle", notice.Find("LayoutNoticeTitle")?.GetComponent<TMP_Text>(),
                        fixture.Title, UiTextStyle.Heading, fill, UiCaptureNodeFlags.RequiresLabel,
                        localFallback: true, kitPaint: true)) return false;
            }
            return Text("/fallback", null, "ImageFallback", label, UiImageNotice.Text, UiTextStyle.Body,
                fill, flags, localFallback: true, kitPaint: true);
        }

        private bool CaptureIcon(UiIcon icon, RectTransform artwork, string path, UiThemeColor? backdrop)
        {
            var glyph = artwork.Find("Glyph")?.GetComponent<Image>();
            if (glyph == null || glyph.sprite == null) return false;
            var state = icon.Decorative ? UiCaptureNodeFlags.Decorative :
                UiCaptureNodeFlags.RequiresLabel | UiCaptureNodeFlags.SemanticMark;
            Add(path, icon.Id, nameof(UiIcon), glyph.rectTransform, State(state, icon.SemanticLabel),
                ArtPaint(glyph, backdrop), visible: glyph.isActiveAndEnabled);
            return true;
        }

        private bool CaptureImage(UiImage image, RectTransform artwork, string path, UiThemeColor? backdrop)
        {
            var frame = artwork.Find("Image frame") as RectTransform;
            var sprite = frame?.Find("Sprite")?.GetComponent<Image>();
            if (frame == null || sprite == null) return false;
            var available = sprite.enabled && sprite.sprite != null;
            var flags = image.Decorative ? UiCaptureNodeFlags.Decorative : UiCaptureNodeFlags.RequiresLabel;
            if (!available) flags |= UiCaptureNodeFlags.MissingAsset;
            var content = State(flags, image.SemanticLabel);
            if (onlyUnavailableImages)
            {
                if (!available) Add(path, image.Id, nameof(UiImage), frame, content, default, visible: false, localFallback: true);
                return true;
            }

            if (available)
            {
                var cover = image.Fit == UiImageFit.Cover;
                if (cover && (!sprite.maskable || !frame.TryGetComponent<RectMask2D>(out var coverMask) ||
                    !coverMask.isActiveAndEnabled)) return false;
                Add(path, image.Id, nameof(UiImage), frame, content, default);
                var tinted = image.Tint.HasValue && !image.Decorative;
                Add(path + "/sprite", null, "ImageSprite", sprite.rectTransform, tinted ? ArtMark : ArtDecoration,
                    tinted ? ArtPaint(sprite, backdrop) : default, authoredFrame: cover ? frame : null);
                if (cover)
                    Add(path + "/source", null, "ImageSourceProjection", sprite.rectTransform, ArtDecoration,
                        default, visible: false);
                return true;
            }

            var plate = frame.Find("Image placeholder")?.GetComponent<TopiaForgeRoundedImage>();
            var edge = frame.Find("Image boundary")?.GetComponent<TopiaForgeRoundedImage>();
            if (plate == null || edge == null) return false;
            var fill = Over(plate, backdrop);
            Add(path, image.Id, nameof(UiImage), frame, content, new UiCaptureNodePaint(null, fill, 0,
                edge.ResolvedBorderWidth * VerticalScale(edge.rectTransform)), localFallback: true);
            Add(path + "/edge", null, "ImageBoundary", edge.rectTransform, image.Decorative ? ArtDecoration : ArtEdge,
                ArtPaint(edge, backdrop), localFallback: true);
            return image.Decorative || Text(path + "/label", null, "ImagePlaceholder", Named<TMP_Text>(frame, "Label"),
                image.SemanticLabel, UiTextStyle.Body, fill, localFallback: true);
        }

        private bool CaptureBadge(UiBadge badge, RectTransform artwork, string path, UiThemeColor? backdrop)
        {
            var plate = artwork.Find("Badge fill")?.GetComponent<TopiaForgeRoundedImage>();
            var border = artwork.Find("Badge boundary")?.GetComponent<TopiaForgeRoundedImage>();
            var shadow = artwork.Find("Badge shadow")?.GetComponent<TopiaForgeRoundedImage>();
            var applied = artwork.GetComponent<UiNativeStatusPaint>();
            if (plate == null || border == null || shadow == null || applied?.Role is not UiPaintRole role) return false;
            var label = Named<TMP_Text>(artwork, "Label");
            var fill = Over(plate, backdrop);
            var edgeActive = border.isActiveAndEnabled;
            var scale = VerticalScale(artwork);
            var box = Bounds(artwork);
            var shadowBounds = shadow.isActiveAndEnabled ? Bounds(shadow.rectTransform) : box;
            var expectedGlyph = UiBadgePresentation.Glyph(badge, theme.HighContrast);
            var glyph = artwork.Find("Badge glyph")?.GetComponent<Image>();
            var hasGlyph = expectedGlyph is UiGlyph expected && glyph != null && glyph.isActiveAndEnabled &&
                glyph.sprite == TopiaForgeSprites.Glyph(expected);
            var flags = UiCaptureNodeFlags.RequiresLabel | UiCaptureNodeFlags.RequiresNonColorCue;
            if (label != null && (!expectedGlyph.HasValue || hasGlyph)) flags |= UiCaptureNodeFlags.HasNonColorCue;
            Add(path, badge.Id, nameof(UiBadge), artwork, State(flags, badge.Text), new UiCaptureNodePaint(null,
                plate.isActiveAndEnabled ? fill : null, (plate.ResolvedCornerRadius ?? border.ResolvedCornerRadius) * scale,
                edgeActive ? border.ResolvedBorderWidth * scale : 0f, shadowBounds.X - box.X, shadowBounds.Y - box.Y,
                applied.StyleId, badge.Style?.Id, badge.Role, role));
            if (edgeActive) Add(path + "/edge", null, "BadgeBoundary", border.rectTransform, ArtEdge, ArtPaint(border, backdrop));
            if (hasGlyph)
                Add(path + "/glyph:" + expectedGlyph, null, "BadgeGlyph", glyph!.rectTransform, ArtMark, ArtPaint(glyph, fill));
            return Text(path + "/label", null, "BadgeLabel", label, badge.Text, UiTextStyle.Label, fill);
        }

        private bool CapturePips(UiPips pips, RectTransform artwork, string path, UiThemeColor? backdrop)
        {
            if (artwork.childCount != 1 + 4 * pips.Total) return false;
            var firstTrack = artwork.GetChild(2).GetComponent<TopiaForgeRoundedImage>();
            var firstBorder = artwork.GetChild(4).GetComponent<TopiaForgeRoundedImage>();
            var firstShadow = artwork.GetChild(1).GetComponent<TopiaForgeRoundedImage>();
            var applied = artwork.GetComponent<UiNativeStatusPaint>();
            if (firstTrack == null || firstBorder == null || firstShadow == null || applied?.Role is not UiPaintRole role) return false;
            var pixels = VerticalScale(firstTrack.rectTransform);
            var trackBox = Bounds(firstTrack.rectTransform);
            var shadowBox = firstShadow.isActiveAndEnabled ? Bounds(firstShadow.rectTransform) : trackBox;
            var label = Named<TMP_Text>(artwork, "Label");
            var shown = UiRefillPresentation.For(pips, clockTime, clockEpoch);
            var readout = pips.DisplayLabel;
            if (pips.Refill != null && !TryReadPipRefill(pips, shown, label?.text, out readout)) return false;
            if (!Text(path + "/label", null, "PipsLabel", label, readout, UiTextStyle.Label, backdrop)) return false;
            float? fraction = pips.Refill != null ? 0f : null;
            for (var index = 0; index < pips.Total; index++)
            {
                var offset = 1 + 4 * index;
                var track = artwork.GetChild(offset + 1).GetComponent<TopiaForgeRoundedImage>();
                var fillClip = artwork.GetChild(offset + 2) as RectTransform;
                var fill = fillClip?.Find("Pip fill")?.GetComponent<TopiaForgeRoundedImage>();
                var border = artwork.GetChild(offset + 3).GetComponent<TopiaForgeRoundedImage>();
                if (track == null || fillClip == null || fill == null || border == null ||
                    !fillClip.TryGetComponent<RectMask2D>(out var mask)) return false;
                var mark = ChildPath(path, index);
                var trackPaint = Over(track, backdrop);
                Add(mark, null, "PipTrack", track.rectTransform, ArtDecoration, new UiCaptureNodePaint(null, trackPaint,
                    track.ResolvedCornerRadius * VerticalScale(track.rectTransform)));
                Add(mark + "/edge", null, "PipBoundary", border.rectTransform, ArtEdge, ArtPaint(border, backdrop));
                if (fill.isActiveAndEnabled)
                {
                    if (!mask.isActiveAndEnabled) return false;
                    if (pips.Refill != null)
                    {
                        var width = Bounds(track.rectTransform).Width - 2f * border.ResolvedBorderWidth * VerticalScale(border.rectTransform);
                        var observed = Bounds(fillClip).Width;
                        if (width <= 0f || observed <= 0f || observed > width + 0.01f ||
                            index > shown.Filled || index == shown.Filled && !shown.IsRefilling ||
                            index < shown.Filled && Math.Abs(observed - width) > 0.01f) return false;
                        if (index == shown.Filled) fraction = Math.Min(1f, observed / width);
                    }
                    Add(mark + "/fill", null, "PipFill", fill.rectTransform, ArtMark, ArtPaint(fill, trackPaint),
                        authoredFrame: fillClip);
                }
                else if (pips.Refill != null && index < shown.Filled) return false;
            }

            var flags = UiCaptureNodeFlags.RequiresLabel | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            Add(path, pips.Id, nameof(UiPips), artwork, new UiCaptureNodeContent(string.Empty, readout, 0f, 0f, flags,
                remainingSeconds: pips.Refill == null ? null : shown.RemainingSeconds, fraction: fraction),
                new UiCaptureNodePaint(null, null, firstTrack.ResolvedCornerRadius * pixels,
                    firstBorder.ResolvedBorderWidth * pixels, shadowBox.X - trackBox.X, shadowBox.Y - trackBox.Y,
                    applied.StyleId, pips.Style?.Id, pips.Role, role));
            return true;
        }

        private bool TryReadPipRefill(UiPips source, UiRefillPresentation shown, string? observed, out string readout)
        {
            readout = source.Label + ": " + CountText(shown.Filled, shown.Total);
            var upper = Descriptor.GetText(UiTextStyle.Label).IsUppercase;
            if (!shown.IsRefilling) return UiGalleryCaptureFixture.SafeDisplayText(observed, readout, upper) != null;

            var prefix = readout + " (";
            const string suffix = "% refilling)";
            var suffixLength = suffix.Length;
            if (observed == null || observed.Length <= prefix.Length + suffixLength ||
                !int.TryParse(observed.AsSpan(prefix.Length, observed.Length - prefix.Length - suffixLength),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent < 0 || percent > 99) return false;
            readout = prefix + percent.ToString(CultureInfo.InvariantCulture) + suffix;
            return UiGalleryCaptureFixture.SafeDisplayText(observed, readout, upper) != null;
        }

        private string? ArtStyle(UiStyleRef? style) =>
            !fallback && style != null && Descriptor.Styles.ContainsKey(style.Id) ? style.Id : null;

        private UiCaptureNodePaint ArtPaint(Graphic graphic, UiThemeColor? backdrop)
        {
            if (graphic is not TopiaForgeRoundedImage shape)
                return new UiCaptureNodePaint(ToRgb(EffectiveColor(graphic)), backdrop);
            var pixels = VerticalScale(shape.rectTransform);
            return new UiCaptureNodePaint(ToRgb(EffectiveColor(shape)), backdrop, shape.ResolvedCornerRadius * pixels,
                shape.ResolvedBorderWidth * pixels);
        }
    }
}
