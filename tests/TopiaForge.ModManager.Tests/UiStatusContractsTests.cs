using System;
using System.Globalization;
using TopiaForge.Mods;
using TopiaForge.TestSupport;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiStatusContractsTests
    {
        internal static void Run()
        {
            LegacyConstructorsKeepTheirDefaults();
            ThresholdsValidateAndResolveInclusiveStates();
            ProgressKeepsAdjacentThresholdsDistinct();
            StatBarsAndProgressFormatVisibleValues();
            CountdownFormatsUseAReusableDigitBuffer();
            Console.WriteLine("UI status contracts passed: legacy overloads, thresholds, value formats and allocation-free countdown digits.");
        }

        private static void LegacyConstructorsKeepTheirDefaults()
        {
            var legacyProgress = new UiProgress(.5f);
            var labelledProgress = new UiProgress(.5f, "Loading", UiTone.Warning, "progress");
            var namedProgress = new UiProgress(.5f, id: "named", tone: UiTone.Danger);
            var defaultProgress = new UiProgress(.5f, "Loading", default);
            Require(legacyProgress is { Label: "", Tone: UiTone.Neutral, Thresholds: null, DisplayLabel: "" } &&
                labelledProgress is { Label: "Loading", DisplayLabel: "Loading", Id: "progress", Tone: UiTone.Warning, Thresholds: null } &&
                namedProgress.Id == "named" && defaultProgress is { Tone: UiTone.Neutral, Thresholds: null },
                "The legacy progress constructors keep the caption and tone unchanged.");

            var legacyCountdown = new UiCountdown(90);
            var labelledCountdown = new UiCountdown(90, "Time", UiTone.Warning, "timer");
            var namedCountdown = new UiCountdown(90, id: "named", tone: UiTone.Danger);
            var defaultCountdown = new UiCountdown(90, "Time", default);
            var zeroCountdown = new UiCountdown(90, "Time", 0);
            var explicitSeconds = UiCountdown.WithFormat(90, "Time", UiCountdownFormat.Seconds, tone: UiTone.Warning, id: "timer");
            Require(legacyCountdown is { Label: "", Tone: UiTone.Neutral, Format: UiCountdownFormat.Seconds, Thresholds: null } &&
                labelledCountdown is { Label: "Time", Id: "timer", Tone: UiTone.Warning, UsesStatusFormat: false } &&
                namedCountdown.Id == "named" && !explicitSeconds.UsesStatusFormat &&
                defaultCountdown is { Tone: UiTone.Neutral, UsesStatusFormat: false } &&
                zeroCountdown is { Tone: UiTone.Neutral, UsesStatusFormat: false },
                "A seconds countdown without thresholds stays equivalent to the legacy render and wire form.");
        }

        private static void ThresholdsValidateAndResolveInclusiveStates()
        {
            var below = new UiThresholds(10, 5);
            var above = new UiThresholds(.85, .95, UiThresholdDirection.AtOrAbove);
            Require(below.ToneFor(10.01, UiTone.Primary) == UiTone.Primary &&
                below.ToneFor(10, UiTone.Primary) == UiTone.Warning && below.ToneFor(5, UiTone.Primary) == UiTone.Danger &&
                below.ToneFor(-1, UiTone.Primary) == UiTone.Danger, "At-or-below is inclusive and critical takes priority.");
            Require(above.ToneFor(.84, UiTone.Muted) == UiTone.Muted &&
                above.ToneFor(.85, UiTone.Muted) == UiTone.Warning && above.ToneFor(.95, UiTone.Muted) == UiTone.Danger &&
                above.ToneFor(1, UiTone.Muted) == UiTone.Danger, "At-or-above is inclusive and critical takes priority.");
            Reject(() => new UiThresholds(double.NaN, 0), "warning");
            Reject(() => new UiThresholds(1, double.NegativeInfinity), "critical");
            Reject(() => new UiThresholds(10, 5, (UiThresholdDirection)(-1)), "direction");
            Reject(() => new UiThresholds(5, 5), "warning");
            Reject(() => new UiThresholds(5, 10), "warning");
            Reject(() => new UiThresholds(10, 5, UiThresholdDirection.AtOrAbove), "warning");
            Reject(() => below.ToneFor(double.PositiveInfinity, UiTone.Neutral), "value");
            Reject(() => below.ToneFor(1, (UiTone)27), "baseTone");
            Reject(() => UiProgress.WithThresholds(.5f, "", (UiThresholds)null!), "thresholds");
            Reject(() => UiProgress.WithThresholds(.5f, "", new UiThresholds(1.01, .5)), "thresholds");
            Reject(() => UiProgress.WithThresholds(.5f, "", new UiThresholds(.5, -.01)), "thresholds");
            Reject(() => UiCountdown.WithFormat(90, "", UiCountdownFormat.Seconds, above), "thresholds");
            Reject(() => UiCountdown.WithFormat(90, "", (UiCountdownFormat)3), "format");

            var quarter = new UiThresholds(.85, .65);
            Require(UiStatusPresentation.For(UiProgress.WithThresholds(.95f, "Load", above)) is
            { Level: UiStatusLevel.Critical, Tone: UiTone.Danger, Glyph: UiGlyph.Cross } &&
                UiStatusPresentation.For(UiProgress.WithThresholds(.85f, "Load", above)) is
                { Level: UiStatusLevel.Warning, Tone: UiTone.Warning, Glyph: UiGlyph.Warning } &&
                UiStatusPresentation.For(UiProgress.WithThresholds(.85f, "Supply", quarter)) is
                { Level: UiStatusLevel.Warning, Tone: UiTone.Warning, Glyph: UiGlyph.Warning } &&
                UiStatusPresentation.For(UiProgress.WithThresholds(.4f, "Load", above)) is
                { Level: UiStatusLevel.Normal, Tone: UiTone.Accent, Glyph: null },
                "Progress reaches authored decimal boundaries at the float precision of its data and uses redundant glyphs.");
        }

        private static void ProgressKeepsAdjacentThresholdsDistinct()
        {
            const float center = .5f;
            var before = MathF.BitDecrement(center);
            var after = MathF.BitIncrement(center);
            var above = new UiThresholds(.5d, Math.BitIncrement(.5d), UiThresholdDirection.AtOrAbove);
            var below = new UiThresholds(.5d, Math.BitDecrement(.5d));
            var rising = UiProgress.WithThresholds(center, "Load", above);
            var falling = UiProgress.WithThresholds(center, "Supply", below);

            Require(UiStatusPresentation.For(UiProgress.WithThresholds(before, "Load", above)).Level == UiStatusLevel.Normal &&
                UiStatusPresentation.For(rising) is { Level: UiStatusLevel.Warning, Tone: UiTone.Warning, Glyph: UiGlyph.Warning } &&
                UiStatusPresentation.For(UiProgress.WithThresholds(after, "Load", above)) is { Level: UiStatusLevel.Critical, Glyph: UiGlyph.Cross },
                "Rising progress preserves the warning at .5f when the critical threshold is the next double.");
            Require(UiStatusPresentation.For(UiProgress.WithThresholds(after, "Supply", below)).Level == UiStatusLevel.Normal &&
                UiStatusPresentation.For(falling) is { Level: UiStatusLevel.Warning, Tone: UiTone.Warning, Glyph: UiGlyph.Warning } &&
                UiStatusPresentation.For(UiProgress.WithThresholds(before, "Supply", below)) is { Level: UiStatusLevel.Critical, Glyph: UiGlyph.Cross },
                "Falling progress preserves the warning at .5f when the critical threshold is the preceding double.");

            var allocated = AllocationProbe.Measure("settled adjacent progress thresholds", () =>
                Require(UiStatusPresentation.For(rising).Level == UiStatusLevel.Warning, "The settled progress stays at warning."),
                frames: 10_000, warmups: 64);
            Require(allocated == 0, "Resolving progress with adjacent thresholds allocates zero bytes at rest.");
        }

        private static void StatBarsAndProgressFormatVisibleValues()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                var thresholds = new UiThresholds(50, 25);
                Require(UiProgress.WithThresholds(.6f, "Download", new UiThresholds(.4, .2)) is
                { DisplayLabel: "Download 60%", Tone: UiTone.Accent } &&
                    UiProgress.WithThresholds(.125f, "", new UiThresholds(.4, .2)).DisplayLabel == "13%" &&
                    new UiStatBar("INTEGRITY", 87, 100, thresholds).DisplayLabel == "INTEGRITY 87" &&
                    new UiStatBar("INTEGRITY", 87, 100, thresholds, UiStatFormat.ValueOfMaximum).DisplayLabel == "INTEGRITY 87 / 100" &&
                    new UiStatBar("INTEGRITY", 7, 8, format: UiStatFormat.Percent).DisplayLabel == "INTEGRITY 88%" &&
                    new UiStatBar("Capacity", 1_000_000, 1_000_000, format: UiStatFormat.Percent).DisplayLabel == "Capacity 100%",
                    "Status formats show the visible label and value using invariant whole numbers.");
                Require(UiStatusPresentation.For(new UiStatBar("Integrity", 50, 100, thresholds)) is
                { Level: UiStatusLevel.Warning, Glyph: UiGlyph.Warning } &&
                    UiStatusPresentation.For(new UiStatBar("Integrity", 25, 100, thresholds)) is
                    { Level: UiStatusLevel.Critical, Glyph: UiGlyph.Cross } &&
                    UiStatusPresentation.For(new UiStatBar("Integrity", 51, 100, thresholds)) is
                    { Level: UiStatusLevel.Normal, Tone: UiTone.Success, Glyph: null },
                    "Stat bar thresholds compare the actual value instead of its fraction.");
                var exact = new UiThresholds(50d, Math.BitIncrement(50d), UiThresholdDirection.AtOrAbove);
                Require(UiStatusPresentation.For(new UiStatBar("Integrity", 50, 100, exact)) is
                { Level: UiStatusLevel.Warning, Glyph: UiGlyph.Warning },
                    "Stat bar thresholds preserve the authored boundary immediately above an integer value.");
                Require(new UiStatBar("Capacity", 0, 1_000_000).Fraction == 0f &&
                    new UiStatBar("Capacity", 1_000_000, 1_000_000).Fraction == 1f, "Stat fractions cover the permitted endpoints.");
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }

            Reject(() => new UiStatBar("", 0, 1), "label");
            Reject(() => new UiStatBar(new string('a', 25), 0, 1), "label");
            Reject(() => new UiStatBar("Capacity", 0, 0), "maximum");
            Reject(() => new UiStatBar("Capacity", 0, 1_000_001), "maximum");
            Reject(() => new UiStatBar("Capacity", -1, 1), "value");
            Reject(() => new UiStatBar("Capacity", 2, 1), "value");
            Reject(() => new UiStatBar("Capacity", 1, 1, format: (UiStatFormat)3), "format");
        }

        private static void CountdownFormatsUseAReusableDigitBuffer()
        {
            var seconds = UiCountdownFormat.Seconds;
            var minutes = UiCountdownFormat.MinutesSeconds;
            var hours = UiCountdownFormat.HoursMinutesSeconds;
            var cases = new[]
            {
                (0, "0", "0:00", "0:00:00"),
                (59, "59", "0:59", "0:00:59"),
                (60, "60", "1:00", "0:01:00"),
                (3599, "3599", "59:59", "0:59:59"),
                (3600, "3600", "60:00", "1:00:00"),
                (3723, "3723", "62:03", "1:02:03"),
                (7500, "7500", "125:00", "2:05:00"),
                (int.MaxValue, "2147483647", "35791394:07", "596523:14:07"),
            };
            foreach (var (value, whole, partial, full) in cases)
                Require(UiCountdownFormatting.Digits(value, seconds) == whole && UiCountdownFormatting.Digits(value, minutes) == partial &&
                    UiCountdownFormatting.Digits(value, hours) == full, "Countdown formats preserve unbounded leading units and exact clock rollovers.");

            var clock = UiCountdown.WithFormat(100.25, "Next wave", minutes, new UiThresholds(10, 5), UiTone.Primary);
            Require(clock.UsesStatusFormat && UiCountdown.WithFormat(1, "", UiCountdownFormat.Seconds, new UiThresholds(10, 5)).UsesStatusFormat &&
                clock.RemainingSeconds(89.99) == 11 && clock.RemainingSeconds(90.25) == 10 &&
                clock.RemainingSeconds(95.25) == 5 && clock.RemainingSeconds(100.25) == 0 &&
                UiCountdownFormatting.Text(clock.Label, 0, minutes) == "Next wave 0:00",
                "The displayed countdown rounds up and has the same formatted zero at expiry.");
            var display = new UiCountdownDisplay(clock);
            var buffer = display.Digits;
            Require(display.Update(11) == (true, true) && display.Update(11) == (false, false) &&
                display.Status is { Level: UiStatusLevel.Normal, Tone: UiTone.Primary } &&
                display.Update(10) == (true, true) && display.Status.Glyph == UiGlyph.Warning &&
                display.Update(5) == (true, true) && display.Status.Glyph == UiGlyph.Cross &&
                ReferenceEquals(buffer, display.Digits) && new string(buffer, 0, display.Length) == "0:05",
                "The retained countdown updates digits at whole seconds and signals its thresholds.");

            var rest = AllocationProbe.Measure("settled formatted timer",
                () => Require(display.Update(5) == (false, false), "The timer second and status did not change."),
                frames: 2048, warmups: 64);
            var frame = 0;
            var ticks = AllocationProbe.Measure("formatted timer ticks", () =>
                Require(display.Update((frame++ & 1) == 0 ? 3599 : 3600).DigitsChanged, "A new second updates the same digit buffer."),
                frames: 2048, warmups: 64);
            Require(rest == 0 && ticks == 0, "Countdown formatting allocates zero bytes between ticks and when the digits change.");

            var fractional = UiCountdown.WithFormat(100d, "Departure", minutes, new UiThresholds(10.5d, 10.25d));
            var fractionalDisplay = new UiCountdownDisplay(fractional);
            Require(fractionalDisplay.Update(fractional.RemainingTime(89.25d)) == (true, false) &&
                fractionalDisplay.Status.Level == UiStatusLevel.Normal &&
                fractionalDisplay.Update(fractional.RemainingTime(89.5d)) == (false, true) &&
                fractionalDisplay.Status.Glyph == UiGlyph.Warning &&
                fractionalDisplay.Update(fractional.RemainingTime(89.75d)) == (false, true) &&
                fractionalDisplay.Status.Glyph == UiGlyph.Cross &&
                new string(fractionalDisplay.Digits, 0, fractionalDisplay.Length) == "0:11",
                "Both exact fractional status transitions leave the display's eleven-second buffer intact.");
            var distant = UiCountdown.WithFormat(4_000_000_000d, "", minutes, new UiThresholds(3_000_000_000d, 2_500_000_000d));
            Require(distant.RemainingSeconds(0d) == int.MaxValue &&
                UiStatusPresentation.For(distant, distant.RemainingTime(0d)).Level == UiStatusLevel.Normal,
                "Distant timers clamp displayed digits without moving their status thresholds.");
        }

        private static void Reject(Action candidate, string argument)
        {
            try { candidate(); }
            catch (ArgumentException error) when (error.ParamName == argument) { return; }
            throw new InvalidOperationException("The status contract must reject an invalid " + argument + ".");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
