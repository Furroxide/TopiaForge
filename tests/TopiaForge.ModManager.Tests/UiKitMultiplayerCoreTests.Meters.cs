using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitMultiplayerCoreTests
    {
        private static void PlainTextIsBoundedAndInert()
        {
            AssertEqual(TopiaForgePlainText.Sanitize(null, 10), string.Empty, "null sanitizes to empty");
            AssertEqual(TopiaForgePlainText.Sanitize(string.Empty, 10), string.Empty, "empty stays empty");

            const string clean = "Alice the Bold";
            Assert(ReferenceEquals(TopiaForgePlainText.Sanitize(clean, 32), clean), "clean text that fits is returned without copying");

            AssertEqual(TopiaForgePlainText.Sanitize("  hi\r\n\tthere \u0007 you  ", 64), "hi there you", "controls and whitespace runs collapse to single spaces and trim");
            AssertEqual(TopiaForgePlainText.Sanitize("line1" + Ch(0x2028) + "line2" + Ch(0x2029) + "end", 64), "line1 line2 end", "line and paragraph separators become spaces");
            AssertEqual(TopiaForgePlainText.Sanitize("Al" + Ch(0x202E) + "ecila" + Ch(0x202C) + ": hi", 64), "Alecila: hi", "bidi overrides are removed so text cannot reorder");
            AssertEqual(TopiaForgePlainText.Sanitize(Ch(0x2066) + "x" + Ch(0x2069) + Ch(0x200F) + Ch(0x061C), 64), "x", "isolates and directional marks are removed");
            AssertEqual(TopiaForgePlainText.Sanitize("a\uD800b", 64), "a" + Ch(0xFFFD) + "b", "a lone high surrogate becomes the replacement character");
            AssertEqual(TopiaForgePlainText.Sanitize("a\uDC00", 64), "a" + Ch(0xFFFD), "a lone low surrogate becomes the replacement character");
            AssertEqual(TopiaForgePlainText.Sanitize("<size=300>big</size>", 64), "<size=300>big</size>", "markup stays literal text; labels disable rich text");
            AssertEqual(TopiaForgePlainText.Sanitize(" \t\r\n ", 64), string.Empty, "whitespace-only text sanitizes to empty");

            var truncated = TopiaForgePlainText.Sanitize("abcdefghijklmnopqrstuvwxyz", 10);
            AssertEqual(truncated, "abcdefghi" + TopiaForgePlainText.Ellipsis, "long text is cut with an ellipsis inside the bound");
            AssertEqual(TopiaForgePlainText.Sanitize("abcd efgh", 6), "abcd" + TopiaForgePlainText.Ellipsis, "a cut never leaves a trailing space");

            var emoji = "abc" + char.ConvertFromUtf32(0x1F600) + "def";
            var cutAtPair = TopiaForgePlainText.Sanitize(emoji, 5);
            AssertEqual(cutAtPair, "abc" + TopiaForgePlainText.Ellipsis, "a cut never splits a surrogate pair");
            AssertEqual(TopiaForgePlainText.Sanitize(emoji, 6), "abc" + char.ConvertFromUtf32(0x1F600) + TopiaForgePlainText.Ellipsis, "a whole pair before the cut is kept");
            AssertEqual(TopiaForgePlainText.Sanitize("abcdef", 1), TopiaForgePlainText.Ellipsis.ToString(), "a one-character bound keeps only the ellipsis");
            AssertEqual(TopiaForgePlainText.Sanitize(new string('x', 5000), 256).Length, 256, "a long message is bounded exactly");
            AssertThrows<ArgumentOutOfRangeException>(() => TopiaForgePlainText.Sanitize("x", 0), "a zero bound is rejected");
        }

        private static string Ch(int code)
        {
            return ((char)code).ToString();
        }

        private static void SignalBarsMapPingQualityAndLevels()
        {
            var expectations = new[] { (0, 4), (60, 4), (61, 3), (120, 3), (121, 2), (200, 2), (201, 1), (350, 1), (351, 0), (100000, 0) };
            foreach (var (ping, bars) in expectations)
            {
                AssertEqual(TopiaForgeSignalMath.BarsForPing(ping), bars, "default ceilings map " + ping + " ms");
            }

            AssertEqual(TopiaForgeSignalMath.BarsForPing(-1), TopiaForgeSignalMath.Unknown, "a negative ping is unknown");
            AssertEqual(TopiaForgeSignalMath.BarsForPing(int.MinValue), TopiaForgeSignalMath.Unknown, "the minimum int ping is unknown");

            var lan = new TopiaForgeSignalThresholds(5, 10, 20, 40);
            AssertEqual(TopiaForgeSignalMath.BarsForPing(5, lan), 4, "custom four-bar ceiling");
            AssertEqual(TopiaForgeSignalMath.BarsForPing(30, lan), 1, "custom one-bar ceiling");
            AssertEqual(TopiaForgeSignalMath.BarsForPing(41, lan), 0, "beyond the custom ceilings");
            AssertEqual(TopiaForgeSignalMath.BarsForPing(61, default), 3, "default(struct) thresholds fall back to the defaults");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeSignalThresholds(0, 10, 20, 30), "a zero ceiling is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeSignalThresholds(10, 10, 20, 30), "equal ceilings are rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeSignalThresholds(10, 20, 15, 30), "descending ceilings are rejected");
            Assert(TopiaForgeSignalThresholds.Default.Equals(new TopiaForgeSignalThresholds(60, 120, 200, 350)), "thresholds compare by value");

            AssertEqual(TopiaForgeSignalMath.BarsForQuality(0f), 0, "zero quality has no bars");
            AssertEqual(TopiaForgeSignalMath.BarsForQuality(1f), 4, "full quality has four bars");
            AssertEqual(TopiaForgeSignalMath.BarsForQuality(0.5f), 2, "half quality has two bars");
            AssertEqual(TopiaForgeSignalMath.BarsForQuality(0.12f), 0, "quality rounds down below a half bar");
            AssertEqual(TopiaForgeSignalMath.BarsForQuality(0.125f), 1, "quality rounds up at a half bar");
            AssertEqual(TopiaForgeSignalMath.BarsForQuality(-3f), 0, "negative quality clamps");
            AssertEqual(TopiaForgeSignalMath.BarsForQuality(7f), 4, "excess quality clamps");
            AssertEqual(TopiaForgeSignalMath.BarsForQuality(float.NaN), TopiaForgeSignalMath.Unknown, "NaN quality is unknown");
            AssertEqual(TopiaForgeSignalMath.BarsForQuality(float.PositiveInfinity), TopiaForgeSignalMath.Unknown, "infinite quality is unknown");

            AssertEqual(TopiaForgeSignalMath.BarsForLevel(0, 4), 0, "worst bucket");
            AssertEqual(TopiaForgeSignalMath.BarsForLevel(4, 4), 4, "best bucket");
            AssertEqual(TopiaForgeSignalMath.BarsForLevel(1, 3), 1, "three-level scale, level 1");
            AssertEqual(TopiaForgeSignalMath.BarsForLevel(2, 3), 3, "three-level scale, level 2");
            AssertEqual(TopiaForgeSignalMath.BarsForLevel(9, 3), 4, "levels above the scale cap");
            AssertEqual(TopiaForgeSignalMath.BarsForLevel(-1, 3), TopiaForgeSignalMath.Unknown, "a negative level is unknown");
            AssertThrows<ArgumentOutOfRangeException>(() => TopiaForgeSignalMath.BarsForLevel(0, 0), "an empty scale is rejected");

            AssertEqual(TopiaForgeSignalMath.Clamp(-7), TopiaForgeSignalMath.Unknown, "negative raw bars are unknown");
            AssertEqual(TopiaForgeSignalMath.Clamp(9), 4, "raw bars cap at four");
            AssertEqual(TopiaForgeSignalMath.Clamp(2), 2, "raw bars in range pass through");
        }

        private static void DecibelMappingAndZones()
        {
            AssertNear(TopiaForgeLevelMath.ToDecibels(1f), 0f, "full scale is 0 dBFS");
            AssertNear(TopiaForgeLevelMath.ToDecibels(0.5f), -6.0206f, "half amplitude is about -6 dB", 0.001f);
            AssertNear(TopiaForgeLevelMath.ToDecibels(0.1f), -20f, "a tenth is -20 dB", 0.001f);
            AssertNear(TopiaForgeLevelMath.ToDecibels(0f), TopiaForgeLevelMath.SilenceDecibels, "zero is silence");
            AssertNear(TopiaForgeLevelMath.ToDecibels(-0.5f), TopiaForgeLevelMath.SilenceDecibels, "negative input is silence");
            AssertNear(TopiaForgeLevelMath.ToDecibels(float.NaN), TopiaForgeLevelMath.SilenceDecibels, "NaN is silence");
            AssertNear(TopiaForgeLevelMath.ToDecibels(1e-9f), TopiaForgeLevelMath.SilenceDecibels, "tiny input clamps to the silence floor");
            AssertNear(TopiaForgeLevelMath.ToDecibels(float.PositiveInfinity), TopiaForgeLevelMath.MaxDecibels, "infinity clamps to the maximum");
            AssertNear(TopiaForgeLevelMath.ToDecibels(1000f), TopiaForgeLevelMath.MaxDecibels, "very loud input clamps to the maximum");
            AssertNear(TopiaForgeLevelMath.ToLinear(-20f), 0.1f, "-20 dB is a tenth", 0.00001f);
            AssertNear(TopiaForgeLevelMath.ToLinear(TopiaForgeLevelMath.ToDecibels(0.37f)), 0.37f, "dB round-trips", 0.00001f);
            AssertNear(TopiaForgeLevelMath.ToLinear(TopiaForgeLevelMath.SilenceDecibels), 0f, "silence maps back to zero");
            AssertNear(TopiaForgeLevelMath.ToLinear(float.NaN), 0f, "NaN dB maps to zero");

            AssertNear(TopiaForgeLevelMath.ToFraction(-60f, -60f, 0f), 0f, "floor maps to zero");
            AssertNear(TopiaForgeLevelMath.ToFraction(0f, -60f, 0f), 1f, "ceiling maps to one");
            AssertNear(TopiaForgeLevelMath.ToFraction(-30f, -60f, 0f), 0.5f, "midpoint maps to a half");
            AssertNear(TopiaForgeLevelMath.ToFraction(-90f, -60f, 0f), 0f, "below the floor clamps");
            AssertNear(TopiaForgeLevelMath.ToFraction(12f, -60f, 0f), 1f, "above the ceiling clamps");
            AssertNear(TopiaForgeLevelMath.ToFraction(float.NaN, -60f, 0f), 0f, "NaN maps to zero");
            AssertNear(TopiaForgeLevelMath.ToFraction(-10f, 0f, 0f), 0f, "an empty range maps to zero");
            AssertNear(TopiaForgeLevelMath.ToFraction(-10f, 0f, -60f), 0f, "an inverted range maps to zero");
            AssertNear(TopiaForgeLevelMath.LinearToFraction(0.1f, -40f, 0f), 0.5f, "-20 dB sits halfway on a 40 dB meter", 0.0001f);

            AssertEqual(TopiaForgeLevelMath.Zone(-0.5f, -40f, -6f, -1f), TopiaForgeLevelZone.Clipping, "near full scale clips");
            AssertEqual(TopiaForgeLevelMath.Zone(-3f, -40f, -6f, -1f), TopiaForgeLevelZone.Hot, "loud is hot");
            AssertEqual(TopiaForgeLevelMath.Zone(-20f, -40f, -6f, -1f), TopiaForgeLevelZone.Normal, "above the threshold is normal");
            AssertEqual(TopiaForgeLevelMath.Zone(-50f, -40f, -6f, -1f), TopiaForgeLevelZone.BelowThreshold, "below the threshold");
            AssertEqual(TopiaForgeLevelMath.Zone(-40f, -40f, -6f, -1f), TopiaForgeLevelZone.Normal, "exactly at the threshold counts as active");
            AssertEqual(TopiaForgeLevelMath.Zone(-50f, float.NaN, -6f, -1f), TopiaForgeLevelZone.Normal, "a NaN threshold means no threshold");
            AssertEqual(TopiaForgeLevelMath.Zone(float.NaN, -40f, -6f, -1f), TopiaForgeLevelZone.BelowThreshold, "a NaN level counts as silence");
        }

        private static void MeterBallisticsAndPeakHold()
        {
            AssertNear(TopiaForgeLevelMath.Release(0.2f, 0.9f, 0.016f, 0.5f), 0.9f, "a louder target is reached instantly");
            AssertNear(TopiaForgeLevelMath.Release(0.9f, 0.1f, 0.2f, 0.5f), 0.8f, "a quieter target is approached at the fall rate");
            AssertNear(TopiaForgeLevelMath.Release(0.9f, 0.85f, 1f, 0.5f), 0.85f, "the fall never undershoots the target");
            AssertNear(TopiaForgeLevelMath.Release(0.9f, 0.1f, 0f, 0.5f), 0.9f, "no elapsed time means no fall");
            AssertNear(TopiaForgeLevelMath.Release(0.9f, 0.1f, float.NaN, 0.5f), 0.9f, "NaN elapsed time means no fall");
            AssertNear(TopiaForgeLevelMath.Release(0.9f, 0.1f, 0.1f, 0f), 0.1f, "a zero fall rate disables smoothing");
            AssertNear(TopiaForgeLevelMath.Release(0.9f, float.NaN, 0.1f, 0.5f), 0.85f, "a NaN target is treated as silence and still falls smoothly");
            AssertNear(TopiaForgeLevelMath.Release(4f, -2f, 10f, 0.5f), 0f, "out-of-range inputs clamp");

            var hold = new TopiaForgePeakHold(holdSeconds: 1f, fallPerSecond: 0.5f);
            hold.Update(0.8f, 0.016f);
            AssertNear(hold.Value, 0.8f, "a new peak latches");
            for (var step = 0; step < 9; step++)
            {
                hold.Update(0.1f, 0.1f);
            }

            AssertNear(hold.Value, 0.8f, "the peak holds through the hold time");
            hold.Update(0.1f, 0.3f);
            AssertNear(hold.Value, 0.7f, "the hold expires mid-step and only the remainder falls", 0.0002f);
            hold.Update(0.1f, 1f);
            AssertNear(hold.Value, 0.2f, "the marker falls at its rate", 0.0002f);
            hold.Update(0.15f, 10f);
            AssertNear(hold.Value, 0.15f, "the marker never falls below the live level");
            hold.Update(0.6f, 0.016f);
            AssertNear(hold.Value, 0.6f, "a fresh peak re-latches");
            hold.Update(float.NaN, 0.5f);
            AssertNear(hold.Value, 0.6f, "a NaN level counts as silence and the peak stays held");
            hold.Reset();
            AssertNear(hold.Value, 0f, "reset drops the marker");

            var instant = new TopiaForgePeakHold(holdSeconds: 0f, fallPerSecond: 1f);
            instant.Update(1f, 0f);
            instant.Update(0f, 0.25f);
            AssertNear(instant.Value, 0.75f, "a zero hold time falls immediately");

            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgePeakHold(-1f, 1f), "a negative hold is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgePeakHold(1f, float.NaN), "a NaN fall rate is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgePeakHold(float.PositiveInfinity, 1f), "an infinite hold is rejected");
        }
    }
}
