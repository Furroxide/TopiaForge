using System;
using System.Globalization;

namespace TopiaForge.Mods
{
    /// <summary>Specifies how a countdown shows its remaining whole seconds.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public enum UiCountdownFormat
    {
        /// <summary>Shows whole seconds.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        Seconds = 0,

        /// <summary>Shows total minutes and two-digit seconds.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        MinutesSeconds = 1,

        /// <summary>Shows total hours, two-digit minutes and two-digit seconds.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        HoursMinutesSeconds = 2
    }

    internal static class UiCountdownFormatting
    {
        // int.MaxValue seconds fits in "596523:14:07", the longest of the three formats.
        internal const int MaximumLength = 12;
        private const string DigitPairs = "000102030405060708091011121314151617181920212223242526272829" +
            "303132333435363738394041424344454647484950515253545556575859";

        internal static int WholeSeconds(double remainingTime) => (int)Math.Min(int.MaxValue, Math.Ceiling(remainingTime));

        internal static string Digits(int seconds, UiCountdownFormat format)
        {
            Span<char> digits = stackalloc char[MaximumLength];
            var size = WriteDigits(seconds, format, digits);
            return new string(digits[..size]);
        }

        internal static string Text(string label, int seconds, UiCountdownFormat format) =>
            UiStatusFormatting.WithLabel(label, Digits(seconds, format));

        internal static int WriteDigits(int seconds, UiCountdownFormat format, Span<char> destination)
        {
            if (seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
            var leading = format switch
            {
                UiCountdownFormat.Seconds => seconds,
                UiCountdownFormat.MinutesSeconds => seconds / 60,
                UiCountdownFormat.HoursMinutesSeconds => seconds / 3600,
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };
            if (!leading.TryFormat(destination, out var written, provider: CultureInfo.InvariantCulture))
                throw new ArgumentException("The countdown digit buffer is too small.", nameof(destination));

            if (format == UiCountdownFormat.HoursMinutesSeconds)
                written = AppendPair(destination, written, seconds / 60 % 60);
            if (format != UiCountdownFormat.Seconds)
                written = AppendPair(destination, written, seconds % 60);
            return written;
        }

        private static int AppendPair(Span<char> destination, int offset, int value)
        {
            destination[offset] = ':';
            DigitPairs.AsSpan(value * 2, 2).CopyTo(destination[(offset + 1)..]);
            return offset + 3;
        }
    }

    /// <summary>Owns the reusable render buffer for a countdown without changing its admitted tree.</summary>
    internal sealed class UiCountdownDisplay
    {
        private readonly UiCountdown countdown;
        private int seconds = -1;

        internal UiCountdownDisplay(UiCountdown countdown) => this.countdown = countdown;

        internal char[] Digits { get; } = new char[UiCountdownFormatting.MaximumLength];
        internal int Length { get; private set; }
        internal UiStatusPresentation Status { get; private set; }

        internal (bool DigitsChanged, bool StatusChanged) Update(double remainingTime)
        {
            var wholeSeconds = UiCountdownFormatting.WholeSeconds(remainingTime);
            var digitsChanged = seconds != wholeSeconds;
            if (digitsChanged)
            {
                Length = UiCountdownFormatting.WriteDigits(wholeSeconds, countdown.Format, Digits);
                seconds = wholeSeconds;
            }

            var next = UiStatusPresentation.For(countdown, remainingTime);
            if (next.Level == Status.Level && next.Tone == Status.Tone) return (digitsChanged, false);

            Status = next;
            return (digitsChanged, true);
        }
    }
}
