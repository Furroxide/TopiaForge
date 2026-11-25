using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiOverlayNativeTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var kit = "src/TopiaForge.Mods.UnityUi/";
            var files = new[]
            {
                "Core/ITopiaForgeDismissable.cs", "Core/TopiaForgeDismissOrder.cs", "Core/TopiaForgeEasing.cs",
                "Core/TopiaForgeInputClaim.cs", "Core/TopiaForgeInputGate.cs", "Core/TopiaForgeLayerBands.cs", "Core/TopiaForgeLayerLedger.cs",
                "Core/TopiaForgeKeyCapture.cs", "Core/TopiaForgeNavigationBindings.cs", "Core/TopiaForgeNavigationDriver.cs", "Core/TopiaForgeTokens.cs", "Motion/TopiaForgeMotionPresets.cs",
                "Motion/TopiaForgeTween.cs", "Runtime/TopiaForgeCursor.cs", "Runtime/TopiaForgeDismissStack.cs",
                "Runtime/TopiaForgeFocus.cs", "Runtime/TopiaForgeLayers.cs", "Runtime/TopiaForgeOverlayStack.cs",
                "Runtime/TopiaForgeInput.cs", "Runtime/TopiaForgeInputOwnership.cs",
                "Runtime/TopiaForgeInputOwnership.Capture.cs", "Runtime/TopiaForgeInputOwnership.PauseEscape.cs",
                "Widgets/TopiaForgeBindingCaptureInput.cs", "Widgets/TopiaForgeInputBindingField.cs",
                "Widgets/TopiaForgeInputBindingState.cs", "Widgets/TopiaForgeOverlay.cs", "UiHost.Overlays.cs",
            };
            var trees = files.Select(file => kit + file).Append("tests/Shared/AllocationProbe.cs")
                .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)), path: file))
                .Append(CSharpSyntaxTree.ParseText(Boundaries, path: "OverlayUnityBoundaries.cs"))
                .Append(CSharpSyntaxTree.ParseText(Scenarios, path: "OverlayNativeScenarios.cs"))
                .Append(CSharpSyntaxTree.ParseText(UiInputEventBoundaries.Sources, path: "InputEventBoundaries.cs"))
                .Append(CSharpSyntaxTree.ParseText(InputWidgetBoundaries, path: "OverlayInputWidgetBoundaries.cs"))
                .Append(CSharpSyntaxTree.ParseText(EscapeScenarios, path: "OverlayEscapeScenarios.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    !Path.GetFileName(path).StartsWith("Unity.InputSystem", StringComparison.Ordinal) &&
                    !Path.GetFileName(path).StartsWith("Unity.TextMeshPro", StringComparison.Ordinal) &&
                    Path.GetFileName(path) != "TopiaForge.Mods.UnityUi.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.Mods.UnityUi", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emit = compilation.Emit(bytes);
            if (!emit.Success)
                throw new InvalidOperationException(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var method = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.Mods.UnityUi.OverlayProbe", true)!
                .GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!;
            ((Action)method.CreateDelegate(typeof(Action)))();
        }

        private const string Scenarios = """"
            using System;
            using TopiaForge.Mods;
            using TopiaForge.TestSupport;
            using UnityEngine;
            using UnityEngine.EventSystems;
            using UnityEngine.UI;

            namespace TopiaForge.Mods.UnityUi
            {
                internal static partial class OverlayProbe
                {
                    internal static void Run()
                    {
                        TopiaForgeInputOwnership.Install();
                        try { EscapeRouting(); }
                        finally { TopiaForgeInputOwnership.Reset(); }
                        PaintAndSafeArea();
                        CreationOrder();
                        FocusAndBack();
                        ManagerAndTrust();
                        ProtectedConnector();
                        GamePause();
                        ThemeMotionAndRetirement();
                        CanonicalComponent();
                        Console.WriteLine("UiOverlayNativeTests passed 8 scenarios through the production overlay and input stack.");
                    }

                    private static void PaintAndSafeArea()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true, EffectiveUiScale = 2f };
                        Screen.safeArea = new Rect(200, 120, 1440, 840);
                        var paint = New(host, false);
                        paint.Show();
                        var scrim = paint.Go.Find("Overlay scrim").GetComponent<Image>();
                        var panel = paint.Go.Find("Overlay panel");
                        var fill = panel.Find("Surface").GetComponent<TopiaForgeRoundedImage>();
                        var title = paint.Content.Go.Find("Label").GetComponent<TMPro.TMP_Text>();
                        var body = paint.Content.Label("Body", TopiaForgeTextStyle.Body);
                        Check(scrim.color.a == 0.72f && scrim.color.r == host.Theme.Surface.r && !scrim.raycastTarget &&
                            fill.color.a == 1f && title.Style == TopiaForgeTextStyle.Title &&
                            TopiaForgeDismissStack.Claims == TopiaForgeInputClaim.None && !Raycasts(paint),
                            "presentation-only overlays draw Surface at 72% scrim and opaque title backplate, with no input");
                        Check(panel.Rect.sizeDelta.x == 684f && panel.Rect.sizeDelta.y == 336f &&
                            Near(paint.Rect.anchorMin.x, 200f / 1920f) && Near(paint.Rect.anchorMax.y, 960f / 1080f) &&
                            paint.MeasureContent(body).MaxHeight > 0 && panel.Rect.anchoredPosition.y == 0,
                            "the panel is centred in the physical safe area and clamped at scale two with an animation reserve");
                        var revision = paint.ViewportRevision;
                        Screen.safeArea = new Rect(204, 130, 1440, 840);
                        paint.RefreshViewport();
                        Check(paint.ViewportRevision == revision + 1 && Near(paint.Rect.anchorMin.x, 204f / 1920f),
                            "an inset shift without a resolution change updates the viewport");
                        body.Go.GetComponent<TMPro.TMP_Text>().text = new string('x', 8000);
                        try
                        {
                            paint.MeasureContent(body);
                            throw new InvalidOperationException("oversized body should fail");
                        }
                        catch (UiLayoutException error) { Check(error.Failure == UiLayoutFailure.ClippedContent, "retained fit error"); }
                        paint.Retire(() => {});
                        Screen.safeArea = new Rect(0, 0, 1920, 1080);
                    }

                    private static void CreationOrder()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var oldest = New(host, true);
                        var survivor = New(host, true);
                        var survivorControl = Control(survivor);
                        oldest.Retire(() => {});
                        var newest = New(host, false);
                        survivor.Show();
                        newest.Show();
                        Check(newest.Canvas.sortingOrder > survivor.Canvas.sortingOrder && !Raycasts(survivor),
                            "reusing a retired low slot keeps the newly created overlay above every survivor");
                        survivor.Close();
                        survivor.Show();
                        Check(!Raycasts(survivor) && !survivor.Go.GetComponent<CanvasGroup>().interactable &&
                            TopiaForgeDismissStack.Top() == null,
                            "a lower overlay gains neither focus nor Back by reopening under a decoration");
                        newest.Close();
                        Check(Raycasts(survivor) && EventSystem.current!.currentSelectedGameObject == survivorControl.gameObject,
                            "removing the top decoration selects the live lower overlay");
                        newest.Retire(() => {});
                        survivor.Retire(() => {});
                        Check(TopiaForgeLayers.HeldSlots == 0, "permuted overlay slots are returned exactly once");
                    }

                    private static void FocusAndBack()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var previous = new GameObject("Game selection");
                        EventSystem.current!.SetSelectedGameObject(previous);
                        var pinned = New(host, true, dismissible: false);
                        var disabled = Control(pinned);
                        disabled.Interactable = false;
                        var first = Control(pinned);
                        var next = Control(pinned);
                        pinned.Show();
                        Check(Raycasts(pinned) && EventSystem.current.currentSelectedGameObject == first.gameObject &&
                            TopiaForgeDismissStack.HoldsFocus && TopiaForgeCursor.ActiveLeases == 1,
                            "an interactive top overlay takes input and selects its first enabled control");
                        Back();
                        Back();
                        Check(pinned.Go.activeSelf && ReferenceEquals(TopiaForgeDismissStack.Top(), pinned),
                            "every distinct Back press keeps the non-dismissible overlay and its ownership");
                        next.Select();
                        var dismissible = New(host, true);
                        var closed = 0;
                        dismissible.Closed += () => closed++;
                        Control(dismissible);
                        dismissible.Show();
                        Check(TopiaForgeOverlayStack.IsCovered(pinned) && !TopiaForgeOverlayStack.IsCovered(dismissible),
                            "the capture reads the covered lower overlay from the actual painted stack");
                        Check(Raycasts(dismissible) && !Raycasts(pinned) && TopiaForgeCursor.ActiveLeases == 1,
                            "only the top visible overlay acquires raycasts and a cursor lease");
                        Back();
                        Check(!TopiaForgeOverlayStack.IsCovered(pinned), "capture sees the restored lower overlay");
                        Check(closed == 1 && !dismissible.Go.activeSelf && TopiaForgeDismissStack.Top() == pinned &&
                            EventSystem.current.currentSelectedGameObject == next.gameObject,
                            "Back hides the dismissible top and restores the previously selected lower control");
                        pinned.SetCompositionReady(false);
                        Check(!Raycasts(pinned) && TopiaForgeDismissStack.Top() == null,
                            "an overlay displaying only the retained layout notice does not trap input");
                        pinned.SetCompositionReady(true);
                        Check(EventSystem.current.currentSelectedGameObject == next.gameObject, "recovery restores focus");
                        var allocations = AllocationProbe.Measure("settled native overlay", () =>
                        {
                            pinned.RefreshViewport();
                            pinned.RefreshFocus();
                            TopiaForgeOverlayStack.SetGamePaused(false);
                            TopiaForgeTween.Tick(0.016f);
                        }, frames: 10000, warmups: 32);
                        Check(allocations == 0 && pinned.Go.ComponentQueries == 1, "settled overlay allocates and scans no controls at rest");
                        dismissible.Retire(() => {});
                        pinned.Retire(() => {});
                        Check(EventSystem.current.currentSelectedGameObject == previous && TopiaForgeCursor.ActiveLeases == 0,
                            "retirement returns the game's previous selection and cursor");
                    }

                    private static void ManagerAndTrust()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var window = new Surface(TopiaForgeLayerBand.Window);
                        var manager = new Surface(TopiaForgeLayerPriority.Manager);
                        var trust = new Surface(TopiaForgeLayerBand.Modal);
                        var toast = new Surface(TopiaForgeLayerBand.Toast);
                        var overlay = New(host, true);
                        Control(overlay);
                        window.Push();
                        overlay.Show();
                        Check(TopiaForgeDismissStack.Top() == overlay && window.Order < overlay.Canvas.sortingOrder,
                            "ordinary windows and tools stay below a gameplay overlay");
                        manager.Push();
                        Check(EventSystem.current!.currentSelectedGameObject == null &&
                            !overlay.Go.GetComponent<CanvasGroup>().interactable,
                            "the manager removes the covered gameplay selection before accepting keyboard input");
                        trust.Push();
                        EventSystem.current!.SetSelectedGameObject(trust.Root);
                        overlay.RefreshFocus();
                        Check(TopiaForgeDismissStack.Top() == trust && EventSystem.current.currentSelectedGameObject == trust.Root &&
                            overlay.Canvas.sortingOrder < manager.Order && manager.Order < trust.Order && trust.Order < toast.Order,
                            "trust stays above the manager and gameplay overlays in the dismiss and paint order");
                        Back();
                        Check(trust.Dismissals == 1 && TopiaForgeDismissStack.Top() == manager && overlay.Go.activeSelf,
                            "dismissing trust leaves the manager and gameplay overlay open");
                        Back();
                        Check(manager.Dismissals == 1 && TopiaForgeDismissStack.Top() == overlay && overlay.Go.activeSelf,
                            "the manager has its own Back precedence");
                        overlay.Retire(() => {});
                        window.Dispose();
                        manager.Dispose();
                        trust.Dispose();
                        toast.Dispose();
                    }

                    private static void GamePause()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var overlay = New(host, true);
                        var control = Control(overlay);
                        overlay.Show();
                        TopiaForgeOverlayStack.SetGamePaused(true);
                        Check(!overlay.Go.activeSelf && !Raycasts(overlay) && TopiaForgeDismissStack.Top() == null &&
                            TopiaForgeCursor.ActiveLeases == 0, "an open game pause menu gets the screen and its input");
                        TopiaForgeOverlayStack.SetGamePaused(false);
                        Check(overlay.Go.activeSelf && Raycasts(overlay) && EventSystem.current!.currentSelectedGameObject == control.gameObject,
                            "unpause restores the same overlay and selection");
                        overlay.Retire(() => {});
                    }

                    private static void ProtectedConnector()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        try
                        {
                            TopiaForgeLayers.CreateCanvas("Public cast", TopiaForgeLayerPriority.TrustOverlay, true, true);
                            throw new InvalidOperationException("public cast should fail");
                        }
                        catch (ArgumentOutOfRangeException) { }

                        var prompt = new TopiaForgeModalInstance { IsTrust = true };
                        var promptRoot = TopiaForgeLayers.CreateTrustCanvas("Consent prompt", true);
                        TopiaForgeDismissStack.Push(prompt, TopiaForgeInputClaim.Navigation, promptRoot);
                        EventSystem.current!.SetSelectedGameObject(promptRoot);
                        var toast = TopiaForgeLayers.CreateCanvas("System toast", TopiaForgeLayerBand.Toast, false, true);
                        var progress = host.TrustOverlay("Connecting", 680f);
                        var action = Control(progress);
                        progress.LimitTrustedScrollBody();
                        progress.Show();
                        Check(progress.CanvasRoot.Persistent && progress.Scheme == TopiaForgeScheme.Paper &&
                            ReferenceEquals(progress.ResolvedTheme.Descriptor, UiThemePackParser.Default) &&
                            progress.Content.Go.GetComponent<TopiaForgeModalColumn>().MotionReserve == 48f,
                            "the loader keeps its protected Paper theme and reserves the whole motion path above the scroll body");
                        Check(progress.Canvas.sortingOrder > toast.GetComponent<Canvas>().sortingOrder &&
                            progress.Canvas.sortingOrder < promptRoot.GetComponent<Canvas>().sortingOrder &&
                            ReferenceEquals(TopiaForgeDismissStack.Top(), prompt) && !progress.HasNavigationFocus &&
                            EventSystem.current.currentSelectedGameObject == promptRoot,
                            "an existing consent prompt remains over newly created connecting progress in paint and input");
                        Back();
                        Check(prompt.Dismissals == 1 && progress.Go.activeSelf, "Back affects the consent before the connecting overlay");
                        progress.RefreshFocus();
                        Check(progress.HasNavigationFocus && EventSystem.current.currentSelectedGameObject == action.gameObject,
                            "the connector resumes its own action focus when higher consent closes");
                        progress.Retire(() => {});
                        TopiaForgeLayers.Release(toast);
                        TopiaForgeLayers.Release(promptRoot);
                        Check(TopiaForgeLayers.HeldSlots == 0, "all protected and ordinary allocations retire through the same ledger");
                    }

                    private static void ThemeMotionAndRetirement()
                    {
                        var host = new UiHost
                        {
                            Theme = new TopiaForgeResolvedTheme(Parse(
                                """{"schema":1,"id":"overlay-animated","motion":{"fastMs":100,"baseMs":300,"slowMs":400,"ease":"outCubic"},"components":{"overlay":{"radius":"small","border":"thin","shadow":"small"}}}"""))
                        };
                        var overlay = New(host, false);
                        var scrim = overlay.Go.Find("Overlay scrim");
                        var panel = overlay.Go.Find("Overlay panel");
                        var border = panel.Find("Boundary").GetComponent<TopiaForgeRoundedImage>();
                        Check(border.Border == 1 && panel.Find("Shadow").GetComponent<TopiaForgeRoundedImage>().enabled,
                            "authored Overlay component controls the boundary and shadow");
                        host.Theme.HighContrast = true;
                        overlay.ApplyTheme(host.Theme);
                        Check(border.Border == 3 && !panel.Find("Shadow").GetComponent<TopiaForgeRoundedImage>().enabled,
                            "high contrast strengthens the authored edge and drops its shadow");
                        overlay.Show();
                        Check(panel.Rect.anchoredPosition.y == -24 && scrim.GetComponent<CanvasGroup>().alpha == 0,
                            "entry begins 24 reference units below rest with an unpainted scrim");
                        TopiaForgeTween.Tick(0.15f);
                        Check(Near(panel.Rect.anchoredPosition.y, -3f) && Near(scrim.GetComponent<CanvasGroup>().alpha, 0.875f),
                            "the authored base duration and ease drive both motion tracks");
                        TopiaForgeTween.Tick(0.15f);
                        overlay.Close();
                        TopiaForgeTween.Tick(0.05f);
                        Check(overlay.Go.activeSelf && Near(overlay.Go.GetComponent<CanvasGroup>().alpha, 0.125f),
                            "the painted overlay remains for its authored fast-exit fade");
                        overlay.Show();
                        TopiaForgeTween.Tick(0.35f);
                        Check(overlay.Go.activeSelf, "reopening cancels the stale exit completion");
                        var retired = 0;
                        overlay.Retire(() => retired++);
                        Check(retired == 0 && overlay.Go.activeSelf && TopiaForgeLayers.HeldSlots == 1,
                            "retirement retains the visual and assets until exit ends");
                        host.EffectiveReducedMotion = true;
                        TopiaForgeTween.Tick(0);
                        Check(retired == 1 && !overlay.Go.activeSelf && TopiaForgeLayers.HeldSlots == 0,
                            "live reduced motion completes the exit and retires the layer immediately");
                        var reduced = New(host, false);
                        reduced.Show();
                        Check(reduced.Go.Find("Overlay panel").Rect.anchoredPosition.y == 0 && TopiaForgeTween.ActiveCount == 0,
                            "starting with reduced motion schedules no tween");
                        reduced.Retire(() => retired++);
                        Check(retired == 2 && TopiaForgeLayers.HeldSlots == 0, "reduced-motion disposal completes synchronously");
                        var zero = New(host, false);
                        host.EffectiveReducedMotion = false;
                        host.MotionIntensity = 0;
                        zero.Show();
                        Check(TopiaForgeTween.ActiveCount == 0 && zero.Go.Find("Overlay panel").Rect.anchoredPosition.y == 0,
                            "zero motion intensity settles entry immediately");
                        host.MotionIntensity = 1;
                        zero.Retire(() => retired++);
                        host.MotionIntensity = 0;
                        TopiaForgeTween.Tick(0);
                        Check(retired == 3 && TopiaForgeLayers.HeldSlots == 0, "zero intensity also settles a running exit");
                    }

                    private static void CanonicalComponent()
                    {
                        var omitted = Parse("""{"schema":1,"id":"overlay-sample"}""");
                        var defaults = Parse("""{"schema":1,"id":"overlay-sample","components":{"overlay":{"radius":"card","border":"standard","shadow":"none"}}}""");
                        var different = Parse("""{"schema":1,"id":"overlay-sample","components":{"overlay":{"radius":"small","border":"standard","shadow":"none"}}}""");
                        Check(omitted.Digest.Span.SequenceEqual(defaults.Digest.Span) &&
                            !omitted.Digest.Span.SequenceEqual(different.Digest.Span) &&
                            different.MinimumWireVersion == UiWireCodec.ControlsContractVersion,
                            "an effective Overlay override changes the digest and requires contract v3");
                        Check(Convert.ToHexString(UiThemePackParser.Default.Digest.Span) ==
                            "4602CE2902CF9C95898AEA57EDFD9E9293DD563941CBF191240C299393D89E3A",
                            "the embedded defaults fingerprint remains stable");
                    }

                    private static UiThemeDescriptor Parse(string source)
                    {
                        var result = UiThemePackParser.Parse(System.Text.Encoding.UTF8.GetBytes(source));
                        Check(result.Succeeded, result.ErrorMessage);
                        return result.Value!;
                    }

                    private static Selectable Control(TopiaForgeOverlay overlay) =>
                        overlay.Content.CreateChildGameObject("Control").AddComponent<Selectable>();

                    private static bool Raycasts(TopiaForgeOverlay overlay) =>
                        overlay.CanvasRoot.GetComponent<GraphicRaycaster>().enabled;

                    private static TopiaForgeOverlay New(UiHost host, bool interactive, bool dismissible = true)
                    {
                        var root = TopiaForgeLayers.CreateKitCanvas("Overlay", TopiaForgeLayerPriority.Overlay, false, true);
                        var layer = new TopiaForgeContainer(host, TopiaForgeScheme.Hud, root);
                        var overlay = new TopiaForgeOverlay(layer, "Overlay title", 1200, 700,
                            interactive ? UiOverlayContentKind.Interactive : UiOverlayContentKind.PresentationOnly, dismissible);
                        host.Register(overlay);
                        return overlay;
                    }

                    private static void Back()
                    {
                        Input.EscapeHeld = true;
                        TopiaForgeDismissStack.TickEscape();
                        Input.EscapeHeld = false;
                        TopiaForgeDismissStack.TickEscape();
                    }

                    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;
                    private static void Check(bool condition, string message)
                    {
                        if (!condition) throw new InvalidOperationException(message);
                    }

                    private sealed class Surface : ITopiaForgeDismissable
                    {
                        internal Surface(TopiaForgeLayerBand band)
                        {
                            Band = band;
                            Root = TopiaForgeLayers.CreateKitCanvas("Test surface", Band, true, true);
                        }

                        public TopiaForgeLayerBand Band { get; }
                        internal GameObject Root { get; }
                        internal int Order => Root.GetComponent<Canvas>().sortingOrder;
                        internal int Dismissals { get; private set; }
                        internal void Push() => TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Navigation, Root);
                        public void Dismiss() { Dismissals++; Root.SetActive(false); }
                        internal void Dispose() { TopiaForgeDismissStack.Remove(this); TopiaForgeLayers.Release(Root); }
                    }
                }
            }
            """";
    }
}
