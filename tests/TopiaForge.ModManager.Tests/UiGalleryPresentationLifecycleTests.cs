using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryPresentationLifecycleTests
    {
        internal static void Run()
        {
            var gallery = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.UiGallery");
            var trees = new[] { "GalleryWindow.cs", "GalleryPresentation.cs", "Pages/PresentationPages.cs" }
                .Select(name => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(gallery, name)), path: name))
                .Append(CSharpSyntaxTree.ParseText(ShellBoundary, path: "GalleryWindowBoundary.cs"))
                .Append(CSharpSyntaxTree.ParseText(Scenarios, path: "GalleryPresentationScenarios.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    Path.GetFileName(path) != "TopiaForge.Mods.UnityUi.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.UiGallery", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success)
                throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var verify = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.UiGallery.PresentationLifecycleProbe", true)!
                .GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Action<string, Func<string, string>>>();
            string Read(string name) => File.ReadAllText(Path.Combine(gallery, name));
            verify(Read("topiaforge.mod.json"), Read);
        }

        private const string Scenarios = """"
            using System;
            using System.Linq;
            using System.Reflection;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.UiGallery.Pages;

            namespace TopiaForge.UiGallery
            {
                internal static class PresentationLifecycleProbe
                {
                    private static readonly UiThemeRef PreviousTheme = new UiThemeRef("prior-gallery-theme");
                    private static readonly UiAccessibilityPreferences PriorSettings = new UiAccessibilityPreferences(uiScale: 1.25f);
                    private static readonly UiCaptureViewport Viewport = new UiCaptureViewport(1920, 1080);

                    internal static void Run(string manifest, Func<string, string> read)
                    {
                        PlayEventsInShell(manifest, read, TopiaForgeScheme.Paper);
                        PlayEventsInShell(manifest, read, TopiaForgeScheme.Hud);
                        SelectPaletteInBothShells(manifest, read);
                        OverlayStackAndTeardown(manifest, read);
                        FailedAdmissionsRestoreTheGallery(manifest, read);
                        Console.WriteLine("F8 Gallery presentation lifecycle passed for both shells, public SDK events and overlay teardown.");
                    }

                    private static void PlayEventsInShell(string manifest, Func<string, string> read, TopiaForgeScheme scheme)
                    {
                        using var context = Prepare(manifest, read);
                        var host = new UiHost();
                        using var gallery = new GalleryWindow(host, context);
                        var shell = EnterOverlays(gallery, host, scheme);

                        shell.Content.Page.Press("Stingers");
                        var stingers = context.Ui.Surfaces.Single();
                        Check(!shell.IsOpen && stingers.Request.Kind == UiSurfaceKind.Window &&
                            context.Ui.Themes.SelectedTheme?.Equals(FoundationPages.SurvivalTheme) == true &&
                            context.Ui.Accessibility.HighContrast && context.Ui.Accessibility.UiScale == 2f &&
                            context.Ui.Accessibility.ReducedMotion,
                            "The F8 launcher inherits the Gallery selection and accessibility through its ordinary SDK service.");
                        context.Ui.CompletePendingThemeRegistrations();
                        foreach (var role in PresentationPages.StingerRoles)
                        {
                            Require(stingers.ActivateButton(PresentationPages.StingerButtonId(role)));
                            var panel = CaptureEffect(context.Ui, "CaptureStinger").Nodes.Single(node => node.Kind == "Stinger");
                            Check(context.Ui.ActiveStinger?.Request.Role == role && panel.ThemeId == "survival" && panel.State == "shown",
                                $"Every requested role paints in the selected stinger theme with reduced motion. {role} used {panel.ThemeId} and {panel.State}.");
                            context.AdvanceFrame(TimeSpan.FromSeconds(3));
                        }
                        Require(stingers.ActivateButton(PresentationPages.StingerQueueId));
                        var queue = context.Ui.Stingers.Skip(PresentationPages.StingerRoles.Count).ToArray();
                        Check(queue.Length == 4 && queue[1].Request.Title == "Water ready" && queue[1].Request.DurationSeconds == 1d &&
                            queue[3].Request.Title == "Lanterns lit" && stingers.Body == "The announcement queue is full.",
                            "The F8 queue uses SDK coalescing, preserves the matched duration and reports overflow.");
                        for (var index = 0; index < 4; index++) context.AdvanceFrame(TimeSpan.FromSeconds(1.01));

                        Check(stingers.TryFindNode(PresentationPages.StingerQueueId, out var previousButton), "The launcher has a queue button.");
                        gallery.Toggle();
                        ((UiButton)previousButton!).Activated();
                        Check(shell.IsOpen && !stingers.IsShown && context.Ui.Surfaces.Count == 0 &&
                            context.Ui.Stingers.Count == PresentationPages.StingerRoles.Count + 4 && Restored(context),
                            "F8 disposes the launcher, restores its originating shell and restores the prior owner profile.");

                        shell.Content.Page.Press("Kit toasts");
                        var toasts = context.Ui.Surfaces.Single();
                        Require(toasts.ActivateButton(PresentationPages.ToastStackId));
                        var toastPaint = CaptureEffect(context.Ui, "CaptureToasts").Nodes.Where(node => node.Kind == "Toast").ToArray();
                        Check(context.Ui.Themes.SelectedTheme?.Equals(FoundationPages.SurvivalTheme) == true &&
                            context.Ui.Toasts.Select(item => item.Tone).SequenceEqual(PresentationPages.ToastTones) &&
                            toastPaint.Length == 4 && toastPaint.All(node => node.ThemeId == "topiaforge.default"),
                            "The four ShowToast calls keep the kit paint under the sample theme.");
                        toasts.DismissFromPlayer();
                        gallery.Tick(0f);
                        Check(shell.IsOpen && context.Ui.Surfaces.Count == 0 && Restored(context),
                            "Back from the SDK launcher restores the originating shell and its prior selection.");
                    }

                    private static void SelectPaletteInBothShells(string manifest, Func<string, string> read)
                    {
                        using var context = Prepare(manifest, read);
                        var host = new UiHost();
                        using var gallery = new GalleryWindow(host, context);
                        gallery.Toggle();
                        host.SetAccessibilityProfile(new TopiaForgeAccessibilityProfile(true, 1.5f, true, 0f));
                        var paper = host.Windows[0].Content;
                        var hud = host.Windows[1].Content;
                        Check(paper.Palette.Label == "Survival" && hud.Palette.Label == "Survival",
                            "Both F8 shells start in Survival.");
                        OpenAndCheck(paper, UiSurfaceKind.Window, "survival");
                        paper.Palette.Choose(0);
                        Check(hud.Palette.Label == "Kit", "The Kit selection is mirrored to the HUD view.");
                        OpenAndCheck(paper, UiSurfaceKind.Window, "topiaforge.default");
                        paper.Press("SWAP SCHEME");
                        OpenAndCheck(hud, UiSurfaceKind.Hud, "topiaforge.default");
                        hud.Palette.Choose(1);
                        Check(paper.Palette.Label == "Survival", "The Survival selection is mirrored to the Paper view.");
                        OpenAndCheck(hud, UiSurfaceKind.Hud, "survival");
                        hud.Press("SWAP SCHEME");
                        OpenAndCheck(paper, UiSurfaceKind.Window, "survival");

                        void OpenAndCheck(TopiaForgeContainer shell, UiSurfaceKind kind, string palette)
                        {
                            shell.Press("Typography");
                            context.Ui.CompletePendingThemeRegistrations();
                            var surface = context.Ui.Surfaces.Single();
                            var captured = surface.Capture(Viewport);
                            Check(surface.Request.Kind == kind &&
                                captured.Nodes.Any(node => node.ThemeId == palette) &&
                                context.Ui.Accessibility.HighContrast && context.Ui.Accessibility.UiScale == 1.5f,
                                $"The ordinary {kind} paints {palette} with viewer accessibility. " +
                                $"Observed {string.Join(", ", captured.Nodes.Select(node => node.ThemeId).Distinct())}.");
                            gallery.Toggle();
                            Check(context.Ui.Surfaces.Count == 0 && Restored(context),
                                "Closing the ordinary preview restores the prior scope theme and accessibility.");
                        }
                    }

                    private static void OverlayStackAndTeardown(string manifest, Func<string, string> read)
                    {
                        using var context = Prepare(manifest, read);
                        var host = new UiHost();
                        var gallery = new GalleryWindow(host, context);
                        using var lifetimeLease = context.Lifetime.Track(gallery);
                        var shell = EnterOverlays(gallery, host, TopiaForgeScheme.Hud);
                        shell.Content.Page.Press("Overlay examples");
                        var launcher = context.Ui.Surfaces.Single();
                        foreach (var variant in PresentationPages.OverlayOrder)
                        {
                            Require(launcher.ActivateButton(PresentationPages.OverlayButtonId(variant)));
                            var overlay = context.Ui.Surfaces.Single(surface => surface.Request.Kind == UiSurfaceKind.Overlay);
                            overlay.DismissFromPlayer();
                            Check(overlay.IsShown == (variant != PresentationOverlay.Dismissible) && context.Ui.Surfaces.Count == 2,
                                "Each individual overlay uses its authored Back and input policy.");
                            if (variant == PresentationOverlay.ButtonOnly) Require(overlay.ActivateButton(PresentationPages.OverlayCloseId));
                            gallery.Tick(0f);
                        }

                        Require(launcher.ActivateButton(PresentationPages.OverlayStackId));
                        var passive = Surface(context, "gallery-overlay-passive");
                        var back = Surface(context, "gallery-overlay-dismissible");
                        var buttonOnly = Surface(context, "gallery-overlay-buttononly");
                        var hud = Surface(context, "gallery-presentation-hud");
                        Check(hud.IsVisible && hud.TryFindNode("gallery-presentation-status", out var status) &&
                            status is UiText { Text: "Supplies loaded." },
                            "The real Gallery updates its existing HUD underneath the full overlay stack.");
                        for (var index = 0; index < 100; index++) gallery.Tick(0f);
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        for (var index = 0; index < 10000; index++) gallery.Tick(0f);
                        Check(GC.GetAllocatedBytesForCurrentThread() == before, "The settled Gallery presentation tick allocates no bytes.");
                        buttonOnly.DismissFromPlayer();
                        Check(buttonOnly.IsShown && back.ActivateButton(PresentationPages.OverlayCloseId).ErrorCode == ModErrorCode.InvalidState,
                            "The button-only top overlay holds input and ignores Back.");
                        Require(buttonOnly.ActivateButton(PresentationPages.OverlayCloseId));
                        gallery.Tick(0f);
                        Check(!context.Ui.Surfaces.Contains(buttonOnly) && back.IsVisible && hud.IsVisible,
                            "The enabled close button retires only its overlay and preserves the HUD.");
                        back.DismissFromPlayer();
                        gallery.Tick(0f);
                        Check(!context.Ui.Surfaces.Contains(back) && passive.IsVisible && hud.IsVisible,
                            "Back retires the dismissible overlay and leaves the passive overlay above the same HUD.");
                        gallery.Toggle();
                        Check(shell.IsOpen && context.Ui.Surfaces.Count == 0 && !hud.IsShown && !passive.IsShown,
                            "Leaving the presentation retires its remaining passive overlay and HUD.");

                        shell.Content.Page.Press("Overlay examples");
                        Require(context.Ui.Surfaces.Single().ActivateButton(PresentationPages.OverlayStackId));
                        var beforeUnload = context.Ui.Surfaces.ToArray();
                        context.Dispose();
                        Check(beforeUnload.Length == 5 && beforeUnload.All(surface => !surface.IsShown) && context.Ui.Surfaces.Count == 0,
                            "The real Gallery lifetime releases the launcher, the entire stack and its HUD on unload.");
                    }

                    private static void FailedAdmissionsRestoreTheGallery(string manifest, Func<string, string> read)
                    {
                        using var context = Prepare(manifest, read);
                        var host = new UiHost();
                        using var gallery = new GalleryWindow(host, context);
                        var shell = EnterOverlays(gallery, host, TopiaForgeScheme.Paper);
                        using (var existing = Require(context.Ui.CreateSurface(PresentationPages.StingerControls(_ => { }, () => { }))))
                        {
                            shell.Content.Page.Press("Stingers");
                            Check(shell.IsOpen && ReferenceEquals(context.Ui.Surfaces.Single(), existing) && Restored(context),
                                "A rejected launcher leaves the prior owner theme, preferences and surface intact.");
                        }

                        using var existingOverlay = Require(context.Ui.CreateSurface(
                            PresentationPages.Overlay(PresentationOverlay.Passive, () => { })));
                        shell.Content.Page.Press("Overlay examples");
                        Require(Surface(context, "gallery-presentation-overlays").ActivateButton(PresentationPages.OverlayStackId));
                        Check(context.Ui.Surfaces.Count == 2 && existingOverlay.IsVisible,
                            "A rejected stack retires its staged HUD and preserves the pre-existing surface.");
                    }

                    private static FakeModContext Prepare(string manifest, Func<string, string> read)
                    {
                        var context = new FakeModContext();
                        Require(context.Ui.AddThemeCatalogue(manifest, read));
                        Require(context.Ui.AddThemeFixture("""{"schema":1,"id":"prior-gallery-theme"}"""));
                        Require(context.Ui.Themes.UseTheme(PreviousTheme));
                        Require(context.Ui.ApplyAccessibility(PriorSettings));
                        return context;
                    }

                    private static bool Restored(FakeModContext context) =>
                        context.Ui.Themes.SelectedTheme?.Equals(PreviousTheme) == true && ReferenceEquals(context.Ui.Accessibility, PriorSettings);

                    private static TopiaForgeWindow EnterOverlays(GalleryWindow gallery, UiHost host, TopiaForgeScheme scheme)
                    {
                        gallery.Toggle();
                        if (scheme == TopiaForgeScheme.Hud) host.Windows[0].Content.Press("SWAP SCHEME");
                        var shell = host.Windows.Single(window => window.Scheme == scheme);
                        Check(shell.IsOpen && !shell.Content.Buttons.ContainsKey("Stingers") && shell.Content.Page.Buttons.Count == 0,
                            "The presentation launchers only appear on the existing Overlays tab.");
                        shell.Content.TabBar.Select("OVERLAYS");
                        Check(shell.Content.Page.Buttons.ContainsKey("Existing overlay demos") &&
                            shell.Content.Page.Buttons.ContainsKey("Stingers") && shell.Content.Page.Buttons.ContainsKey("Overlay examples") &&
                            shell.Content.Page.Buttons.ContainsKey("Kit toasts"),
                            "The existing Overlays tab offers all three presentation launchers alongside its earlier demos.");
                        host.SetAccessibilityProfile(new TopiaForgeAccessibilityProfile(true, 2f, true, 1f));
                        return shell;
                    }

                    private static FakeUiSurface Surface(FakeModContext context, string id) =>
                        context.Ui.Surfaces.Single(surface => surface.Id == id);

                    private static UiTreeCapture CaptureEffect(FakeUiService ui, string method) =>
                        (UiTreeCapture)typeof(FakeUiService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(ui, new object[] { Viewport })!;

                    private static T Require<T>(OperationResult<T> result) where T : notnull
                    {
                        if (!result.TryGetValue(out var value)) throw new InvalidOperationException(result.ErrorMessage);
                        return value;
                    }

                    private static void Check(bool passed, string reason)
                    {
                        if (!passed) throw new InvalidOperationException(reason);
                    }
                }
            }
            """";
    }
}
