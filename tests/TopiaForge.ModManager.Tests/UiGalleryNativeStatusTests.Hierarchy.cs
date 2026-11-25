using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryNativeStatusTests
    {
        private static void CaptureNativeStatusChildren()
        {
            var root = Program.FindRepoRoot();
            const string recorder = "src/TopiaForge.ModManager/UiGalleryRecorder/";
            const string captures = "src/TopiaForge.Mods.Testing/";
            var files = new[]
            {
                recorder + "UiGalleryNativeTree.cs",
                recorder + "UiGalleryNativeTree.Layout.cs",
                recorder + "UiGalleryNativeTree.Status.cs",
                recorder + "UiGalleryNativeTree.StatusText.cs",
                recorder + "UiGalleryNativeTree.StatusStat.cs",
                recorder + "UiGalleryStatusEvidence.cs",
                recorder + "UiGalleryCaptureClip.cs",
                captures + "UiCapturedNode.cs",
                captures + "UiCaptureViewport.cs",
                captures + "UiTreeCapture.cs",
            };
            var fixture = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, recorder + "UiGalleryCaptureFixture.cs")));
            var safeText = fixture.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.Text is "SafeDisplayText" or "SafeAuthoredFieldValue");
            var options = new CSharpParseOptions(preprocessorSymbols: new[] { "TOPIAFORGE_NATIVE_CAPTURE" });
            var sources = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path)), options, path))
                .Append(CSharpSyntaxTree.ParseText(HierarchyBoundary.Replace("/* authored text gates */",
                    string.Join("\n", safeText)), options, "NativeStatusBoundary.cs"))
                .Append(CSharpSyntaxTree.ParseText(HierarchyCases, options, "NativeStatusCases.cs"));
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = platform.Append(typeof(UiNode).Assembly.Location)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    Path.GetFileName(path) != "TopiaForge.ModManager.Tests.dll")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager.Tests", sources, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var image = new MemoryStream();
            var emit = compilation.Emit(image);
            Check(emit.Success, "The production status recorder compiles against the Unity boundary. " +
                string.Join("\n", emit.Diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var run = Assembly.Load(image.ToArray()).GetType("NativeStatusCases", throwOnError: true)!.GetMethod("Run")!;
            try { run.Invoke(null, null); }
            catch (TargetInvocationException failure) { throw failure.InnerException!; }
        }

        private const string HierarchyCases = """
            using System;
            using System.Linq;
            using TMPro;
            using TopiaForge.ModManager;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UI;

            public static class NativeStatusCases
            {
                private const string Epoch = "gallery-status-room";
                private const double Now = 52.25;
                private static readonly UiThemeDescriptor Theme = UiThemePackParser.Default;
                private static readonly UiThemeScheme Scheme = Theme.SelectScheme(hud: true, highContrast: false);

                public static void Run()
                {
                    Countdown();
                    FractionalCountdown();
                    Progress();
                    StatBar();
                    Legacy();
                    Console.WriteLine("Native status hierarchy passed for single-child status hosts, legacy widgets, observed ink and geometry.");
                }

                private static void Countdown()
                {
                    var timer = UiCountdown.WithFormat(60d, "Relay", UiCountdownFormat.MinutesSeconds, new UiThresholds(10, 5), id: "relay");
                    UiClockBinding.Bind(timer, Epoch);
                    var stage = new Stage(timer);
                    var ink = Scheme.ResolveForeground(UiTone.Warning, UiPaintRole.Surface);
                    Label(stage.Widget, "Label", timer.Label, UiTextStyle.Label, ink, 106, 112, 160);
                    var digits = Label(stage.Widget, "Countdown digits", "0:08", UiTextStyle.Mono, ink, 280, 112, 110);
                    Cue(stage.Widget, UiGlyph.Warning, ink);
                    Check(stage.Read(out var result, out var recorder), "Formatted countdown recording must use the Status child already selected by Visit.");
                    Check(result!.Nodes.Single(node => node.Id == "relay") is { State: "warning", RemainingSeconds: 8d } &&
                        result.Nodes.Single(node => node.Kind == "CountdownDigits").Text == "0:08" &&
                        recorder.Fonts.Any(font => font.Path == "/0:relay/digits" && font.Family == "Fixture Mono") &&
                        result.Nodes.Any(node => node.Path == "/0:relay/cue:Warning"), "The recorder preserves observed Mono digits and threshold paint.");
                    digits.text = "0:07";
                    Check(!stage.Read(out _, out _), "A canonical but stale native readout cannot be recorded as the current countdown.");
                }

                private static void FractionalCountdown()
                {
                    var timer = UiCountdown.WithFormat(60d, "Relay", UiCountdownFormat.MinutesSeconds,
                        new UiThresholds(10.5d, 10.25d), id: "fractional");
                    UiClockBinding.Bind(timer, Epoch);
                    var stage = new Stage(timer);
                    var ink = Scheme.ResolveForeground(UiTone.Neutral, UiPaintRole.Surface);
                    var caption = Label(stage.Widget, "Label", timer.Label, UiTextStyle.Label, ink, 126, 112, 130);
                    var digits = Label(stage.Widget, "Countdown digits", "0:11", UiTextStyle.Mono, ink, 280, 112, 110);
                    var mark = Cue(stage.Widget, UiGlyph.Warning, ink);
                    mark.enabled = false;

                    Verify(49.25d, "normal", null, 11, "0:11");
                    Check(!stage.Read(out _, out _, clockTime: 49.5d),
                        "The recorder rejects stale normal paint at the fractional warning threshold even when digits still read eleven.");
                    PaintState(UiTone.Warning, UiGlyph.Warning);
                    Verify(49.5d, "warning", UiGlyph.Warning, 11, "0:11");
                    Check(!stage.Read(out _, out _, clockTime: 49.75d),
                        "The recorder rejects stale warning paint at the fractional critical threshold even when digits still read eleven.");
                    PaintState(UiTone.Danger, UiGlyph.Cross);
                    Verify(49.75d, "critical", UiGlyph.Cross, 11, "0:11");

                    digits.text = "0:00";
                    Verify(60d, "critical", UiGlyph.Cross, 0, "0:00");
                    Verify(0d, "critical", UiGlyph.Cross, 0, "0:00", "next-epoch");

                    void PaintState(UiTone tone, UiGlyph glyph)
                    {
                        var color = new Color(Scheme.ResolveForeground(tone, UiPaintRole.Surface));
                        digits.color = caption.color = mark.color = color;
                        mark.sprite = glyph;
                        mark.enabled = true;
                    }

                    void Verify(double time, string state, UiGlyph? cue, int seconds, string readout, string epoch = Epoch)
                    {
                        Check(stage.Read(out var result, out _, clockTime: time, clockEpoch: epoch),
                            "The recorder accepts the actual fractional threshold paint and rounded digits.");
                        Check(result!.Nodes.Single(node => node.Id == timer.Id) is { } row &&
                            row.State == state && row.RemainingSeconds == seconds &&
                            result.Nodes.Single(node => node.Kind == "CountdownDigits").Text == readout &&
                            result.Nodes.Count(node => node.Kind == "StatusCue") == (cue.HasValue ? 1 : 0) &&
                            (!cue.HasValue || result.Nodes.Any(node => node.Path.EndsWith("/cue:" + cue, StringComparison.Ordinal))),
                            "The recorder preserves exact cue state and the whole-second capture field.");
                    }
                }

                private static void Progress()
                {
                    var progress = UiProgress.WithThresholds(.25f, "Load", new UiThresholds(.5, .25), id: "load");
                    var stage = new Stage(progress);
                    var ink = Scheme.ResolveForeground(UiTone.Danger, UiPaintRole.Surface);
                    var meter = UiStatusMeterPaint.Resolve(Scheme, UiTone.Danger, highContrast: false);
                    Paint(stage.Widget, "Progress track", meter.Empty.Fill, 100, 154, 400, 18);
                    var fill = Paint(stage.Widget, "Progress fill", meter.Filled.Fill, 100, 154, 99.75f, 18);
                    Label(stage.Widget, "Label", progress.DisplayLabel, UiTextStyle.Body, ink, 106, 112, 200);
                    Cue(stage.Widget, UiGlyph.Cross, ink);
                    Check(stage.Read(out var result, out _), "Threshold progress recording must use the Status child already selected by Visit.");
                    Check(result!.Nodes.Single(node => node.Id == "load") is { State: "critical", Fraction: { } fraction } &&
                        Math.Abs(fraction - .249375f) < .00001f &&
                        result.Nodes.Single(node => node.Kind == "TrackFill").Width == 99.75f &&
                        result.Nodes.Any(node => node.Path == "/0:load/cue:Cross"), "Progress records the actual fill bounds and critical cue.");
                    fill.rectTransform.Frame = new UiCaptureRect(100, 154, 75, 18);
                    Check(!stage.Read(out _, out _), "The recorder rejects a stale native progress fill.");
                }

                private static void StatBar()
                {
                    var bar = new UiStatBar("Capacity", 50, 100, new UiThresholds(55, 40), UiStatFormat.ValueOfMaximum, id: "capacity");
                    var stage = new Stage(bar);
                    var meter = UiStatusMeterPaint.Resolve(Scheme, UiTone.Warning, highContrast: false);
                    var filled = new RectTransform(stage.Widget, "Stat filled clip", 100, 100, 199.75f, 48);
                    var empty = new RectTransform(stage.Widget, "Stat empty clip", 299.75f, 100, 200.25f, 48);
                    filled.Attach(new RectMask2D());
                    empty.Attach(new RectMask2D());
                    Rounded(stage.Widget, "Stat track", meter.Empty.Fill);
                    Rounded(filled, "Stat fill", meter.Filled.Fill);
                    Rounded(stage.Widget, "Stat boundary", Scheme.ResolveBoundary(UiPaintRole.Surface)).ResolvedBorderWidth = 1f;
                    Rounded(stage.Widget, "Stat shadow", meter.Empty.Fill).enabled = false;
                    Label(filled, "Label", bar.DisplayLabel, UiTextStyle.Label, meter.Filled.On, 253, 112, 200);
                    Label(empty, "Label", bar.DisplayLabel, UiTextStyle.Label, meter.Empty.On, 253, 112, 200);
                    Cue(filled, UiGlyph.Warning, meter.Filled.On);
                    Cue(empty, UiGlyph.Warning, meter.Empty.On);
                    Check(stage.Read(out var result, out _), "Stat bar recording must use the Status child already selected by Visit.");
                    var onFill = result!.Nodes.Single(node => node.Kind == "StatFilledLabel");
                    var onTrack = result.Nodes.Single(node => node.Kind == "StatEmptyLabel");
                    Check(result.Nodes.Single(node => node.Id == "capacity") is { State: "warning", Fraction: { } fraction } &&
                        Math.Abs(fraction - .499375f) < .00001f && onFill.IsRendered && onTrack.IsRendered &&
                        onFill.X + onFill.Width == onTrack.X && onFill.FillColor != onTrack.FillColor,
                        "The observed stat label halves meet at the native mask edge with distinct semantic paint.");
                    empty.Frame = new UiCaptureRect(301, 100, 199, 48);
                    Check(!stage.Read(out _, out _), "The recorder rejects a gap between the native stat masks.");
                }

                private static void Legacy()
                {
                    var timer = new UiCountdown(60d, "Relay", id: "legacy-timer");
                    UiClockBinding.Bind(timer, Epoch);
                    var stage = new Stage(timer, "Legacy countdown");
                    Label(stage.Widget, "Label", "Relay 8", UiTextStyle.Body, Scheme.ResolveForeground(UiTone.Neutral, UiPaintRole.Surface),
                        106, 112, 200);
                    Check(stage.Read(out var captured, out _) && captured!.Nodes.Single(node => node.Id == timer.Id).RemainingSeconds == 8d,
                        "Legacy countdown capture still reads its single Body label.");
                    var progress = new UiProgress(.5f, "Signal", id: "legacy-progress");
                    stage = new Stage(progress, "Legacy progress");
                    var pair = Scheme.GetPair(UiPaintRole.Surface);
                    var track = Paint(stage.Widget, "Progress", pair.Fill, 100, 154, 400, 18);
                    Paint(track.rectTransform, "Fill", pair.On, 100, 154, 200, 18);
                    Label(stage.Widget, "Label", progress.Label, UiTextStyle.Body,
                        Scheme.ResolveForeground(progress.Tone, UiPaintRole.Surface), 106, 112, 200);
                    Check(stage.Read(out captured, out _) && captured!.Nodes.Single(node => node.Id == progress.Id).Fraction == .5f,
                        "Legacy progress capture still finds the fill inside its track.");
                }

                private static TMP_Text Label(RectTransform parent, string name, string text, UiTextStyle role, UiThemeColor ink,
                    float x, float y, float width)
                {
                    var type = Theme.GetText(role);
                    return new RectTransform(parent, name, x, y, width, type.ReferenceSize).Attach(new TMP_Text
                    {
                        text = type.IsUppercase ? text.ToUpperInvariant() : text,
                        font = TopiaForgeFonts.ForThemeRole(type.Font, role == UiTextStyle.Mono ? TopiaForgeTextStyle.Mono : TopiaForgeTextStyle.Body),
                        fontSize = type.ReferenceSize,
                        color = new Color(ink),
                    });
                }

                private static Image Paint(RectTransform parent, string name, UiThemeColor fill, float x, float y, float width, float height) =>
                    new RectTransform(parent, name, x, y, width, height).Attach(new Image { color = new Color(fill) });

                private static TopiaForgeRoundedImage Rounded(RectTransform parent, string name, UiThemeColor fill) =>
                    new RectTransform(parent, name, 100, 100, 400, 48).Attach(new TopiaForgeRoundedImage
                    { color = new Color(fill), ResolvedCornerRadius = 24f });

                private static Image Cue(RectTransform parent, UiGlyph glyph, UiThemeColor ink) =>
                    new RectTransform(parent, "Status cue", 100, 112, 20, 20).Attach(new Image { color = new Color(ink), sprite = glyph });

                private sealed class Stage
                {
                    private readonly Canvas canvas;
                    private readonly RectTransform surface;
                    private readonly RectTransform composition;
                    private readonly UiSurfaceRequest request;
                    internal RectTransform Widget { get; }

                    internal Stage(UiNode fixture, string widgetName = "Status")
                    {
                        var screen = new RectTransform(null, "Canvas", 0, 0, 1920, 1080);
                        canvas = screen.Attach(new Canvas());
                        surface = new RectTransform(screen, "Surface", 80, 80, 440, 200);
                        Paint(surface, "Fill", Scheme.GetPair(UiPaintRole.Surface).Fill, 80, 80, 440, 200);
                        composition = new RectTransform(surface, "Composition", 96, 96, 408, 170);
                        var leaf = new RectTransform(composition, fixture.Id ?? fixture.GetType().Name, 100, 100, 400, 100);
                        var host = new RectTransform(leaf, "Widget", 100, 100, 400, 100);
                        Widget = new RectTransform(host, widgetName, 100, 100, 400, 100);
                        request = new UiSurfaceRequest("fixture", "Status", "", UiSurfaceKind.Hud, content: fixture);
                    }

                    internal bool Read(out UiTreeCapture? result, out UiGalleryNativeTree recorder,
                        double clockTime = Now, string clockEpoch = Epoch)
                    {
                        recorder = new UiGalleryNativeTree(canvas, new TopiaForgeResolvedTheme(Theme),
                            new TopiaForgeEffectiveAccessibility(1f), false, clockTime, clockEpoch);
                        return recorder.TryCapture(request, surface, composition, null, out result);
                    }
                }

                private static void Check(bool passed, string detail)
                {
                    if (!passed) throw new InvalidOperationException(detail);
                }
            }
            """;
    }
}
