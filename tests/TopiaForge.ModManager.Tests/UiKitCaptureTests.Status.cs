using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        internal static void RunStatusCaptureTests()
        {
            CountdownClockAndTypography();
            FractionalCountdownCuesUseExactTime();
            StatMasksLeaveCompletePhrasesReadable();
            TallStatPillKeepsItsCaptionAndCue();
            VerifyStatusGoldenManifest();
            Console.WriteLine("K2 status captures passed for paired host and guest themes, thresholds, timer epochs, tall Pill fit, and split bar inks.");
        }

        private static void CountdownClockAndTypography()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var clock = new StatusCaptureClock();
            clock.Connect(ui);
            var palette = new UiThemeRef("clock-type");
            Check(ui.AddThemeFixture("""
                {"schema":1,"id":"clock-type","typography":{"label":{"case":"upper"},"mono":{"trackingEm":0.12}}}
                """).Succeeded, "The countdown type fixture supplies different Label and Mono typography.");
            var thresholds = new UiThresholds(10, 5);
            var legacy = new UiCountdown(11.25, "Old timer", UiTone.Neutral, id: "legacy-timer");
            var input = UiCountdown.WithFormat(11.25, "Next transfer", UiCountdownFormat.Seconds, thresholds, id: "transfer");
            var legacyProgress = new UiProgress(0.41f, "Old meter", UiTone.Accent, id: "legacy-meter");
            var request = new UiSurfaceRequest("status-type", "Status", string.Empty, content: new UiColumn(legacy, input, legacyProgress))
                .WithTheme(palette);
            using var surface = Make(ui, request);
            ui.CompletePendingThemeRegistrations();
            var initial = surface.Capture(Viewport);
            var root = Row(initial, "transfer");
            var digits = Child(initial, "transfer", "CountdownDigits");
            var caption = Child(initial, "transfer", "CountdownLabel");
            var reserved = UiCountdownFormatting.Digits(int.MaxValue, UiCountdownFormat.Seconds).Length;
            var expectedMonoWidth = 18f * (0.60f + 0.12f) * reserved;
            Check(root is { RemainingSeconds: 12, State: "normal" } && digits.Text == "12" &&
                caption is { Text: "NEXT TRANSFER", TextHeightPixels: 18 } && digits.TextHeightPixels == 18 &&
                Math.Abs(digits.Width - expectedMonoWidth) < 0.01f && caption.X + caption.Width <= digits.X,
                "Explicit seconds use separate Label and tabular Mono, and round up at a fractional deadline.");
            Check(Row(initial, "legacy-timer") is { Text: "Old timer 12", RemainingSeconds: 12, TextHeightPixels: 20 } &&
                Row(initial, "legacy-meter") is { Text: "Old meter", Fraction: 0.41f, State: "normal" } &&
                legacy.Format == UiCountdownFormat.Seconds && legacy.Thresholds == null && legacyProgress.Thresholds == null,
                "Legacy forms retain their Body labels, quiet tracks, and default contracts while exposing numeric observations.");
            AssertStatusCue(initial, "transfer", null);

            clock.Time.AdvanceFrame(1.25f);
            var warning = surface.Capture(Viewport);
            Check(Row(warning, "transfer") is { State: "warning", RemainingSeconds: 10 } &&
                Child(warning, "transfer", "CountdownDigits").Text == "10", "The threshold crossing includes exactly ten seconds.");
            AssertStatusCue(warning, "transfer", UiGlyph.Warning);
            clock.Time.AdvanceFrame(1f);
            var narrower = Child(surface.Capture(Viewport), "transfer", "CountdownDigits");
            Check(narrower.Text == "9" && narrower.X == digits.X && narrower.Width == digits.Width,
                "Dropping a digit preserves the stable tabular reservation and leading edge.");
            clock.Time.AdvanceFrame(4f);
            var critical = surface.Capture(Viewport);
            Check(Row(critical, "transfer") is { State: "critical", RemainingSeconds: 5 }, "Five seconds selects the critical state.");
            AssertStatusCue(critical, "transfer", UiGlyph.Cross);

            clock.Epoch = "status-next";
            var stale = surface.Capture(Viewport);
            Check(Row(stale, "transfer") is { State: "critical", RemainingSeconds: 0 } &&
                Child(stale, "transfer", "CountdownDigits").Text == "0" && Row(stale, "legacy-timer").Text == "Old timer 0",
                "An admitted deadline retains its tree and shows zero locally after an epoch change.");
            var staleReplacement = Make(ui, new UiSurfaceRequest("stale-replacement", "Check", string.Empty));
            using (staleReplacement)
                Check(!staleReplacement.SetContent(input).Succeeded && staleReplacement.Content == null,
                    "A stale timer cannot be admitted as new content in another tree.");

            var wrappedTimer = UiCountdown.WithFormat(20, "Relay deadline for the next supply transfer", UiCountdownFormat.Seconds,
                thresholds, id: "wrapped-clock");
            using var wrapped = Make(ui, new UiSurfaceRequest("wrapped-status", "Check", string.Empty,
                content: new UiSizedBox(new UiRow(wrappedTimer), width: 265f)).WithTheme(palette));
            var narrow = wrapped.Capture(Viewport);
            Check(Row(narrow, "wrapped-clock").Height > caption.Height && narrow.CheckAccessibility().Count == 0 &&
                Child(narrow, "wrapped-clock", "CountdownDigits").Text == "14",
                "A narrow formatted row wraps the caption while preserving the readable tabular reservation.");
        }

        private static void FractionalCountdownCuesUseExactTime()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var clock = new StatusCaptureClock();
            clock.Connect(ui);
            var timer = UiCountdown.WithFormat(10.75d, "Transfer", UiCountdownFormat.MinutesSeconds,
                new UiThresholds(10.5d, 10.25d), id: "fractional");
            using var surface = Make(ui, new UiSurfaceRequest("fractional-clock", "Status", string.Empty, content: timer));

            Verify("normal", null, 11, "0:11");
            clock.Time.AdvanceFrame(.25f);
            Verify("warning", UiGlyph.Warning, 11, "0:11");
            clock.Time.AdvanceFrame(.25f);
            Verify("critical", UiGlyph.Cross, 11, "0:11");
            clock.Time.AdvanceFrame(10.25f);
            Verify("critical", UiGlyph.Cross, 0, "0:00");
            ((IUiClockReceiver)ui).FollowClock(() => 0d, () => "replacement");
            Verify("critical", UiGlyph.Cross, 0, "0:00");

            void Verify(string state, UiGlyph? glyph, int seconds, string digits)
            {
                var captured = surface.Capture(Viewport);
                var record = Row(captured, "fractional");
                Check(record.State == state && record.RemainingSeconds == seconds &&
                    Child(captured, "fractional", "CountdownDigits").Text == digits &&
                    surface.TryGetCountdownSeconds("fractional", out var remaining) && remaining == seconds,
                    "Fake countdown capture changes fractional cues independently of the whole-second readout.");
                AssertStatusCue(captured, "fractional", glyph);
            }
        }

        private static void StatMasksLeaveCompletePhrasesReadable()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var style = new UiThemeRef("stat-shape");
            Check(ui.AddThemeFixture("""
                {"schema":1,"id":"stat-shape","shape":{"small":4},"components":{"statBar":{"radius":"small","border":"strong","shadow":"small"}}}
                """).Succeeded, "The stat-bar component controls ordinary decoration.");
            var limits = new UiThresholds(60, 50);
            var stat = new UiStatBar("Integrity", 50, 100, limits, UiStatFormat.ValueOfMaximum, id: "test-stat");
            var tree = new UiColumn(stat,
                new UiStatBar("Empty", 0, 100, id: "stat-none"), new UiStatBar("Full", 100, 100, id: "stat-full"));
            using var surface = Make(ui, new UiSurfaceRequest("status-masks", "Readings", string.Empty, content: tree).WithTheme(style));
            ui.CompletePendingThemeRegistrations();
            foreach (var high in new[] { false, true })
            {
                Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(high, 1f, false)).Succeeded,
                    "Apply the chosen contrast to the stat masks.");
                var capture = surface.Capture(Viewport);
                var row = Row(capture, "test-stat");
                Check(row is { Fraction: 0.5f, State: "critical", CornerRadiusPixels: 4 } &&
                    row.BorderWidthPixels >= 3f && row.ShadowOffsetYPixels == (high ? 0f : 4f),
                    "Published statBar decoration follows the authored component and high-contrast shadow rules.");
                var descriptor = ThemeForWire(ui, style)!;
                var scheme = descriptor.SelectScheme(false, high);
                AssertStatInk(capture, "test-stat", "Integrity 50 / 100", UiStatusMeterPaint.Resolve(scheme, UiTone.Danger, high), high);
                Check(!Under(capture, "stat-none", "StatFilledLabel").Any() && Under(capture, "stat-none", "StatEmptyLabel").Single().IsRendered &&
                    !Under(capture, "stat-full", "StatEmptyLabel").Any() && Under(capture, "stat-full", "StatFilledLabel").Single().IsRendered,
                    "Zero and full values expose only the side with actual text paint.");
                AssertStatusCue(capture, "test-stat", UiGlyph.Cross);
                Check(capture.CheckAccessibility().Count == 0, "The complementary stat masks leave the full phrase readable.");
            }

            var ok = true;
            for (var index = 0; index < 1024; index++) ok &= surface.SetContent(tree).Succeeded;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 4096; index++) ok &= surface.SetContent(tree).Succeeded;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(ok && allocated == 0, "Warmed unchanged status reapplication allocated " + allocated + " bytes.");
        }

        private static void TallStatPillKeepsItsCaptionAndCue()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var stat = new UiStatBar("Hull", 50, 100, new UiThresholds(60, 40), id: "tall-stat");
            var tall = new UiSizedBox(stat, width: 200, height: 200);
            foreach (var high in new[] { false, true })
            {
                foreach (var scale in new[] { 1f, 2f })
                {
                    Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(high, scale, false)).Succeeded,
                        "Apply the chosen contrast and scale to the tall stat bar.");
                    using var surface = Make(ui, new UiSurfaceRequest("status-tall", "Status", string.Empty,
                        width: 252, content: tall));
                    var capture = surface.Capture(Viewport);
                    var row = Row(capture, "tall-stat");
                    var cue = Under(capture, "tall-stat", "StatusCue").Single();
                    var filled = Child(capture, "tall-stat", "StatFilledLabel");
                    var scheme = UiThemePackParser.Default.SelectScheme(false, high);
                    Check(row.Width == 200 * scale && row.Height == 200 * scale && row.CornerRadiusPixels == 100 * scale &&
                        cue.IsRendered && cue.X + cue.Width <= filled.X && Math.Abs(cue.Y + cue.Height / 2f - (row.Y + row.Height / 2f)) < 0.001f,
                        "A fitted tall Pill preserves its rounded track and positions its cue before the split phrase.");
                    AssertStatInk(capture, "tall-stat", stat.DisplayLabel, UiStatusMeterPaint.Resolve(scheme, UiTone.Warning, high), high);
                    AssertStatusCue(capture, "tall-stat", UiGlyph.Warning);
                    Check(capture.CheckAccessibility().Count == 0, "The complete caption and cue remain readable in the tall Pill.");

                    var unfit = ui.CreateSurface(new UiSurfaceRequest("status-unfit", "Status", string.Empty,
                        width: 132, content: new UiSizedBox(stat, width: 80, height: 200)));
                    Check(!unfit.Succeeded && unfit.ErrorCode == ModErrorCode.InvalidArgument &&
                        unfit.ErrorMessage?.Contains("cannot fit its allocation", StringComparison.Ordinal) == true,
                        "A tall bar that cannot contain its caption and cue is rejected before capture.");

                    var stable = true;
                    var allocated = AllocationProbe.Measure("settled Fake tall Pill",
                        () => stable &= surface.SetContent(tall).Succeeded, 4096, 1024);
                    Check(stable && allocated == 0, "Unchanged tall Pill reapplication allocated " + allocated + " bytes.");
                }
            }
        }

        private static void AssertStatInk(UiTreeCapture capture, string id, string phrase, UiStatusMeterPaint meter, bool high)
        {
            var filled = Child(capture, id, "StatFilledLabel");
            var empty = Child(capture, id, "StatEmptyLabel");
            var minimum = high ? 7f : 4.5f;
            Check(filled.IsRendered && empty.IsRendered && !filled.Clipped && !empty.Clipped &&
                Math.Abs(filled.X + filled.Width - empty.X) <= 0.001f &&
                filled.SemanticLabel == phrase && empty.SemanticLabel == phrase && filled.Text == empty.Text &&
                filled.InkColor == meter.Filled.On.ToString() && filled.FillColor == meter.Filled.Fill.ToString() &&
                empty.InkColor == meter.Empty.On.ToString() && empty.FillColor == meter.Empty.Fill.ToString() &&
                filled.ContrastRatio >= minimum && empty.ContrastRatio >= minimum,
                "The split stat phrase must show both exact readable ink and fill pairs without treating the masks as clipping.");
        }

        private static void AssertStatusCue(UiTreeCapture capture, string id, UiGlyph? glyph)
        {
            var cues = Under(capture, id, "StatusCue").Where(node => node.IsRendered).ToArray();
            Check(glyph == null ? cues.Length == 0 : cues.Length > 0 &&
                cues.All(node => node.Path.EndsWith("/cue:" + glyph, StringComparison.Ordinal) && node.InkColor != null),
                id + " must draw the matching Warning or Cross glyph only at its threshold.");
        }

        private static System.Collections.Generic.IEnumerable<UiCapturedNode> Under(UiTreeCapture capture, string id, string kind)
        {
            var path = Row(capture, id).Path + "/";
            return capture.Nodes.Where(node => node.Kind == kind && node.Path.StartsWith(path, StringComparison.Ordinal));
        }

        private sealed class StatusCaptureClock
        {
            internal DeterministicGameTime Time { get; } = new DeterministicGameTime();
            internal string Epoch { get; set; } = "status-initial";
            internal void Connect(FakeUiService ui) => ((IUiClockReceiver)ui).FollowClock(() => Time.Frame.ElapsedTime, () => Epoch);
        }
    }
}
