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
        private UiThemeScheme StatusScheme => Descriptor.SelectScheme(Scheme == TopiaForgeScheme.Hud, theme.HighContrast);

        private bool CaptureStatus(UiNode fixture, RectTransform native, string path, UiThemeColor? backdrop)
        {
            if (backdrop is not UiThemeColor background) return false;
            // Visit already passes Widget's single native child.
            return fixture switch
            {
                UiCountdown timer => CaptureCountdown(timer, native, path, background),
                UiProgress progress => CaptureProgress(progress, native, path, background),
                UiStatBar stat => CaptureStatBar(stat, native, path, background),
                _ => false,
            };
        }

        private bool CaptureRetainedZero(UiText fixture, RectTransform native, string path, UiThemeColor? backdrop) =>
            backdrop is UiThemeColor background && ReadStatusText(path, native.GetComponent<TMP_Text>(), fixture.Text,
                UiTextStyle.Body, StatusScheme.ResolveForeground(fixture.Tone, UiPaintRole.Surface), background, out var digits) &&
            StoreStatusText(path, fixture.Id, nameof(UiText), digits);

        private bool CaptureCountdown(UiCountdown source, RectTransform root, string path, UiThemeColor background)
        {
            var formatted = source.UsesStatusFormat;
            var digits = root.Find(formatted ? "Countdown digits" : "Label")?.GetComponent<TMP_Text>();
            if (digits == null || source.ClockEpoch == null || !double.IsFinite(clockTime) || clockTime < 0d ||
                !UiGalleryStatusEvidence.TryReadCountdown(digits.text, formatted ? string.Empty : source.Label, source.Format,
                    !formatted && Descriptor.GetText(UiTextStyle.Body).IsUppercase, out var seconds)) return false;
            var remaining = source.ClockEpoch == clockEpoch ? source.RemainingTime(clockTime) : 0d;
            if (seconds != UiCountdownFormatting.WholeSeconds(remaining)) return false;

            var status = UiStatusPresentation.For(source, remaining);
            var ink = StatusScheme.ResolveForeground(status.Tone, UiPaintRole.Surface);
            var phrase = UiCountdownFormatting.Text(source.Label, seconds, source.Format);
            if (!formatted)
            {
                return ReadStatusText(path, digits, phrase, UiTextStyle.Body, ink, background, out var text) &&
                    StoreStatusText(path, source.Id, nameof(UiCountdown), text, state: "normal", seconds: seconds);
            }

            var readoutPath = path + "/digits";
            if (!ReadStatusText(readoutPath, digits, UiCountdownFormatting.Digits(seconds, source.Format), UiTextStyle.Mono,
                ink, background, out var readout)) return false;
            var caption = root.Find("Label")?.GetComponent<TMP_Text>();
            var captionPath = path + "/label";
            StatusTextSample label = default;
            if (source.Label.Length != 0 && !ReadStatusText(captionPath, caption, source.Label, UiTextStyle.Label,
                ink, background, out label)) return false;
            if (!CaptureStatusCue(path, root, source.Thresholds != null, status.Glyph, ink, background)) return false;

            Add(path, source.Id, nameof(UiCountdown), root, StatusContent(phrase, status, seconds: seconds), default);
            return (source.Label.Length == 0 || StoreStatusText(captionPath, null, "CountdownLabel", label)) &&
                StoreStatusText(readoutPath, null, "CountdownDigits", readout);
        }

        private bool CaptureProgress(UiProgress source, RectTransform root, string path, UiThemeColor background)
        {
            var thresholded = source.Thresholds != null;
            var track = root.Find(thresholded ? "Progress track" : "Progress")?.GetComponent<Image>();
            var fill = (thresholded ? root.Find("Progress fill") : track?.transform.Find("Fill"))?.GetComponent<Image>();
            if (track == null || fill == null || !ReadPaint(track, out var trackInk) || !ReadPaint(fill, out var fillInk) ||
                !UiGalleryStatusEvidence.TryMeasureFill(Pixels(Bounds(track.rectTransform)), Pixels(Bounds(fill.rectTransform)),
                    source.Fraction, out var fraction)) return false;

            var status = UiStatusPresentation.For(source);
            var scheme = StatusScheme;
            var textInk = scheme.ResolveForeground(status.Tone, UiPaintRole.Surface);
            var caption = thresholded ? source.DisplayLabel : source.Label;
            if (thresholded)
            {
                var palette = UiStatusMeterPaint.Resolve(scheme, status.Tone, theme.HighContrast);
                if (!trackInk.Equals(palette.Empty.Fill) || !fillInk.Equals(palette.Filled.Fill) ||
                    !CaptureStatusCue(path, root, true, status.Glyph, textInk, background)) return false;
                Add(path, source.Id, nameof(UiProgress), root, StatusContent(caption, status, fraction: fraction), default);
                if (!ReadStatusText(path + "/label", root.Find("Label")?.GetComponent<TMP_Text>(), caption,
                    UiTextStyle.Body, textInk, background, out var label) || !StoreStatusText(path + "/label", null, "ProgressLabel", label))
                    return false;
            }
            else if (caption.Length == 0)
            {
                Add(path, source.Id, nameof(UiProgress), root, EmptyContent.WithStatus("normal", fraction: fraction), default);
            }
            else if (!ReadStatusText(path, root.Find("Label")?.GetComponent<TMP_Text>(), caption, UiTextStyle.Body,
                textInk, background, out var label) || !StoreStatusText(path, source.Id, nameof(UiProgress), label,
                    state: "normal", fraction: fraction)) return false;

            return AddStatusPaint(path + "/track", null, "Track", track, Bounds(track.rectTransform), EmptyContent,
                new UiCaptureNodePaint(null, trackInk)) && AddStatusPaint(path + "/fill", null, "TrackFill", fill,
                    Bounds(fill.rectTransform), EmptyContent, new UiCaptureNodePaint(null, fillInk));
        }

        private bool CaptureStatusCue(string path, RectTransform root, bool reserved, UiGlyph? expected,
            UiThemeColor ink, UiThemeColor background, UiCaptureRect? mask = null)
        {
            var cue = root.Find("Status cue")?.GetComponent<Image>();
            if (!reserved) return cue == null && expected == null;
            if (cue == null) return false;
            if (expected is not UiGlyph glyph) return !cue.enabled;
            var sprite = TopiaForgeSprites.Glyph(glyph);
            if (sprite == null || cue.sprite != sprite || !ReadPaint(cue, out var observed) || !ink.Equals(observed)) return false;
            var bounds = Bounds(cue.rectTransform);
            if (bounds.Width <= 0f || bounds.Height <= 0f) return false;
            if (mask.HasValue) bounds = bounds.Intersect(mask.Value);
            return AddStatusPaint(path + "/cue:" + glyph, null, "StatusCue", cue, bounds,
                State(UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.SemanticMark, UiGlyphLabels.Get(glyph)),
                new UiCaptureNodePaint(observed, background));
        }

        private static UiCaptureNodeContent StatusContent(string text, UiStatusPresentation status,
            double? seconds = null, float? fraction = null)
        {
            var flags = UiCaptureNodeFlags.RequiresLabel;
            if (status.Glyph.HasValue) flags |= UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            var state = status.Level switch
            {
                UiStatusLevel.Warning => "warning",
                UiStatusLevel.Critical => "critical",
                _ => "normal",
            };
            return new UiCaptureNodeContent(string.Empty, text, 0f, 0f, flags, state: state,
                remainingSeconds: seconds, fraction: fraction);
        }

        private static bool ReadPaint(Graphic graphic, out UiThemeColor observed)
        {
            var color = EffectiveColor(graphic);
            observed = ToRgb(color);
            return graphic.isActiveAndEnabled && color.a >= 0.999f;
        }

        private static UiLayoutRect Pixels(UiCaptureRect rect) => new UiLayoutRect(rect.X, rect.Y, rect.Width, rect.Height);
    }
}
