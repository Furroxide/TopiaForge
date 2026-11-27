using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Validates the numeric content and screen geometry observed by the native status recorder.</summary>
    internal static class UiGalleryStatusEvidence
    {
        private const double PixelTolerance = 1d;

        internal static bool TryReadCountdown(string? visible, string caption, UiCountdownFormat format,
            bool uppercase, out int seconds)
        {
            seconds = 0;
            if (visible == null) return false;
            var text = visible.AsSpan();
            if (caption.Length != 0)
            {
                if (text.Length <= caption.Length || text[caption.Length] != ' ' ||
                    (!text[..caption.Length].SequenceEqual(caption.AsSpan()) &&
                        (!uppercase || !text[..caption.Length].SequenceEqual(caption.ToUpperInvariant().AsSpan())))) return false;
                text = text[(caption.Length + 1)..];
            }

            if (text.Length == 0 || text.Length > UiCountdownFormatting.MaximumLength) return false;
            var firstColon = text.IndexOf(':');
            long total;
            if (format == UiCountdownFormat.Seconds)
            {
                if (!Whole(text, out var whole)) return false;
                total = whole;
            }
            else
            {
                if (firstColon < 1 || !Whole(text[..firstColon], out var first)) return false;
                var rest = text[(firstColon + 1)..];
                if (format == UiCountdownFormat.MinutesSeconds)
                {
                    if (!Pair(rest, out var tail)) return false;
                    total = first * 60L + tail;
                }
                else if (format == UiCountdownFormat.HoursMinutesSeconds)
                {
                    if (rest.Length != 5 || rest[2] != ':' || !Pair(rest[..2], out var minutes) || !Pair(rest[3..], out var tail))
                        return false;
                    total = first * 3600L + minutes * 60L + tail;
                }
                else return false;
            }

            if (total > int.MaxValue || !text.SequenceEqual(UiCountdownFormatting.Digits((int)total, format).AsSpan())) return false;
            seconds = (int)total;
            return true;
        }

        internal static bool SamePaintBounds(UiLayoutRect first, UiLayoutRect second) =>
            Finite(first) && Finite(second) && Near(first.X, second.X) && Near(first.Y, second.Y) &&
            Near(first.Right, second.Right) && Near(first.Bottom, second.Bottom);

        internal static bool TryMeasureFill(UiLayoutRect track, UiLayoutRect fill, float advertised, out float fraction)
        {
            fraction = 0f;
            if (!(advertised >= 0f && advertised <= 1f) || !Finite(track) || !Finite(fill) ||
                track.Width <= 0d || track.Height <= 0d || fill.Width < 0d ||
                !Near(track.X, fill.X) || !Near(track.Y, fill.Y) || !Near(track.Bottom, fill.Bottom) ||
                !Near(fill.Width, track.Width * advertised)) return false;
            fraction = (float)Math.Min(1d, fill.Width / track.Width);
            return true;
        }

        internal static bool TryMeasureSplit(UiLayoutRect track, UiLayoutRect filled, UiLayoutRect empty, float advertised,
            out float fraction)
        {
            fraction = 0f;
            return Finite(empty) && empty.Width >= 0d && Near(filled.Right, empty.X) && Near(track.Right, empty.Right) &&
                Near(track.Y, empty.Y) && Near(track.Bottom, empty.Bottom) && TryMeasureFill(track, filled, advertised, out fraction);
        }

        private static bool Whole(ReadOnlySpan<char> text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;

        private static bool Pair(ReadOnlySpan<char> text, out int value)
        {
            value = 0;
            return text.Length == 2 && Whole(text, out value) && value < 60;
        }

        private static bool Finite(UiLayoutRect box) => double.IsFinite(box.X) && double.IsFinite(box.Y) &&
            double.IsFinite(box.Right) && double.IsFinite(box.Bottom);
        private static bool Near(double first, double second) => Math.Abs(first - second) <= PixelTolerance;
    }
}
