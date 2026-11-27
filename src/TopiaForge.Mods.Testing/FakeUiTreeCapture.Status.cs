using System;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private const float ProgressTrackHeight = 14f;
        private static readonly string[] MaximumCountdownDigits =
        {
            UiCountdownFormatting.Digits(int.MaxValue, UiCountdownFormat.Seconds),
            UiCountdownFormatting.Digits(int.MaxValue, UiCountdownFormat.MinutesSeconds),
            UiCountdownFormatting.Digits(int.MaxValue, UiCountdownFormat.HoursMinutesSeconds),
        };

        private UiLayoutLeafMeasurement MeasureStatus(UiNode node, UiLayoutConstraints limits)
        {
            var width = (float)(limits.MaxWidth * layoutScale);
            var shadow = default(UiLayoutOutsets);
            Size wanted;
            switch (node)
            {
                case UiCountdown countdown:
                    wanted = StatusRow(countdown.Label, UiTextStyle.Label, countdown.Thresholds != null, width, countdown.Format).Size;
                    break;
                case UiProgress progress:
                    var row = StatusRow(progress.DisplayLabel, UiTextStyle.Body, true, width).Size;
                    wanted = new Size(float.IsPositiveInfinity(width) ? row.Width : Math.Max(width, row.Width),
                        row.Height + (StatusGap + ProgressTrackHeight) * layoutScale);
                    break;
                case UiStatBar stat:
                    var caption = MeasureText(stat.DisplayLabel, UiTextStyle.Label, width, wrap: false);
                    var cue = stat.Thresholds == null ? 0f : ArtGlyphSize;
                    var content = StatContentSize(caption, cue);
                    var height = Math.Max(UiStatBarLayout.MinimumHeight * layoutScale, content.Height + 2f * LabelGap);
                    var units = height / (double)layoutScale;
                    var fitted = new UiLayoutExtent(limits.MaxWidth, Math.Clamp(units, limits.MinHeight, limits.MaxHeight));
                    var chrome = ResolveDecoration(stat, fitted);
                    var inset = StatContentInset(chrome, fitted, content);
                    wanted = new Size(content.Width + 2f * inset, height);
                    shadow = chrome.ShadowOutsets;
                    break;
                default:
                    throw new ArgumentException("The leaf is not a status widget.", nameof(node));
            }
            return new UiLayoutLeafMeasurement(new UiLayoutExtent(ToReferenceUnits(wanted.Width, limits.MaxWidth),
                ToReferenceUnits(wanted.Height, limits.MaxHeight)), UiLayoutPaintKind.Protected, shadow);
        }

        private (Size Size, float Cue, Size Digits) StatusRow(string text, UiTextStyle style, bool reserveCue, float width,
            UiCountdownFormat? format = null)
        {
            var cue = reserveCue ? ArtGlyphSize : 0f;
            var digits = format.HasValue
                ? MeasureText(MaximumCountdownDigits[(int)format.Value], UiTextStyle.Mono, float.PositiveInfinity, wrap: false)
                : default;
            var extra = (cue > 0f ? cue + LabelGap : 0f) + digits.Width + (text.Length > 0 && digits.Width > 0 ? LabelGap : 0f);
            var available = width - extra;
            var caption = MeasureText(text, style, available, wrap: false);
            if (available > 0f && caption.Width > available)
            {
                var wrapped = MeasureText(text, style, available);
                caption = new Size(Math.Max(available, wrapped.Width), wrapped.Height);
            }
            return (new Size(caption.Width + extra, Math.Max(Math.Max(caption.Height, digits.Height), cue)), cue, digits);
        }

        private void PaintLegacyCountdown(UiCountdown timer, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var seconds = surface.CapturedCountdownSeconds(timer);
            var value = UiCountdownFormatting.Text(timer.Label, seconds, timer.Format);
            var pair = PairFor(backdrop, timer.Tone);
            Add(path, timer.Id, nameof(UiCountdown), bounds, clip, TextContent(value, UiTextStyle.Body,
                UiCaptureNodeFlags.ScrollableClip).WithStatus("normal", remainingSeconds: seconds), new UiCaptureNodePaint(pair.On, pair.Fill));
        }

        private void PaintStatusCountdown(UiCountdown timer, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var remaining = surface.CapturedCountdownRemaining(timer);
            var seconds = UiCountdownFormatting.WholeSeconds(remaining);
            var digits = UiCountdownFormatting.Digits(seconds, timer.Format);
            var status = UiStatusPresentation.For(timer, remaining);
            var row = StatusRow(timer.Label, UiTextStyle.Label, timer.Thresholds != null, bounds.Width, timer.Format);
            var pair = PairFor(backdrop, status.Tone);
            var start = bounds.X + (row.Cue > 0f ? row.Cue + LabelGap : 0f);
            var digitsLeft = bounds.X + row.Size.Width - row.Digits.Width;
            var flags = UiCaptureNodeFlags.ScrollableClip | UiCaptureNodeFlags.RequiresLabel;
            var label = UiStatusFormatting.WithLabel(timer.Label, digits);
            Add(path, timer.Id, nameof(UiCountdown), bounds, clip, StatusGroup(label, status, remainingSeconds: seconds), default);
            if (timer.Label.Length != 0)
            {
                var caption = new UiCaptureRect(start, bounds.Y, Math.Max(0f, digitsLeft - LabelGap - start), row.Size.Height);
                Add(path + "/label", null, "CountdownLabel", caption, clip, TextContent(timer.Label, UiTextStyle.Label, flags),
                    new UiCaptureNodePaint(pair.On, pair.Fill));
            }
            var readout = new UiCaptureRect(digitsLeft, bounds.Y, row.Digits.Width, row.Size.Height);
            Add(path + "/digits", null, "CountdownDigits", readout, clip, TextContent(digits, UiTextStyle.Mono, flags),
                new UiCaptureNodePaint(pair.On, pair.Fill));
            PaintStatusCue(path, status, CenterVertically(bounds.X, row.Cue, row.Cue, bounds.Y, row.Size.Height), clip, pair);
        }

        private void PaintStatusProgress(UiProgress progress, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var status = UiStatusPresentation.For(progress);
            var row = StatusRow(progress.DisplayLabel, UiTextStyle.Body, true, bounds.Width);
            var colors = PairFor(backdrop, status.Tone);
            Add(path, progress.Id, nameof(UiProgress), bounds, clip, StatusGroup(progress.DisplayLabel, status,
                fraction: progress.Fraction), default);
            var start = row.Cue + LabelGap;
            var caption = new UiCaptureRect(bounds.X + start, bounds.Y, Math.Max(0f, row.Size.Width - start), row.Size.Height);
            Add(path + "/label", null, "ProgressLabel", caption, clip, TextContent(progress.DisplayLabel, UiTextStyle.Body,
                UiCaptureNodeFlags.RequiresLabel | UiCaptureNodeFlags.ScrollableClip), new UiCaptureNodePaint(colors.On, colors.Fill));
            PaintStatusCue(path, status, CenterVertically(bounds.X, row.Cue, row.Cue, bounds.Y, row.Size.Height), clip, colors);

            var height = ProgressTrackHeight * layoutScale;
            var track = new UiCaptureRect(bounds.X, bounds.Bottom - height, bounds.Width, height);
            var meter = UiStatusMeterPaint.Resolve(scheme, status.Tone, preferences.HighContrast);
            Add(path + "/track", null, "Track", track, clip, EmptyContent, new UiCaptureNodePaint(null, meter.Empty.Fill, height / 2f, 0f, 0f, 0f));
            var fill = new UiCaptureRect(track.X, track.Y, track.Width * progress.Fraction, height);
            Add(path + "/fill", null, "TrackFill", fill, clip, EmptyContent, new UiCaptureNodePaint(null, meter.Filled.Fill, height / 2f, 0f, 0f, 0f));
        }

        private void PaintStatBar(UiStatBar stat, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var status = UiStatusPresentation.For(stat);
            var meter = UiStatusMeterPaint.Resolve(scheme, status.Tone, preferences.HighContrast);
            var extent = new UiLayoutExtent(bounds.Width / (double)layoutScale, bounds.Height / (double)layoutScale);
            var chrome = ResolveDecoration(stat, extent);
            var radius = (float)(chrome.CornerRadius * layoutScale);
            var border = (float)(chrome.BorderWidth * layoutScale);
            Add(path, stat.Id, nameof(UiStatBar), bounds, clip, StatusGroup(stat.DisplayLabel, status, fraction: stat.Fraction),
                new UiCaptureNodePaint(null, meter.Empty.Fill, radius, border,
                    (float)(chrome.ShadowX * layoutScale), (float)(chrome.ShadowY * layoutScale)));
            var width = bounds.Width * stat.Fraction;
            var filled = new UiCaptureRect(bounds.X, bounds.Y, width, bounds.Height);
            var empty = new UiCaptureRect(filled.Right, bounds.Y, bounds.Width - width, bounds.Height);
            Add(path + "/fill", null, "StatFill", filled, clip, EmptyContent, new UiCaptureNodePaint(null, meter.Filled.Fill, radius));
            if (border > 0f)
            {
                Add(path + "/boundary", null, "StatBoundary", bounds, clip, EmptyContent.WithFlags(
                    UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.BoundaryMark),
                    new UiCaptureNodePaint(scheme.ResolveBoundary(backdrop), scheme.GetPair(backdrop).Fill, radius, border));
            }

            var cue = stat.Thresholds == null ? 0f : ArtGlyphSize;
            var accessory = cue > 0f ? cue + LabelGap : 0f;
            var size = MeasureText(stat.DisplayLabel, UiTextStyle.Label, float.PositiveInfinity, wrap: false);
            var inset = StatContentInset(chrome, extent, StatContentSize(size, cue));
            var labelBox = bounds.Inset(inset + accessory, 0f, inset, 0f);
            var text = CenterVertically(labelBox.X + (labelBox.Width - size.Width) / 2f, size.Width, size.Height, labelBox.Y, labelBox.Height);
            var cueBox = CenterVertically(bounds.X + inset, cue, cue, bounds.Y, bounds.Height);
            var flags = UiCaptureNodeFlags.RequiresLabel | UiCaptureNodeFlags.ScrollableClip;
            var content = TextContent(stat.DisplayLabel, UiTextStyle.Label, flags);
            if (!labelBox.Contains(text))
            {
                nodes.Add(new UiCapturedNode(path + "/label-overflow", null, "StatLabelOverflow", text,
                    content.WithFlags(UiCaptureNodeFlags.LayoutDiagnostic), default, theme.Descriptor.Id,
                    theme.UsedFallback, isRendered: false, clipped: true, occluded: false));
                return;
            }

            PaintStatSide(path + "/filled", "StatFilledLabel", filled, meter.Filled);
            PaintStatSide(path + "/empty", "StatEmptyLabel", empty, meter.Empty);

            void PaintStatSide(string side, string kind, UiCaptureRect mask, UiThemePair pair)
            {
                // The masks split an already positioned phrase. Only the visible portion belongs to each paint group.
                var visibleText = text.Intersect(mask);
                if (visibleText.Width > 0f)
                    Add(side, null, kind, visibleText, clip, content, new UiCaptureNodePaint(pair.On, pair.Fill));
                PaintStatusCue(side, status, cueBox.Intersect(mask), clip, pair);
            }
        }

        private void PaintStatusCue(string path, UiStatusPresentation status, UiCaptureRect bounds, UiCaptureRect clip, UiThemePair pair)
        {
            if (status.Glyph is not UiGlyph glyph || bounds.Width <= 0f) return;
            Add(path + "/cue:" + glyph, null, "StatusCue", bounds, clip, new UiCaptureNodeContent(string.Empty,
                UiGlyphLabels.Get(glyph), 0f, 0f, UiCaptureNodeFlags.Decorative | UiCaptureNodeFlags.SemanticMark),
                new UiCaptureNodePaint(pair.On, pair.Fill));
        }

        private static UiCaptureNodeContent StatusGroup(string label, UiStatusPresentation status,
            double? remainingSeconds = null, float? fraction = null)
        {
            var flags = UiCaptureNodeFlags.RequiresLabel | UiCaptureNodeFlags.ScrollableClip;
            if (status.Level != UiStatusLevel.Normal) flags |= UiCaptureNodeFlags.RequiresNonColorCue;
            if (status.Glyph.HasValue) flags |= UiCaptureNodeFlags.HasNonColorCue;
            var state = status.Level switch
            {
                UiStatusLevel.Warning => "warning",
                UiStatusLevel.Critical => "critical",
                _ => "normal",
            };
            return new UiCaptureNodeContent(string.Empty, label, 0f, 0f, flags, state: state,
                remainingSeconds: remainingSeconds, fraction: fraction);
        }

        private Size StatContentSize(Size caption, float cue) =>
            new Size(caption.Width + (cue > 0f ? cue + LabelGap : 0f), Math.Max(caption.Height, cue));

        private float StatContentInset(UiLayoutDecoration chrome, UiLayoutExtent bounds, Size content)
        {
            var units = new UiLayoutExtent(content.Width / (double)layoutScale, content.Height / (double)layoutScale);
            return (float)(UiStatBarLayout.ContentInset(chrome, bounds, units) * layoutScale) + LabelGap;
        }

        private static UiCaptureRect CenterVertically(float x, float width, float height, float y, float extent) =>
            new UiCaptureRect(x, y + (extent - height) / 2f, width, height);
    }
}
