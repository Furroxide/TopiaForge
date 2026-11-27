using System;
using System.Collections.Generic;
using System.Globalization;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        private const int MaxShownToasts = 4;
        private const int MaxWaitingToasts = 64;
        private const double ToastLifetime = 3.5d;
        private const float ToastWidth = 340f;
        private const float ToastHeight = 44f;
        private const float ToastInsetX = 14f;
        private const float ToastInsetY = 12f;
        private const float ToastGap = 8f;
        private const float ToastGlyph = 20f;
        private static readonly UiCaptureViewport DefaultToastViewport = new UiCaptureViewport(1920, 1080);
        private UiCaptureViewport toastViewport = DefaultToastViewport;
        private List<ToastPresentation>? shownToasts;
        private Queue<FakeToast>? waitingToasts;
        private ToastFrame lastToastFrame;
        private ToastPresentation preparedToast;
        private double lastToastTime;

        private void QueueToast(FakeToast toast)
        {
            if (shownToasts == null)
            {
                shownToasts = new List<ToastPresentation>(MaxShownToasts);
                waitingToasts = new Queue<FakeToast>(MaxWaitingToasts);
                lifetime.StoppingToken.Register(StopToasts);
            }

            var now = ClockTime;
            var frame = ToastViewport();
            AdvanceToasts(now, frame);
            toasts.Add(toast);
            if (waitingToasts!.Count == MaxWaitingToasts) waitingToasts.Dequeue();
            waitingToasts.Enqueue(toast);
            AdvanceToasts(now, frame);
        }

        internal void AdvanceToasts()
        {
            if (shownToasts?.Count > 0 || waitingToasts?.Count > 0) AdvanceToasts(ClockTime, ToastViewport());
        }

        private void AdvanceToasts(double now, ToastFrame frame)
        {
            var measuresChanged = lastToastFrame.Scale != frame.Scale || lastToastFrame.Width != frame.Width ||
                lastToastFrame.ReadableScale != frame.ReadableScale;
            var elapsed = Math.Max(0d, now - lastToastTime);
            lastToastFrame = frame;
            lastToastTime = now;
            var exit = MotionSeconds(UiThemePackParser.Default.Motion.BaseMs);
            var bottom = frame.Bottom;
            var room = frame.Width > 0f;
            for (var index = 0; index < shownToasts!.Count;)
            {
                var entry = shownToasts[index];
                if (measuresChanged) entry.Measure = frame.Measure(entry.Toast!);
                var fits = room && bottom >= entry.Measure.Height;
                if (fits && !entry.Parked) entry.Remaining -= elapsed;
                if (entry.Remaining <= -exit || (!fits && entry.Remaining <= 0d))
                {
                    shownToasts.RemoveAt(index);
                    continue;
                }
                entry.Parked = !fits;
                shownToasts[index++] = entry;
                room = fits;
                if (fits) bottom = bottom - entry.Measure.Height - ToastGap * frame.Scale;
            }

            if (measuresChanged) preparedToast = default;
            while (room && shownToasts.Count < MaxShownToasts && waitingToasts!.Count != 0)
            {
                var next = waitingToasts.Peek();
                if (!ReferenceEquals(preparedToast.Toast, next))
                    preparedToast = new ToastPresentation(next, frame.Measure(next));
                if (bottom < preparedToast.Measure.Height) break;
                shownToasts.Add(preparedToast);
                bottom = bottom - preparedToast.Measure.Height - ToastGap * frame.Scale;
                waitingToasts.Dequeue();
                preparedToast = default;
            }
        }

        private void StopToasts()
        {
            shownToasts?.Clear();
            waitingToasts?.Clear();
            preparedToast = default;
        }

        internal UiTreeCapture CaptureToasts(UiCaptureViewport viewport)
        {
            if (viewport == null) throw new ArgumentNullException(nameof(viewport));
            gameThread.AssertCurrent();
            toastViewport = viewport;
            var draws = new List<UiCapturedNode>();
            if (shownToasts == null) return new UiTreeCapture(draws, Accessibility.HighContrast);
            var frame = ToastViewport();
            AdvanceToasts(ClockTime, frame);

            var theme = UiThemePackParser.Default;
            var scheme = theme.SelectScheme(hud: true, Accessibility.HighContrast);
            var scale = frame.Scale;
            var readableScale = frame.ReadableScale;
            var screen = new UiCaptureRect(0, 0, viewport.Width, viewport.Height);
            var bottom = frame.Bottom;

            for (var index = 0; index < shownToasts.Count; index++)
            {
                var entry = shownToasts[index];
                if (entry.Parked) break;
                var toast = entry.Toast!;
                var glyph = entry.Measure.Glyph;
                var leading = glyph.HasValue ? (ToastGlyph + ToastGap) * readableScale : 0f;
                var text = entry.Measure.Text;
                var size = entry.Measure.Label;
                var height = entry.Measure.Height;
                var panel = new UiCaptureRect(viewport.Width - frame.Margin - frame.Width, bottom - height, frame.Width, height);
                bottom = panel.Y - ToastGap * scale;
                var path = "/toast/" + index.ToString(CultureInfo.InvariantCulture);
                const UiPaintRole plate = UiPaintRole.Raised;
                var pair = scheme.GetPair(plate);
                var flags = UiCaptureNodeFlags.RequiresLabel;
                if (glyph.HasValue) flags |= UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                var role = theme.GetText(UiTextStyle.Label);
                var expected = role.ReferenceSize * readableScale;
                var drawn = ResolveTextMetrics(theme, role.Font).PredictHeight(expected);
                var label = role.IsUppercase ? text.ToUpperInvariant() : text;
                var labelBounds = new UiCaptureRect(panel.X + ToastInsetX * scale + leading,
                    panel.Y + (height - size.Height) / 2f, size.Width, size.Height);
                draws.Add(new UiCapturedNode(path, null, "Toast", panel,
                    new UiCaptureNodeContent(string.Empty, toast.Message, 0f, 0f, flags),
                    new UiCaptureNodePaint(null, pair.Fill, Math.Min(theme.Shape.Control * scale, panel.Height / 2f),
                        (Accessibility.HighContrast ? 3f : 2f) * scale, 0f, 0f),
                    theme.Id, false, true, !screen.Contains(panel), false));
                draws.Add(new UiCapturedNode(path + "/label", null, "ToastLabel", labelBounds,
                    new UiCaptureNodeContent(label, toast.Message, drawn, expected, UiCaptureNodeFlags.RequiresLabel),
                    new UiCaptureNodePaint(pair.On, pair.Fill), theme.Id, false, true, !panel.Contains(labelBounds), false));
                if (glyph is { } mark)
                {
                    var edge = ToastGlyph * readableScale;
                    var markBounds = new UiCaptureRect(panel.X + ToastInsetX * scale, panel.Y + (height - edge) / 2f, edge, edge);
                    draws.Add(new UiCapturedNode(path + "/glyph", null, "ToastGlyph", markBounds,
                        new UiCaptureNodeContent(string.Empty, mark.ToString(), 0f, 0f, UiCaptureNodeFlags.SemanticMark),
                        new UiCaptureNodePaint(scheme.ResolveForeground(toast.Tone, plate), pair.Fill), theme.Id, false, true, false, false));
                }
            }
            return new UiTreeCapture(draws, Accessibility.HighContrast);
        }

        private ToastFrame ToastViewport()
        {
            var viewport = toastViewport;
            var screenScale = viewport.ScreenScale;
            var scale = screenScale * Accessibility.UiScale;
            var margin = UiHudViewport.SafeMargin * scale;
            var bottom = viewport.Height - margin;
            var safe = UiHudViewport.SafeArea(new UiLayoutRect(0d, 0d, viewport.Width, viewport.Height), scale);
            var used = 0d;
            foreach (var surface in surfaces)
            {
                var request = surface.Request;
                if (request.Kind != UiSurfaceKind.Hud || request.HudAnchor != UiHudAnchor.BottomRight || !surface.IsVisible) continue;
                var height = request.Height * scale;
                var bounds = UiHudViewport.Fit(safe, safe.Right, safe.Bottom - used, 1d, 1d,
                    new UiLayoutExtent(request.Width * scale, height));
                bottom = Math.Min(bottom, (float)bounds.Y - ToastGap * scale);
                used += height + UiHudViewport.SlotGap * scale;
            }
            return new ToastFrame(scale, screenScale * Math.Max(1f, Accessibility.UiScale), margin,
                Math.Min(ToastWidth * scale, viewport.Width - 2f * margin), bottom);
        }

        private struct ToastPresentation
        {
            internal ToastPresentation(FakeToast toast, ToastMeasure measure)
            {
                Toast = toast;
                Measure = measure;
                Remaining = ToastLifetime;
                Parked = false;
            }

            internal readonly FakeToast? Toast;
            internal ToastMeasure Measure;
            internal double Remaining;
            internal bool Parked;
        }

        private readonly struct ToastFrame
        {
            internal ToastFrame(float scale, float readableScale, float margin, float width, float bottom)
            {
                Scale = scale;
                ReadableScale = readableScale;
                Margin = margin;
                Width = width;
                Bottom = bottom;
            }

            internal float Scale { get; }
            internal float ReadableScale { get; }
            internal float Margin { get; }
            internal float Width { get; }
            internal float Bottom { get; }

            internal ToastMeasure Measure(FakeToast toast)
            {
                var glyph = toast.Tone switch
                {
                    UiTone.Success => UiGlyph.Check,
                    UiTone.Warning => UiGlyph.Warning,
                    UiTone.Danger => UiGlyph.Cross,
                    _ => (UiGlyph?)null,
                };
                var leading = glyph.HasValue ? (ToastGlyph + ToastGap) * ReadableScale : 0f;
                var available = Width - 2f * ToastInsetX * Scale - leading;
                var theme = UiThemePackParser.Default;
                var text = VisibleToastText(toast.Message, theme, available, ReadableScale);
                var label = FakeUiTreeCapture.MeasurePlainText(text, UiTextStyle.Label, available, theme, ReadableScale);
                var textArea = Math.Max(label.Height, glyph.HasValue ? ToastGlyph * ReadableScale : 0f);
                return new ToastMeasure(glyph, text, label, Math.Max(ToastHeight * Scale, textArea + 2f * ToastInsetY * Scale));
            }
        }

        private readonly struct ToastMeasure
        {
            internal ToastMeasure(UiGlyph? glyph, string text, FakeUiTreeCapture.Size label, float height)
            {
                Glyph = glyph;
                Text = text;
                Label = label;
                Height = height;
            }

            internal UiGlyph? Glyph { get; }
            internal string Text { get; }
            internal FakeUiTreeCapture.Size Label { get; }
            internal float Height { get; }
        }

        private static string VisibleToastText(string text, UiThemeDescriptor theme, float width, float scale)
        {
            if (FakeUiTreeCapture.MeasurePlainText(text, UiTextStyle.Label, width, theme, scale).Lines <= 3)
                return text;
            var low = 0;
            var high = text.Length - 1;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                var prefix = LimitToastText(text, mid) + "…";
                if (FakeUiTreeCapture.MeasurePlainText(prefix, UiTextStyle.Label, width, theme, scale).Lines <= 3) low = mid;
                else high = mid - 1;
            }
            return LimitToastText(text, low) + "…";
        }

        private static string LimitToastText(string text, int length)
        {
            if (text.Length <= length) return text;
            if (length > 0 && char.IsSurrogatePair(text, length - 1)) length--;
            return text.Substring(0, length);
        }
    }
}
