using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
                "src/TopiaForge.ModManager/UnityUiSurface.Presence.cs",
                "src/TopiaForge.ModManager/UnityUiCompositionRenderer.cs",
                "src/TopiaForge.ModManager/UnityUiWorldWidgets.cs",
                "src/TopiaForge.ModManager/UnityMainThreadGuard.cs",
                "src/TopiaForge.ModManager/UiSurfacePresence.cs",
                "src/TopiaForge.ModManager/Multiplayer/Presentation/IPresentationSurfaceState.cs",
                "tests/Shared/AllocationProbe.cs",
            };
            // Compile the shipped surface, safe-node renderer, and countdown driver against Unity-boundary doubles.
            var trees = files.Select(relative =>
                CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, relative)), path: relative))
                .Append(CSharpSyntaxTree.ParseText(Boundaries, path: "SurfaceTestBoundaries.cs"))
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
            Console.WriteLine("UiSurfaceReapplyTests passed for Window, FullscreenTool and HUD.");
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
                        var label = rendered.Children.Single().Labels.Single();
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
