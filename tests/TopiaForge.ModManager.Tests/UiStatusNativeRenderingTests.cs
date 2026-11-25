using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiStatusNativeRenderingTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var paths = new[] { "Status", "StatusBar", "StatusCountdown" };
            var allocation = File.ReadAllText(Path.Combine(root, "tests/Shared/AllocationProbe.cs"));
            var sources = paths.Select(suffix => File.ReadAllText(Path.Combine(root,
                    "src/TopiaForge.ModManager/UnityUiLayoutRenderer." + suffix + ".cs")))
                .Concat(new[] { NativeBoundary, Scenarios, allocation })
                .Select(source => CSharpSyntaxTree.ParseText(source));
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = platform.Append(typeof(UiNode).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager.Tests", sources, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var assembly = new MemoryStream();
            var result = compilation.Emit(assembly);
            if (!result.Success)
                throw new InvalidOperationException("The production status renderer did not compile against the native boundary. " +
                    string.Join("\n", result.Diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));

            var themePath = Path.Combine(root, "mods/TopiaForge.UiGallery/ui/themes/survival.json");
            var test = Assembly.Load(assembly.ToArray()).GetType("TopiaForge.ModManager.OwnerUiService")!.GetMethod("Prove")!;
            try { test.Invoke(null, new object[] { File.ReadAllBytes(themePath) }); }
            catch (TargetInvocationException failure) { throw failure.InnerException!; }
            Console.WriteLine("UiStatusNativeRenderingTests passed: tall stat fit, both tone directions, clipped stat paint, tabular digits, clock rollover, and zero bytes across 10,000 idle, 10,000 animated frames.");
        }

        private const string Scenarios = """
            using System;
            using System.Linq;
            using TMPro;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.TestSupport;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    public static void Prove(byte[] sample) => UnityUiLayoutRenderer.Prove(sample);

                    private sealed partial class UnityUiLayoutRenderer
                    {
                        internal static void Prove(byte[] sample)
                        {
                            var survival = UiThemePackParser.Parse(sample).Value!;
                            foreach (var descriptor in new[] { UiThemePackParser.Default, survival })
                            foreach (var hud in new[] { false, true })
                            foreach (var high in new[] { false, true })
                            foreach (var scale in new[] { 1f, 2f })
                            {
                                var scheme = descriptor.SelectScheme(hud, high);
                                VerifyBar(descriptor, scheme, high, scale);
                                if (!hud && ReferenceEquals(descriptor, UiThemePackParser.Default))
                                    VerifyTallPill(descriptor, scheme, high, scale);
                                VerifyProgress(descriptor, scheme, high, scale);
                            }
                            VerifyBothHighContrastFloorsAfterRepair();
                            VerifyCountdown();
                            VerifyFractionalCountdown();
                        }

                        private static void VerifyBar(UiThemeDescriptor descriptor, UiThemeScheme scheme, bool high, float scale)
                        {
                            var thresholds = new UiThresholds(55, 40);
                            var theme = new TopiaForgeResolvedTheme(high);
                            foreach (var value in new[] { 0, 40, 55, 80, 100 })
                            {
                                var node = new UiStatBar("Capacity", value, 100, thresholds, UiStatFormat.ValueOfMaximum);
                                var root = new TopiaForgeContainer(theme, scale);
                                var leaf = new NativeStatusLeaf(root, node, new UiCallbackGate());
                                var bounds = Arrange(leaf, node, descriptor, high, scale, 400, 40);
                                leaf.Apply(bounds, UiPaintRole.Raised, scheme, theme, descriptor, descriptor);
                                var filled = root.Go.Part("Stat filled clip");
                                var empty = root.Go.Part("Stat empty clip");
                                var track = root.Go.Part("Stat track");
                                var fill = filled.Part("Stat fill");
                                var left = filled.Part("Label");
                                var right = empty.Part("Label");
                                var fillEdge = bounds.Width * node.Fraction;
                                Check(filled.GetComponent<RectMask2D>() != null && empty.GetComponent<RectMask2D>() != null &&
                                    Near(filled.Rect.Bounds.Right, fillEdge) && Near(empty.Rect.Bounds.X, fillEdge) &&
                                    Near(empty.Rect.Bounds.Right, bounds.Width), "Stat fill, on-fill label and on-track label use the same edge.");
                                Check(fill.Rect.Bounds.Width == track.Rect.Bounds.Width &&
                                    fill.GetComponent<TopiaForgeRoundedImage>()!.Radius == track.GetComponent<TopiaForgeRoundedImage>()!.Radius,
                                    "The fill is the full rounded projection clipped by the left mask.");
                                Check(Near(left.Rect.Bounds.X, right.Rect.Bounds.X + fillEdge) && left.Rect.Bounds.Width == right.Rect.Bounds.Width &&
                                    left.GetComponent<TMP_Text>()!.text == right.GetComponent<TMP_Text>()!.text &&
                                    left.GetComponent<TMP_Text>()!.text == node.DisplayLabel,
                                    "Clipped labels keep identical glyph positions and the canonical formatting.");
                                Check(filled.Active == (value != 0) && empty.Active == (value != 100), "Empty segments do not paint at either endpoint.");
                                var mark = UiStatusPresentation.For(node).Glyph;
                                Check(Cue(filled) == mark && Cue(empty) == mark, "Both halves draw the approved Warning or Cross before the label.");
                                var filledBackground = fill.GetComponent<Image>()!.color;
                                var emptyBackground = track.GetComponent<Image>()!.color;
                                var minimum = high ? 7d : 4.5d;
                                var leftInk = left.GetComponent<TMP_Text>()!.color;
                                var rightInk = right.GetComponent<TMP_Text>()!.color;
                                var ink = UiStatusMeterPaint.Resolve(scheme, UiStatusPresentation.For(node).Tone, high);
                                Check(leftInk == ColorOf(ink.Filled.On) && rightInk == ColorOf(ink.Empty.On) &&
                                    Contrast(leftInk, filledBackground) >= minimum && Contrast(rightInk, emptyBackground) >= minimum,
                                    "Independent stat inks keep native text readable on both sides in every scheme.");
                                Check(root.Go.GetComponent<UnityUiStatusCountdownDriver>() == null, "A static bar has no frame driver.");
                            }

                            var overwide = new UiStatBar("Whole station bandwidth", 999_999, 1_000_000, thresholds, UiStatFormat.ValueOfMaximum);
                            var measured = new NativeStatusLeaf(new TopiaForgeContainer(theme, scale), overwide, new UiCallbackGate());
                            try { Arrange(measured, overwide, descriptor, high, scale, 60, 40); }
                            catch (UiLayoutException rejected) when (rejected.Failure == UiLayoutFailure.ClippedContent) { return; }
                            throw new InvalidOperationException("A stat that cannot fit is rejected before changing native geometry.");
                        }

                        private static void VerifyTallPill(UiThemeDescriptor descriptor, UiThemeScheme scheme, bool high, float scale)
                        {
                            var stat = new UiStatBar("Hull", 50, 100, new UiThresholds(60, 40));
                            var theme = new TopiaForgeResolvedTheme(high);
                            var root = new TopiaForgeContainer(theme, scale);
                            var leaf = new NativeStatusLeaf(root, stat, new UiCallbackGate());
                            var result = UiLayoutEngine.Arrange(new UiSizedBox(stat, width: 200, height: 200),
                                UiLayoutConstraints.Loose(400, 400),
                                (_, limits, _) => leaf.Measure(limits, descriptor, descriptor, high), scale, high);
                            var bounds = result.Placements.Single(placement => ReferenceEquals(placement.Node, stat)).Bounds;
                            leaf.Apply(bounds, UiPaintRole.Surface, scheme, theme, descriptor, descriptor);

                            var filled = root.Go.Part("Stat filled clip");
                            var empty = root.Go.Part("Stat empty clip");
                            var label = filled.Part("Label");
                            var cue = filled.Part("Status cue").Rect.Bounds;
                            var preference = label.GetComponent<TMP_Text>()!.GetPreferredValues(stat.DisplayLabel);
                            Check(bounds.Width == 200 && bounds.Height == 200 &&
                                root.Go.Part("Stat track").GetComponent<TopiaForgeRoundedImage>()!.Radius == 100 &&
                                label.Rect.Bounds.Width >= preference.x && cue.Right <= label.Rect.Bounds.X &&
                                Near(cue.Y + cue.Height / 2, 100) && Cue(filled) == UiGlyph.Warning &&
                                label.Rect.Bounds.Width == empty.Part("Label").Rect.Bounds.Width,
                                "The fitted tall Pill keeps readable split captions and the preceding cue in its centre band.");

                            var allocated = AllocationProbe.Measure("settled native tall Pill",
                                () => leaf.Apply(bounds, UiPaintRole.Surface, scheme, theme, descriptor, descriptor), 4096, 256);
                            Check(allocated == 0, "Reapplying a settled tall Pill allocated " + allocated + " bytes.");

                            try { Arrange(leaf, stat, descriptor, high, scale, 80, 200); }
                            catch (UiLayoutException rejected) when (rejected.Failure == UiLayoutFailure.ClippedContent) { return; }
                            throw new InvalidOperationException("A tall Pill without width for its cue and caption is rejected.");
                        }

                        private static void VerifyProgress(UiThemeDescriptor descriptor, UiThemeScheme scheme, bool high, float scale)
                        {
                            Check(!NativeStatusLeaf.Handles(new UiProgress(.5f, "Classic")), "The legacy progress route retains its own renderer.");
                            Check(!NativeStatusLeaf.Handles(new UiCountdown(20d, "Classic")), "Legacy Seconds keeps the original renderer.");
                            var theme = new TopiaForgeResolvedTheme(high);
                            var above = new UiThresholds(.85, .95, UiThresholdDirection.AtOrAbove);
                            var below = new UiThresholds(.5, .25);
                            foreach (var (fraction, thresholds, state, glyph) in new[]
                            {
                                (.6f, above, UiTone.Accent, (UiGlyph?)null), (.85f, above, UiTone.Warning, (UiGlyph?)UiGlyph.Warning),
                                (.95f, above, UiTone.Danger, (UiGlyph?)UiGlyph.Cross), (.75f, below, UiTone.Accent, (UiGlyph?)null),
                                (.5f, below, UiTone.Warning, (UiGlyph?)UiGlyph.Warning), (.25f, below, UiTone.Danger, (UiGlyph?)UiGlyph.Cross),
                            })
                            {
                                var node = UiProgress.WithThresholds(fraction, "Load", thresholds);
                                var root = new TopiaForgeContainer(theme, scale);
                                var leaf = new NativeStatusLeaf(root, node, new UiCallbackGate());
                                var bounds = Arrange(leaf, node, descriptor, high, scale, 350, 56);
                                leaf.Apply(bounds, UiPaintRole.Raised, scheme, theme, descriptor, descriptor);
                                var label = root.Go.Part("Label").GetComponent<TMP_Text>()!;
                                var fill = root.Go.Part("Progress fill");
                                Check(Cue(root.Go) == glyph && label.text == node.DisplayLabel &&
                                    label.color == ColorOf(scheme.ResolveForeground(state, UiPaintRole.Raised)),
                                    "Progress displays the percentage and a matching glyph at either threshold direction.");
                                Check(Near(fill.Rect.Bounds.Width, fraction * bounds.Width) &&
                                    fill.GetComponent<Image>()!.color == ColorOf(UiStatusMeterPaint.Resolve(scheme, state, high).Filled.Fill),
                                    "Progress paints the resolved fill at the actual value fraction.");
                            }
                        }

                        private static void VerifyBothHighContrastFloorsAfterRepair()
                        {
                            var black = new UiThemeColor(0, 0, 0);
                            var white = new UiThemeColor(255, 255, 255);
                            var dark = new UiThemeColor(68, 68, 68);
                            var roles = Enum.GetValues<UiPaintRole>();
                            var pairs = roles.Select(_ => new UiThemePair(black, white)).ToArray();
                            pairs[(int)UiPaintRole.Success] = new UiThemePair(dark, white);
                            var scheme = new UiThemeScheme(pairs, white, white, white, new UiThemeColor?[Enum.GetValues<UiTone>().Length], 7);
                            var paint = UiStatusMeterPaint.Resolve(scheme, UiTone.Success, true);
                            Check(UiThemeColors.Contrast(paint.Filled.Fill, paint.Filled.On) >= 7 &&
                                UiThemeColors.Contrast(paint.Filled.Fill, paint.Empty.Fill) >= 4.5,
                                "Repairing a dark fill against a dark track still leaves independent 7:1 label ink.");
                        }

                        private static void VerifyCountdown()
                        {
                            var descriptor = UiThemePackParser.Default;
                            var scheme = descriptor.SelectScheme(false, true);
                            var theme = new TopiaForgeResolvedTheme(true);
                            foreach (var format in Enum.GetValues<UiCountdownFormat>())
                            {
                                var epochs = "old";
                                double now = 0;
                                var node = UiCountdown.WithFormat(3600, "Departure", format, new UiThresholds(10, 5));
                                UiClockBinding.Bind(node, epochs);
                                var callbacks = new UiCallbackGate { Clock = () => now, ClockEpoch = () => epochs };
                                var root = new TopiaForgeContainer(theme, 2);
                                var leaf = new NativeStatusLeaf(root, node, callbacks);
                                var bounds = Arrange(leaf, node, descriptor, true, 2, 430, 36);
                                leaf.Apply(bounds, UiPaintRole.Surface, scheme, theme, descriptor, descriptor);
                                var text = root.Go.Part("Countdown digits").GetComponent<TMP_Text>()!;
                                var caption = root.Go.Part("Label").GetComponent<TMP_Text>()!;
                                var driver = root.Go.GetComponent<UnityUiStatusCountdownDriver>()!;
                                Check(caption.Role == TopiaForgeTextStyle.Label && text.Role == TopiaForgeTextStyle.Mono && !text.richText,
                                    "The countdown separates the Label caption from literal Mono digits.");

                                foreach (var seconds in new[] { 3600, 3599, 60, 59, 10, 5, 0 })
                                {
                                    now = 3600 - seconds;
                                    driver.TickTest();
                                    Check(text.Current == UiCountdownFormatting.Digits(seconds, format), "Native digits follow the signed countdown formatter.");
                                    Check(Cue(root.Go) == UiStatusPresentation.For(node, seconds).Glyph &&
                                        text.color == ColorOf(scheme.ResolveForeground(UiStatusPresentation.For(node, seconds).Tone, UiPaintRole.Surface)),
                                        "Threshold crossings update the native glyph and ink together.");
                                    text.RenderTest();
                                    if (text.Info.characterCount < 3) continue;
                                    var first = text.Info.characterInfo[0];
                                    var second = text.Info.characterInfo[1];
                                    var third = text.Info.characterInfo[2];
                                    Check(first.xAdvance == second.origin && Near(second.origin - first.origin, third.origin - second.origin) &&
                                        text.Info.meshInfo[0].vertices[second.vertexIndex].x == second.origin,
                                        "Proportional source glyphs have a tabular advance in the actual mesh.");
                                }

                                now = 3590.8;
                                driver.TickTest();
                                Check(text.Current == UiCountdownFormatting.Digits(10, format), "Native remaining time rounds up.");
                                epochs = "new";
                                now = 0;
                                leaf.Apply(bounds, UiPaintRole.Surface, scheme, theme, descriptor, descriptor);
                                Check(text.Current == UiCountdownFormatting.Digits(0, format) && Cue(root.Go) == UiGlyph.Cross && node.ClockEpoch == "old",
                                    "An epoch change renders zero in the selected format without rebinding the old node.");
                                var count = text.Changes;
                                var idle = AllocationProbe.Measure("idle native countdown " + format, driver.TickTest, 10_000);
                                Check(count == text.Changes && idle == 0, "Idle timer frames make no digit writes or allocations.");

                                epochs = "old";
                                now = 1;
                                driver.TickTest();
                                text.RenderTest();
                                var frame = 0;
                                var animated = AllocationProbe.Measure("changing native countdown " + format, () =>
                                {
                                    now = (frame++ & 1) == 0 ? 3590 : 3596;
                                    driver.TickTest();
                                    text.RenderTest();
                                }, 10_000);
                                Check(animated == 0, "Changing countdown digits and threshold paint allocates nothing after warm-up.");
                            }
                        }

                        private static void VerifyFractionalCountdown()
                        {
                            var descriptor = UiThemePackParser.Default;
                            var scheme = descriptor.SelectScheme(false, true);
                            var theme = new TopiaForgeResolvedTheme(true);
                            var now = 89.25d;
                            const string epoch = "fractional";
                            var node = UiCountdown.WithFormat(100d, "Departure", UiCountdownFormat.MinutesSeconds, new UiThresholds(10.5d, 10.25d));
                            UiClockBinding.Bind(node, epoch);
                            var callbacks = new UiCallbackGate { Clock = () => now, ClockEpoch = () => epoch };
                            var root = new TopiaForgeContainer(theme, 2);
                            var leaf = new NativeStatusLeaf(root, node, callbacks);
                            var bounds = Arrange(leaf, node, descriptor, true, 2, 430, 36);
                            leaf.Apply(bounds, UiPaintRole.Surface, scheme, theme, descriptor, descriptor);
                            var digits = root.Go.Part("Countdown digits").GetComponent<TMP_Text>()!;
                            var caption = root.Go.Part("Label").GetComponent<TMP_Text>()!;
                            var driver = root.Go.GetComponent<UnityUiStatusCountdownDriver>()!;
                            var changes = digits.Changes;

                            VerifyPaint(null, UiTone.Neutral);
                            now = 89.5d;
                            driver.TickTest();
                            VerifyPaint(UiGlyph.Warning, UiTone.Warning);
                            now = 89.75d;
                            driver.TickTest();
                            VerifyPaint(UiGlyph.Cross, UiTone.Danger);

                            var frame = 0;
                            var sameState = AllocationProbe.Measure("native countdown within one fractional state", () =>
                            {
                                now = (frame++ & 1) == 0 ? 89.8125d : 89.875d;
                                driver.TickTest();
                            }, 10_000);
                            Check(sameState == 0 && digits.Changes == changes,
                                "Fractional frames inside one state neither rewrite the digits nor allocate.");

                            frame = 0;
                            var crossing = AllocationProbe.Measure("native countdown crossing fractional cues", () =>
                            {
                                now = (frame++ & 1) == 0 ? 89.5d : 89.75d;
                                driver.TickTest();
                            }, 10_000);
                            Check(crossing == 0 && digits.Changes == changes,
                                "Fractional cue crossings use the existing digit buffer and allocate zero bytes.");

                            void VerifyPaint(UiGlyph? glyph, UiTone tone)
                            {
                                var ink = ColorOf(scheme.ResolveForeground(tone, UiPaintRole.Surface));
                                Check(digits.Current == "0:11" && digits.Changes == changes &&
                                    Cue(root.Go) == glyph && digits.color == ink && caption.color == ink,
                                    "The native countdown changes fractional cue and ink without rewriting its eleven-second digits.");
                            }
                        }

                        private static UiLayoutRect Arrange(NativeStatusLeaf leaf, UiNode node, UiThemeDescriptor descriptor, bool high,
                            float scale, float width, float height) => UiLayoutEngine.Arrange(node, UiLayoutConstraints.Tight(width, height),
                                (_, constraints, _) => leaf.Measure(constraints, descriptor, descriptor, high), scale, high).Placements[0].Bounds;
                        private static UiGlyph? Cue(GameObject parent)
                        {
                            var image = parent.Part("Status cue").GetComponent<Image>()!;
                            return image.enabled ? image.sprite!.Value : null;
                        }
                        private static double Contrast(Color ink, Color fill) => UiThemeColors.Contrast(ink.Underlying, fill.Underlying);
                        private static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;
                        private static void Check(bool good, string reason) { if (!good) throw new InvalidOperationException(reason); }
                    }
                }
            }
            """;
    }
}
