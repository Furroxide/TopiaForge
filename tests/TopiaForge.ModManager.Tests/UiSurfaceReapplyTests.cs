using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiSurfaceReapplyTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var files = new[]
            {
                "src/TopiaForge.ModManager/UnityUiSurface.cs",
                "src/TopiaForge.ModManager/UnityUiSurface.OuterScroll.cs",
                "src/TopiaForge.ModManager/UnityUiSurface.Presence.cs",
                "src/TopiaForge.ModManager/UnityUiSurface.Artwork.cs",
                "src/TopiaForge.ModManager/UnityUiCompositionRenderer.cs",
                "src/TopiaForge.ModManager/UnityUiGraphRetention.cs",
                "src/TopiaForge.ModManager/UnityUiLayoutRenderer.cs",
                "src/TopiaForge.ModManager/UnityUiLayoutRenderer.KeyGlyphs.cs",
                "src/TopiaForge.ModManager/UiNativeActionBinding.cs",
                "src/TopiaForge.ModManager/UnityUiLayoutRenderer.Leaves.cs",
                "src/TopiaForge.ModManager/UnityUiLayoutRenderer.Tabs.cs",
                "src/TopiaForge.ModManager/UnityUiLayoutDivider.cs",
                "src/TopiaForge.Mods.UnityUi/Widgets/TopiaForgeGraphCanvas.Retention.cs",
                "src/TopiaForge.ModManager/UnityUiWorldWidgets.cs",
                "src/TopiaForge.ModManager/UnityMainThreadGuard.cs",
                "src/TopiaForge.ModManager/UiSurfacePresence.cs",
                "src/TopiaForge.ModManager/UiThemeScope.cs",
                "src/TopiaForge.ModManager/UiThemeScope.Preload.cs",
                "src/TopiaForge.ModManager/UiThemeAcquisitions.cs",
                "src/TopiaForge.ModManager/UiThemeRegistration.cs",
                "src/TopiaForge.ModManager/OwnerModLifetime.cs",
                "src/TopiaForge.ModManager/AssetNativeWorkTracker.cs",
                "src/TopiaForge.ModManager/IHostDispatcher.cs",
                "src/TopiaForge.ModManager/HostDispatcher.cs",
                "src/TopiaForge.ModManager/Multiplayer/Presentation/IPresentationSurfaceState.cs",
                "tests/Shared/AllocationProbe.cs",
            };
            // Compile the shipped surface, safe-node renderer, and countdown driver against Unity-boundary doubles.
            var trees = files.Select(relative =>
                CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, relative)), path: relative))
                .Append(CSharpSyntaxTree.ParseText(OuterScrollAdapter(root), path: "OuterScrollAdapter.cs"))
                .Append(CSharpSyntaxTree.ParseText(Boundaries, path: "SurfaceTestBoundaries.cs"))
                .Append(CSharpSyntaxTree.ParseText(EngineBoundaries, path: "SurfaceTestEngine.cs"))
                .Append(CSharpSyntaxTree.ParseText(GraphBoundaries, path: "SurfaceTestGraph.cs"))
                .Append(CSharpSyntaxTree.ParseText(GraphScenarios, path: "SurfaceTestGraphScenarios.cs"))
                .Append(CSharpSyntaxTree.ParseText(ArtworkScenarios, path: "SurfaceTestArtwork.cs"))
                .Append(CSharpSyntaxTree.ParseText(Scenarios, path: "SurfaceTestScenarios.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && Path.GetFileName(path) != "TopiaForge.Mods.UnityUi.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emit = compilation.Emit(bytes);
            if (!emit.Success)
                throw new InvalidOperationException(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.ModManager.OwnerUiService", throwOnError: true)!;
            ((Action)probe.GetMethod("ProbeReapply", BindingFlags.Public | BindingFlags.Static)!
                .CreateDelegate(typeof(Action)))();
            Console.WriteLine("UiSurfaceReapplyTests passed for Window, FullscreenTool, HUD, retained graphs and local tab selection.");
        }

        private static string OuterScrollAdapter(string root)
        {
            var file = Path.Combine(root, "src/TopiaForge.ModManager/UnityUiSurface.Layout.cs");
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            var methods = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
            var measure = methods.Single(method => method.Identifier.ValueText == "MeasureContentArea")
                .WithIdentifier(SyntaxFactory.Identifier("MeasureNativeContentArea"));
            var refresh = methods.Single(method => method.Identifier.ValueText == "UpdateRetainedLayout")
                .WithIdentifier(SyntaxFactory.Identifier("UpdateNativeRetainedLayout"));
            var preferred = methods.Single(method => method.Identifier.ValueText == "PreferredSize");
            var show = methods.Single(method => method.Identifier.ValueText == "ShowCompositionLayout");
            return "using System; using TMPro; using TopiaForge.Mods; using TopiaForge.Mods.UnityUi; " +
                "using UnityEngine; using UnityEngine.UI; namespace TopiaForge.ModManager { " +
                "internal sealed partial class OwnerUiService { private sealed partial class UnityUiSurface { " +
                measure + "\n" + refresh + "\n" + preferred + "\n" + show + " } } }";
        }

        private const string Scenarios = """
            using System;
            using System.Linq;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.TestSupport;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    public static void ProbeReapply()
                    {
                        UnityMainThreadGuard.CaptureCurrentThread();
                        foreach (var kind in new[] { UiSurfaceKind.Window, UiSurfaceKind.FullscreenTool, UiSurfaceKind.Hud })
                            ProbeSurface(kind);
                        ProbeGraphAdmission();
                        ProbeTabs();
                        ProbeArtworkAdmission();
                        ProbeArtworkRetries(remote: false, retryWhileDraining: false);
                        ProbeArtworkRetries(remote: true, retryWhileDraining: false);
                        ProbeArtworkRetries(remote: false, retryWhileDraining: true);
                        ProbeOuterScroll(useFullscreen: false);
                        ProbeOuterScroll(useFullscreen: true);
                    }

                    private static void ProbeOuterScroll(bool useFullscreen)
                    {
                        var host = new UiHost();
                        var window = new TopiaForgeWindow(host);
                        var fullscreen = new TopiaForgeFullscreenTool(host);
                        var shell = useFullscreen ? (TopiaForgeWidget)fullscreen : window;
                        shell.Rect.rect = new Rect { width = 500f, height = 260f };
                        var scrollRoot = new GameObject();
                        scrollRoot.transform.SetParent(shell.Rect, false);
                        var viewport = new GameObject();
                        viewport.transform.SetParent(scrollRoot.transform, false);
                        viewport.transform.rect = new Rect { width = 458f, height = 156f };
                        var parent = new TopiaForgeContainer(host);
                        parent.Rect.SetParent(viewport.transform, false);
                        parent.Rect.rect = new Rect { width = 458f, height = 1100f };
                        var group = parent.Go.AddComponent<VerticalLayoutGroup>();
                        group.padding.top = group.padding.bottom = 16;
                        group.spacing = 8f;
                        var scroll = scrollRoot.AddComponent<ScrollRect>();
                        scroll.content = parent.Rect;
                        scroll.viewport = viewport.transform;
                        scroll.vertical = true;
                        var body = parent.Label(new string('W', 200), TopiaForgeTextStyle.Body);
                        using var life = new FakeModLifetime();
                        var logger = new BoundaryLogger();
                        using var surface = useFullscreen
                            ? UnityUiSurface.ForFullscreen("outer", fullscreen, body, parent, life, logger, _ => { })
                            : UnityUiSurface.ForWindow("outer", window, body, parent, life, logger, _ => { });
                        surface.Present();

                        var invalid = new UiSizedBox(new UiButton("oversized", "Oversized", static () => { }), height: 2000f);
                        var refused = surface.SetContent(invalid);
                        Require(refused.ErrorCode == ModErrorCode.InvalidArgument &&
                            refused.ErrorMessage.Contains("/0/0", StringComparison.Ordinal) && parent.Children.Count == 0,
                            "The native staged surface rejects its unreachable target before committing or showing a child. Got " +
                            refused.ErrorCode + " " + refused.ErrorMessage + " with " + parent.Children.Count + " children.");

                        var actions = Enumerable.Range(0, 12).Select(index =>
                            (UiNode)new UiButton("action-" + index, "Inspect", static () => { }));
                        var tall = new UiSizedBox(new UiButton("resize", "Restore", static () => { }), height: 130f);
                        var line = new UiText(string.Join(" ", Enumerable.Repeat("Relay telemetry ready.", 100)), id: "wrapped");
                        var composition = new UiColumn(actions.Prepend(line).Append(tall));
                        Require(surface.SetContent(composition).Succeeded, "The scrolled body does not subtract from the native aperture.");
                        var retained = parent.Children.Single();
                        Require(retained.Go.activeSelf && surface.SetContent(invalid).ErrorCode == ModErrorCode.InvalidArgument &&
                            ReferenceEquals(retained, parent.Children.Single()),
                            "An invalid native replacement keeps the existing shown composition.");
                        parent.Rect.anchoredPosition = new Vector2(0f, 170f);
                        var builds = host.ColumnsBuilt;
                        var layout = LayoutRebuilder.Requests;
                        var allocated = AllocationProbe.Measure("settled native outer scroll", () =>
                            Require(surface.SetContent(composition).Succeeded, "Identical scroll content reapplication succeeds."),
                            frames: 4096, warmups: 32);
                        Require(allocated == 0 && host.ColumnsBuilt == builds && LayoutRebuilder.Requests == layout,
                            "The unchanged aperture requires 0 B, no rebuild and no remeasure over 4,096 native reapplications.");

                        viewport.transform.rect.height = 105f;
                        Require(surface.SetContent(composition).Succeeded && !retained.Go.activeSelf &&
                            surface.GraphConnectionsSuspended && parent.Children.Count == 2 && parent.Rect.anchoredPosition.y == 0f && scroll.MovementStops == 1,
                            "An aperture-only resize hides the original controls and resets the notice to the viewport start.");
                        viewport.transform.rect.height = 185f;
                        Require(surface.SetContent(composition).Succeeded && retained.Go.activeSelf &&
                            ReferenceEquals(retained, parent.Children.Single()) && parent.Rect.anchoredPosition.y == 170f &&
                            !surface.GraphConnectionsSuspended && scroll.MovementStops == 2 && logger.Errors == 0,
                            "The larger aperture restores the original native tree and its saved scroll offset.");
                    }

                    private static void ProbeTabs()
                    {
                        var host = new UiHost();
                        var parent = new TopiaForgeContainer(host);
                        using var lifetime = new FakeModLifetime();
                        using var surface = UnityUiSurface.ForWindow("tabs", new TopiaForgeWindow(host),
                            new TopiaForgeLabel(), parent, lifetime, new BoundaryLogger(), _ => { });
                        var notified = "";
                        UiTabs Tree(string selected) => new UiTabs("pages", new[]
                        {
                            new UiTab("first", "First", new UiText("Readiness")),
                            new UiTab("second", "Second", new UiText("Map")),
                        }, selected, id => notified = id);
                        surface.Present();
                        Require(surface.SetContent(Tree("first")).Succeeded, "The two native pages are admitted.");
                        var pages = parent.Children.Single().Children.Single().Children;
                        var first = pages[0];
                        var second = pages[1];
                        Require(first.Go.Shown && !second.Go.Shown, "Only the authored initial page is drawn.");
                        host.Buttons["Second"].Click();
                        Require(!first.Go.Shown && second.Go.Shown && ReferenceEquals(pages[0], first) &&
                            ReferenceEquals(pages[1], second) && notified == "second",
                            "Native tabs change immediately and retain both page instances before reporting the selection.");
                        Require(surface.SetContent(Tree("first")).Succeeded &&
                            parent.Children.Single().Children.Single().Children[1].Go.Shown,
                            "An unchanged authored tab preserves the viewer's selection across a new native revision.");
                    }

                    private static void ProbeSurface(UiSurfaceKind kind)
                    {
                        var host = new UiHost();
                        var parent = new TopiaForgeContainer(host);
                        var body = new TopiaForgeLabel();
                        using var lifetime = new FakeModLifetime();
                        var logger = new BoundaryLogger();
                        var window = new TopiaForgeWindow(host);
                        var fullscreen = new TopiaForgeFullscreenTool(host);
                        var hud = new TopiaForgeHudLayer(host);
                        using var surface = kind switch
                        {
                            UiSurfaceKind.Window => UnityUiSurface.ForWindow(
                                "probe", window, body, parent, lifetime, logger, _ => { }),
                            UiSurfaceKind.FullscreenTool => UnityUiSurface.ForFullscreen(
                                "probe", fullscreen, body, parent, lifetime, logger, _ => { }),
                            _ => UnityUiSurface.ForHud(
                                "probe", hud, body, parent, false, true, lifetime, logger, _ => { }),
                        };
                        var view = kind switch
                        {
                            UiSurfaceKind.Window => (TopiaForgeWidget)window,
                            UiSurfaceKind.FullscreenTool => fullscreen,
                            _ => hud,
                        };

                        var epoch = "starting-world";
                        var clock = 10d;
                        surface.SetClock(() => clock, () => epoch);
                        surface.Present();
                        var countdown = new UiCountdown(70, "Remaining", id: "timer");
                        Require(surface.SetContent(countdown).Succeeded, "Initial countdown should be admitted");
                        var rendered = parent.Children.Single();
                        var label = rendered.Go.GetComponentsInChildren<TopiaForgeLabel>(includeInactive: true).Single();
                        Require(countdown.ClockEpoch == epoch && label.Seconds == 60,
                            "An admitted countdown binds once and initially renders current seconds");

                        if (kind == UiSurfaceKind.Hud)
                        {
                            var result = surface.SetContent(new UiColumn(new UiRow(new UiButton("control", "Action", () => { }))));
                            Require(result.ErrorCode == ModErrorCode.InvalidArgument && ReferenceEquals(rendered, parent.Children.Single()),
                                "A local HUD replacement rejects nested interactive controls and preserves the admitted view");
                        }

                        var builds = host.ColumnsBuilt;
                        var layout = LayoutRebuilder.Requests;
                        var driverQueries = host.ComponentQueries;
                        surface.Hide();
                        surface.Show();
                        var allocated = AllocationProbe.Measure("same-reference native surface update",
                            () => Require(surface.SetContent(countdown).Succeeded, "Re-applying settled content should succeed"),
                            frames: 2048, warmups: 32);
                        Require(allocated == 0, "A settled re-apply must allocate zero bytes");
                        Require(host.ColumnsBuilt == builds && LayoutRebuilder.Requests == layout
                            && host.ComponentQueries == driverQueries && ReferenceEquals(parent.Children.Single(), rendered),
                            "A settled re-apply must not build, relayout, or reinitialize the shown content");

                        host.ResolvedTheme = new TopiaForgeResolvedTheme();
                        Require(surface.SetContent(countdown).Succeeded && LayoutRebuilder.Requests == ++layout,
                            "A changed resolved theme requests a layout refresh");
                        Screen.width++;
                        Screen.height++;
                        Require(surface.SetContent(countdown).Succeeded && LayoutRebuilder.Requests == ++layout,
                            "A changed display viewport requests a layout refresh");
                        var rect = view.Rect.rect;
                        rect.width++;
                        view.Rect.rect = rect;
                        Require(surface.SetContent(countdown).Succeeded && LayoutRebuilder.Requests == ++layout,
                            "A resized view requests a layout refresh without a display change");

                        surface.Hide();
                        epoch = "next-world";
                        clock = 2d;
                        Require(surface.SetContent(countdown).Succeeded && LayoutRebuilder.Requests == ++layout,
                            "A retained countdown refreshes under a new epoch even when hidden");
                        Require(countdown.ClockEpoch == "starting-world" && label.Seconds == 0
                            && host.ComponentQueries == driverQueries + 1,
                            "The existing countdown refreshes to zero without changing its binding");
                        surface.Show();
                        Require(surface.SetContent(countdown).Succeeded && LayoutRebuilder.Requests == layout
                            && host.ColumnsBuilt == builds && ReferenceEquals(rendered, parent.Children.Single()),
                            "Theme, viewport and clock refresh keep the existing native subtree");

                        var stale = surface.SetContent(new UiColumn(countdown));
                        Require(stale.ErrorCode == ModErrorCode.InvalidArgument && ReferenceEquals(rendered, parent.Children.Single()),
                            "A new tree cannot admit a countdown from an ended epoch");

                        var newCountdown = new UiCountdown(30, "New round");
                        var newTree = new UiColumn(newCountdown, new UiText("Render error fixture"));
                        host.FailedLabel = "Render error fixture";
                        var rejected = surface.SetContent(newTree);
                        Require(rejected.ErrorCode == ModErrorCode.External && newCountdown.ClockEpoch == null
                            && ReferenceEquals(rendered, parent.Children.Single()),
                            "A renderer failure rolls back the candidate view without binding its clock");
                        Require(surface.SetContent(countdown).Succeeded && LayoutRebuilder.Requests == layout,
                            "A failed admission keeps the preceding render stamp");

                        host.FailedLabel = null;
                        host.FailNextDestroy = true;
                        epoch = "retry-world";
                        Require(surface.SetContent(newTree).Succeeded && newCountdown.ClockEpoch == epoch
                            && !rendered.Go.Shown && logger.Errors == 1,
                            "Retry binds in the current epoch, and a cleanup error cannot report a failed admission");

                        surface.HandleLost();
                        surface.SetBody("The lost view ignores a body write");
                        surface.Show();
                        Require(!surface.IsVisible && surface.SetContent(newTree).ErrorCode == ModErrorCode.InvalidState,
                            "A lost surface refuses a same-reference update and cannot reopen");
                    }

                    private static void Require(bool condition, string message)
                    {
                        if (!condition) throw new InvalidOperationException(message);
                    }
                }
            }
            """;
    }
}
