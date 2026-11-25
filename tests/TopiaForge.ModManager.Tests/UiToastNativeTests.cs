using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiToastNativeTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var kit = "src/TopiaForge.Mods.UnityUi/";
            var files = new[]
            {
                kit + "Core/TopiaForgeCorner.cs", kit + "Core/TopiaForgeHudLayout.cs",
                kit + "Core/TopiaForgeAccessibilityProfile.cs", kit + "Core/TopiaForgeEasing.cs",
                kit + "Motion/TopiaForgeTween.cs",
                kit + "Core/TopiaForgeToastStack.cs", kit + "Widgets/TopiaForgeToast.cs",
                kit + "Widgets/TopiaForgeToast.Appearance.cs", kit + "Widgets/TopiaForgeToast.Capture.cs",
                kit + "Widgets/TopiaForgeToast.Layout.cs",
                "tests/Shared/AllocationProbe.cs",
            };
            var trees = files.Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)), path: file))
                .Append(CSharpSyntaxTree.ParseText(ToastMotion(root, kit), path: "NativeToastMotion.cs"))
                .Append(CSharpSyntaxTree.ParseText(Boundaries, path: "ToastUnityBoundary.cs"))
                .Append(CSharpSyntaxTree.ParseText(OverflowScenarios, path: "ToastOverflowScenarios.cs"))
                .Append(CSharpSyntaxTree.ParseText(Scenarios, path: "ToastScenarios.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    Path.GetFileName(path) != "TopiaForge.Mods.UnityUi.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.Mods.UnityUi", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emit = compilation.Emit(bytes);
            if (!emit.Success)
                throw new InvalidOperationException(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var run = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.ModManager.ToastProbe", true)!
                .GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!;
            ((Action)run.CreateDelegate(typeof(Action)))();
        }

        private static string ToastMotion(string root, string kit)
        {
            var source = File.ReadAllText(Path.Combine(root, kit + "Motion/TopiaForgeMotionPresets.cs"));
            var methods = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>().Where(method => method.Identifier.ValueText is "ToastIn" or "ToastOut");
            return "using System; namespace TopiaForge.Mods.UnityUi { public static class TopiaForgeMotion { "
                + string.Join("\n", methods) + "} }";
        }

        private const string Scenarios = """"
            using System;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.TestSupport;
            using UnityEngine;
            using UnityEngine.UI;
            using TMPro;

            namespace TopiaForge.ModManager
            {
                internal static partial class ToastProbe
                {
                    internal static void Run()
                    {
                        PaintsAndReusesToneGlyphs();
                        MovesAboveShownHudsAndReappliesAccessibility();
                        ObservesTheOwnedNativeView();
                        QueuesAboveATallHud();
                        ResumesVisibleToastsAfterViewportGrowth();
                        PendingQueueStaysBoundedAndIdle();
                        PendingViewerScaleCanOpenSpace();
                        PendingColumnUsesMeasuredTextHeight();
                        OutdatedReflowCannotMoveBehindHud();
                        Console.WriteLine("UiToastNativeTests passed for native tone cues, bounded plain text, retained paint, pooled views and toast clearance.");
                    }

                    private static void PaintsAndReusesToneGlyphs()
                    {
                        const string Plain = "<b>A toast\ntwo\nthree\nfour</b>";
                        TopiaForgeTheme.ReducedMotion = true;
                        TopiaForgeToasts.ShowPlain(Plain, TopiaForgeTone.Success, 1f);
                        TopiaForgeToasts.ShowPlain("Warning", TopiaForgeTone.Warning, 2f);
                        TopiaForgeToasts.ShowPlain("Danger", TopiaForgeTone.Danger, 2f);
                        TopiaForgeToasts.ShowPlain("Neutral", duration: 2f);
                        TopiaForgeToasts.ShowPlain(new string('x', 513), duration: 3f);
                        var canvas = TopiaForgeUi.LastHost.LayerRoot;
                        var first = canvas.transform.Children[0];
                        var label = first.Find("Label").GetComponent<TextMeshProUGUI>();
                        var scheme = UiThemePackParser.Default.SelectScheme(hud: true, highContrast: false);
                        Check(TopiaForgeUi.LastHost.OwnerId == "io.github.furroxide.topiaforge.ui.toasts" &&
                            canvas.transform.Children.Count == 4 && !TopiaForgeUi.LastHost.Interactive,
                            "The shared kit host shows four noninteractive views and holds the fifth in its queue.");
                        Check(label.text == Plain && !label.richText && !label.parseCtrlCharacters &&
                            label.overflowMode == TextOverflowModes.Ellipsis && first.transform.sizeDelta.y == 84f,
                            "The actual plain toast caps measurement to three lines and lets TMP ellipsize literal data.");
                        CheckGlyph(first, UiGlyph.Check, scheme.ResolveForeground(UiTone.Success, UiPaintRole.Raised));
                        CheckGlyph(canvas.transform.Children[1], UiGlyph.Warning, scheme.ResolveForeground(UiTone.Warning, UiPaintRole.Raised));
                        CheckGlyph(canvas.transform.Children[2], UiGlyph.Cross, scheme.ResolveForeground(UiTone.Danger, UiPaintRole.Raised));
                        var neutral = canvas.transform.Children[3].Find("Tone glyph").GetComponent<Image>();
                        Check(!neutral.enabled && neutral.sprite == null &&
                            label.rectTransform.offsetMin.x > canvas.transform.Children[3].Find("Label").transform.offsetMin.x,
                            "An unmarked toast uses the space without leaving a previous cue.");
                        TopiaForgeToasts.Tick(1f);
                        var reused = first.Find("Tone glyph").GetComponent<Image>();
                        Check(canvas.transform.Children.Count == 4 && first.activeSelf && label.text.Length == 512 &&
                            !reused.enabled && reused.sprite == null && label.rectTransform.offsetMin.x == 14f,
                            "Finishing the first toast reuses the same view for a bounded, unmarked queued toast.");
                        TopiaForgeToasts.Reset();
                        TopiaForgeTheme.ReducedMotion = false;
                    }

                    private static void MovesAboveShownHudsAndReappliesAccessibility()
                    {
                        var dock = new TopiaForgeHudDock();
                        var far = dock.Add(TopiaForgeCorner.BottomLeft, 600f, 2f);
                        var near = dock.Add(TopiaForgeCorner.BottomRight, 120f, 2f);
                        var after = dock.Add(TopiaForgeCorner.BottomRight, 48f, 1f);
                        dock.SetShown(far, true);
                        dock.SetShown(near, true);
                        dock.SetShown(after, true);
                        var viewer = new UiHost("viewer")
                        {
                            Profile = new TopiaForgeAccessibilityProfile(uiScale: 2f, reducedMotion: true),
                        };
                        const float Reserve = .12f;
                        TopiaForgeToasts.SetBottomRightHudTop(dock.GetShownTop(TopiaForgeCorner.BottomRight), Reserve);
                        TopiaForgeToasts.ShowPlain("Confirmed", TopiaForgeTone.Success, viewer, 30f);
                        var view = TopiaForgeUi.LastHost.LayerRoot.transform.Children[0];
                        var expected = 18f + 240f + TopiaForgeHudDock.Gap + 48f + Screen.height * Reserve + 16f;
                        Check(Math.Abs(view.transform.anchoredPosition.y - expected) < .001f &&
                            view.transform.anchoredPosition.x == -36f && view.transform.localScale.x == 2f &&
                            view.Group.alpha == 1f && TopiaForgeTween.ActiveCount == 0,
                            "The scaled toast clears the dock and subtitle reserve with the viewer's motion disabled.");
                        dock.SetShown(near, false);
                        dock.Remove(after);
                        TopiaForgeToasts.SetBottomRightHudTop(dock.GetShownTop(TopiaForgeCorner.BottomRight));
                        viewer.Profile = new TopiaForgeAccessibilityProfile(highContrast: true, uiScale: .75f, reducedMotion: true);
                        TopiaForgeToasts.Tick(0f);
                        var scheme = UiThemePackParser.Default.SelectScheme(hud: true, highContrast: true);
                        var glyph = view.Find("Tone glyph").GetComponent<Image>();
                        var ring = view.Find("Ring").GetComponent<Image>();
                        var fill = view.Find("Fill").GetComponent<Image>();
                        var label = view.Find("Label").GetComponent<TextMeshProUGUI>();
                        Check(view.transform.anchoredPosition.y == 13.5f && view.transform.localScale.x == .75f &&
                            ring.sprite!.Stroke == 3f && !TopiaForgeTheme.HighContrast &&
                            fill.color == new Color(scheme.GetPair(UiPaintRole.Raised).Fill) &&
                            label.color == new Color(scheme.GetPair(UiPaintRole.Raised).On) &&
                            Math.Abs(glyph.rectTransform.sizeDelta.x * .75f - 20f) < .001f,
                            "Changing contrast, scale and HUD occupancy repaints the live view and preserves readable geometry.");
                        CheckGlyph(view, UiGlyph.Check, scheme.ResolveForeground(UiTone.Success, UiPaintRole.Raised));
                        var separate = new UiHost("standard");
                        TopiaForgeToasts.ShowPlain("Second", TopiaForgeTone.Warning, separate, 30f);
                        var second = TopiaForgeUi.LastHost.LayerRoot.transform.Children[1];
                        Check(TopiaForgeTween.ActiveCount == 2 && second.Group.alpha == 0f &&
                            second.Find("Ring").GetComponent<Image>().sprite!.Stroke == 2f,
                            "A different viewer profile keeps standard paint and starts its own motion.");
                        separate.Profile = new TopiaForgeAccessibilityProfile(motionIntensity: 0f);
                        TopiaForgeTween.Tick(0f);
                        Check(TopiaForgeTween.ActiveCount == 0 && second.Group.alpha == 1f &&
                            second.transform.anchoredPosition.x == -18f,
                            "Selecting zero motion settles an existing entrance immediately.");
                        var resting = AllocationProbe.Measure("native toast settled tick", static () => TopiaForgeToasts.Tick(0f),
                            frames: 10_000, warmups: 128);
                        Check(resting == 0, "Active, settled toasts allocate zero bytes.");
                        TopiaForgeToasts.Reset();
                    }

                    private static void ObservesTheOwnedNativeView()
                    {
                        var viewer = new UiHost("observed") { Profile = new TopiaForgeAccessibilityProfile(
                            highContrast: true, uiScale: 2f) };
                        int starts = 0, releases = 0;
                        TopiaForgeContainer? first = null;
                        Func<TopiaForgeToasts.ToastPaint?>? readFirst = null;
                        TopiaForgeToasts.ShowPlainObserved("First observed", TopiaForgeTone.Success, viewer, (view, read) =>
                        {
                            starts++;
                            first = view;
                            readFirst = read;
                            Check(view.Go.activeSelf && view.Go.Find("Label").GetComponent<TextMeshProUGUI>().text == "First observed" &&
                                view.Go.Find("Tone glyph").GetComponent<Image>().sprite?.Glyph == UiGlyph.Check,
                                "The per-entry observer attaches after the real text and cue are painted.");
                            return () => releases++;
                        });
                        for (var index = 1; index < 4; index++)
                            TopiaForgeToasts.ShowPlain("Other " + index, duration: 10f);
                        TopiaForgeToasts.ShowPlainObserved("Queued observed", TopiaForgeTone.Warning, viewer, (view, read) =>
                        {
                            starts++;
                            Check(ReferenceEquals(view, first) && releases == 1 &&
                                view.Go.Find("Label").GetComponent<TextMeshProUGUI>().text == "Queued observed" &&
                                view.Go.Find("Tone glyph").GetComponent<Image>().sprite?.Glyph == UiGlyph.Warning,
                                "Only the newly painted entry owns the reused native view.");
                            return () => releases++;
                        });
                        TopiaForgeTween.Tick(1f);
                        Check(starts == 1 && readFirst!() is { Theme.HighContrast: true, Accessibility.UiScale: 2f,
                            Scale: 2f, RestingX: -36f },
                            "Only the visible toast exposes the settled viewer paint.");
                        viewer.Profile = new TopiaForgeAccessibilityProfile(highContrast: true, uiScale: 1.5f);
                        Check(readFirst!() == null, "A newly changed viewer scale waits for matching native paint.");
                        TopiaForgeToasts.Tick(0f);
                        TopiaForgeTween.Tick(1f);
                        Check(readFirst!() is { Accessibility.UiScale: 1.5f, Scale: 1.5f, RestingX: -27f },
                            "The observer reads the repainted viewer geometry.");
                        TopiaForgeToasts.Tick(3.5f);
                        Check(starts == 1 && releases == 1,
                            "The capture lease retires immediately when the first toast starts leaving.");
                        TopiaForgeTween.Tick(1f);
                        Check(starts == 2 && releases == 1, "The queued observer waits for the old native view to finish exiting.");
                        TopiaForgeToasts.Reset();
                        Check(releases == 2, "Process reset retires the current native view observer exactly once.");
                        Console.WriteLine("Native toast capture ownership passed through settlement, queued reuse, exit and reset.");
                    }

                    private static void CheckGlyph(GameObject view, UiGlyph expected, UiThemeColor ink)
                    {
                        var glyph = view.Find("Tone glyph").GetComponent<Image>();
                        Check(glyph.enabled && glyph.sprite?.Glyph == expected && glyph.color == new Color(ink) &&
                            !glyph.raycastTarget, "The tone's native glyph uses accessible ink over the kit's raised surface.");
                    }

                    private static void Check(bool condition, string description)
                    {
                        if (!condition) throw new InvalidOperationException(description);
                    }
                }
            }
            """";
    }
}
