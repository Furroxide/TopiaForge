using System;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private bool CaptureStatBar(UiStatBar node, RectTransform? root, string path, UiThemeColor background)
        {
            if (root == null || root.Find("Stat filled clip") is not RectTransform filled ||
                root.Find("Stat empty clip") is not RectTransform empty) return false;
            var track = root.Find("Stat track")?.GetComponent<TopiaForgeRoundedImage>();
            var paint = filled.Find("Stat fill")?.GetComponent<TopiaForgeRoundedImage>();
            var edge = root.Find("Stat boundary")?.GetComponent<TopiaForgeRoundedImage>();
            var shadow = root.Find("Stat shadow")?.GetComponent<TopiaForgeRoundedImage>();
            if (track == null || paint == null || edge == null || shadow == null || !ReadPaint(track, out var trackInk) ||
                !TryStatusMask(filled, out var filledBox) || !TryStatusMask(empty, out var emptyBox)) return false;

            var trackBox = Bounds(track.rectTransform);
            var fillBox = Bounds(paint.rectTransform);
            if (track.ResolvedCornerRadius is not float trackRadius || paint.ResolvedCornerRadius is not float fillRadius ||
                !UiGalleryStatusEvidence.SamePaintBounds(Pixels(trackBox), Pixels(fillBox)) ||
                !UiGalleryStatusEvidence.TryMeasureSplit(Pixels(trackBox), Pixels(filledBox), Pixels(emptyBox), node.Fraction,
                    out var fraction) || !Mathf.Approximately(trackRadius, fillRadius)) return false;

            var status = UiStatusPresentation.For(node);
            var scheme = StatusScheme;
            var meter = UiStatusMeterPaint.Resolve(scheme, status.Tone, theme.HighContrast);
            if (!trackInk.Equals(meter.Empty.Fill)) return false;
            UiThemeColor? fillInk = null;
            if (filledBox.Width > 0f)
            {
                if (!ReadPaint(paint, out var observed) || !observed.Equals(meter.Filled.Fill)) return false;
                fillInk = observed;
            }

            var units = new UiLayoutExtent(track.rectTransform.rect.width, track.rectTransform.rect.height);
            var shape = UiLayoutDecoration.Resolve(node, Descriptor, Descriptor, units, theme.HighContrast);
            var hasBorder = shape.BorderWidth > 0d;
            var hasShadow = shape.ShadowX != 0d || shape.ShadowY != 0d;
            if (hasBorder != edge.isActiveAndEnabled || hasShadow != shadow.isActiveAndEnabled ||
                hasShadow && EffectiveColor(shadow).a <= 0f ||
                !Mathf.Approximately(trackRadius, (float)shape.CornerRadius) ||
                hasBorder && (!Mathf.Approximately(edge.ResolvedBorderWidth, (float)shape.BorderWidth) ||
                    !UiGalleryStatusEvidence.SamePaintBounds(Pixels(trackBox), Pixels(Bounds(edge.rectTransform))))) return false;

            var scale = VerticalScale(track.rectTransform);
            var radius = trackRadius * scale;
            var thickness = hasBorder ? edge.ResolvedBorderWidth * scale : 0f;
            var shadeBox = hasShadow ? Bounds(shadow.rectTransform) : trackBox;
            if (!AddStatusPaint(path, node.Id, nameof(UiStatBar), track, trackBox, StatusContent(node.DisplayLabel, status, fraction: fraction),
                new UiCaptureNodePaint(null, trackInk, radius, thickness, shadeBox.X - trackBox.X, shadeBox.Y - trackBox.Y))) return false;
            if (hasBorder)
            {
                if (!ReadPaint(edge, out var observed) || !observed.Equals(scheme.ResolveBoundary(UiPaintRole.Surface))) return false;
                if (!AddStatusPaint(path + "/boundary", null, "StatBoundary", edge, Bounds(edge.rectTransform),
                    State(UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.BoundaryMark),
                    new UiCaptureNodePaint(observed, background, radius, thickness))) return false;
            }
            if (fillInk.HasValue && !AddStatusPaint(path + "/fill", null, "StatFill", paint, fillBox.Intersect(filledBox),
                EmptyContent, new UiCaptureNodePaint(null, fillInk, radius))) return false;

            if (!CaptureStatSide(path + "/filled", "StatFilledLabel", filled, filledBox, meter.Filled, out var first) ||
                !CaptureStatSide(path + "/empty", "StatEmptyLabel", empty, emptyBox, meter.Empty, out var second)) return false;
            return !first.HasValue || !second.HasValue || UiGalleryStatusEvidence.SamePaintBounds(Pixels(first.Value), Pixels(second.Value));

            bool CaptureStatSide(string sidePath, string kind, RectTransform mask, UiCaptureRect maskBox, UiThemePair pair,
                out UiCaptureRect? glyphBounds)
            {
                glyphBounds = null;
                if (maskBox.Width <= 0f) return true;
                if (!ReadStatusText(sidePath, mask.Find("Label")?.GetComponent<TMP_Text>(), node.DisplayLabel,
                    UiTextStyle.Label, pair.On, pair.Fill, out var label) ||
                    !CaptureStatusCue(sidePath, mask, node.Thresholds != null, status.Glyph, pair.On, pair.Fill, maskBox)) return false;
                glyphBounds = label.Bounds;
                return StoreStatusText(sidePath, null, kind, label, maskBox);
            }
        }

        private bool TryStatusMask(RectTransform rect, out UiCaptureRect bounds)
        {
            bounds = Bounds(rect);
            var mask = rect.GetComponent<RectMask2D>();
            return mask != null && mask.padding == Vector4.zero && mask.softness == Vector2Int.zero &&
                mask.isActiveAndEnabled == (bounds.Width > 0f);
        }
    }
}
