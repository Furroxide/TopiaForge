using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static class OverlayRefreshTests
    {
        internal static void Run()
        {
            StatusExpiresAndNewFeedbackGetsItsOwnTime();
            TargetControlsAreMeasuredAsAColumn();
            GallerySurfacesSurviveSceneLoads();
            ViewportResizeRebindsTheProductionWatcher();
            Console.WriteLine("OverlayRefreshTests passed: status lifetime, target layout, gallery lifetime, viewport resize.");
        }

        private static void StatusExpiresAndNewFeedbackGetsItsOwnTime()
        {
            var status = new ManagerStatus();
            Check(!status.Tick(30) && status.Text == ManagerStatus.Default, "idle status");
            status.Set("Leave the session before starting another world.");
            Check(!status.Tick(7) && status.Text != ManagerStatus.Default, "feedback remains readable");
            Check(status.Tick(1) && status.Text == ManagerStatus.Default, "feedback expires after eight unscaled seconds");
            Check(!status.Tick(1), "expiry only changes the label once");
            status.Set("First result");
            status.Tick(7);
            status.Set("New result");
            Check(!status.Tick(1) && status.Text == "New result", "a new result gets its own lifetime");
            Check(!status.Tick(float.NaN) && !status.Tick(float.PositiveInfinity) && !status.Tick(-1), "invalid elapsed values do not poison the clock");
            Check(status.Tick(7) && status.Text == ManagerStatus.Default, "replacement eventually expires");
            // The overlay counts the feedback down every frame, shown or not, on unscaled time, which runs while the game is
            // paused: the whole condition of one of Tick's own statements, which puts the label back when it expires.
            var tick = Method("src/TopiaForge.ModManager/Overlay/ManagerOverlay.cs", "Tick");
            var countdown = tick.DescendantNodes().OfType<InvocationExpressionSyntax>().SingleOrDefault(call =>
                call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Tick"
                && member.Expression is IdentifierNameSyntax { Identifier.ValueText: "status" });
            Check(countdown != null && countdown.ArgumentList.Arguments.Count == 1
                && countdown.ArgumentList.Arguments[0].Expression.ToString() == "Time.unscaledDeltaTime",
                "status counts down on unscaled time, which runs while the game is paused");
            Check(countdown!.Parent is IfStatementSyntax expiry && expiry.Condition == countdown && expiry.Parent == tick.Body
                && PluginSourceSyntax.CallsOn(expiry.Statement, "statusLabel", "SetText"),
                "status counts down every frame, whether the overlay shows or not, and its label follows when it expires");
        }

        private static void TargetControlsAreMeasuredAsAColumn()
        {
            var build = Method("src/TopiaForge.ModManager/Overlay/GamemodesTab.cs", "BuildTarget");
            var card = build.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(node => node.Identifier.ValueText == "card");
            Check(card.Initializer?.Value is InvocationExpressionSyntax call && call.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.ValueText == "Column", "each target reserves the measured height of a column");
            Check(PluginSourceSyntax.CallsOn(build, "card", "Label") && PluginSourceSyntax.CallsOn(build, "card", "Row"), "title and launch controls share that column");
        }

        // A scene load destroyed the gallery's window while the mod kept it, so every F8 threw ObjectDisposedException. Each
        // surface the gallery keeps for later lives as long as the mod, as the SDK's own surfaces do: both scheme windows,
        // the demo, shop and scoreboard windows its pages reopen, the demo HUD, and the chat layer. Only the name tags stay with their scene: every
        // spawn makes them afresh at points in it, and a tag whose canvas a load destroyed is no longer tracked.
        private static void GallerySurfacesSurviveSceneLoads()
        {
            var factories = new[] { "Window", "ShopWindow", "FullscreenTool", "HudLayer", "Layer" };
            var folder = Path.Combine(Program.FindRepoRoot(), "mods", "TopiaForge.UiGallery");
            var kept = 0;
            var sceneBound = 0;
            foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                var source = Path.GetRelativePath(folder, file).Replace('\\', '/');
                if (source.StartsWith("bin/", StringComparison.Ordinal) || source.StartsWith("obj/", StringComparison.Ordinal)) continue;
                foreach (var call in CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!(call.Expression is MemberAccessExpressionSyntax member) || !factories.Contains(member.Name.Identifier.ValueText)) continue;
                    if (call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { Token.ValueText: "gallery-nametags" })
                    {
                        sceneBound++;
                        continue;
                    }

                    kept++;
                    Check(call.ArgumentList.Arguments.Any(argument => argument.NameColon?.Name.Identifier.ValueText == "persistent" &&
                        argument.Expression.IsKind(SyntaxKind.TrueLiteralExpression)),
                        source + " makes a " + member.Name.Identifier.ValueText + " that a scene load destroys while the gallery keeps it: " + call);
                }
            }

            Check(kept == 6 && sceneBound == 1, "the gallery makes its scheme windows, the demo, shop and scoreboard windows, the demo HUD, the chat layer and the name tags, "
                + "one call each; found " + kept + " kept and " + sceneBound + " scene-bound");
        }

        private static void ViewportResizeRebindsTheProductionWatcher()
        {
            var list = CSharpSyntaxTree.ParseText(Read("src/TopiaForge.Mods.UnityUi/Widgets/TopiaForgeListView.cs")).GetRoot();
            var initialize = list.DescendantNodes().OfType<InvocationExpressionSyntax>().SingleOrDefault(call =>
                call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Initialize" &&
                member.Expression.ToString().Contains("TopiaForgeListViewRuntime", StringComparison.Ordinal));
            Check(initialize != null && PluginSourceSyntax.Calls(initialize, "Refresh"), "each list wires viewport changes to its real refresh");
            Check(initialize!.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax { Identifier.ValueText: "viewportRect" }
                && list.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
                    assignment.Left.ToString() == "scrollRect.viewport" && assignment.Right.ToString() == "viewportRect"),
                "each list watches the viewport its scroll view clips to, whose height layout resolves, not the content it sizes");

            // Run the production MonoBehaviour against a minimal dimensions contract, without a game or renderer.
            var sources = new[]
            {
                Read("src/TopiaForge.Mods.UnityUi/Widgets/TopiaForgeListViewRuntime.cs"),
                Read("src/TopiaForge.Mods.UnityUi/Core/TopiaForgeVirtualListMath.cs"),
                """
                using System;
                using System.Reflection;
                namespace UnityEngine
                {
                    public class MonoBehaviour { }
                    public sealed class RectTransform { public Rect rect; }
                    public struct Rect { public float height; }
                }
                public static class ViewportProbe
                {
                    public static void Run()
                    {
                        var viewport = new UnityEngine.RectTransform();
                        var watcher = new TopiaForge.Mods.UnityUi.TopiaForgeListViewRuntime();
                        var calls = 0;
                        var rows = 0;
                        watcher.Initialize(viewport, () => {
                            calls++;
                            rows = TopiaForge.Mods.UnityUi.TopiaForgeVirtualListMath.VisibleRange(0, viewport.rect.height, 9, 64, 4).Count;
                        });
                        void Invoke(string name) => watcher.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(watcher, null);
                        Invoke("LateUpdate");
                        if (calls != 0) throw new Exception("An unresolved viewport must wait.");
                        viewport.rect.height = 160;
                        Invoke("LateUpdate");
                        if (calls != 1 || rows != 6) throw new Exception("The transient viewport shows six rows.");
                        viewport.rect.height = 680;
                        Invoke("LateUpdate");
                        if (calls != 2 || rows != 9) throw new Exception("Layout must reveal all nine without scrolling.");
                        for (var frame = 0; frame < 100; frame++) Invoke("LateUpdate");
                        if (calls != 2) throw new Exception("Stable dimensions must not rebind rows.");
                        Invoke("OnEnable");
                        Invoke("LateUpdate");
                        if (calls != 3) throw new Exception("Reopening must refresh the viewport.");
                        Invoke("OnDestroy");
                        viewport.rect.height = 720;
                        Invoke("LateUpdate");
                        if (calls != 3) throw new Exception("A destroyed list must release its callback.");
                    }
                }
                """
            };
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = trusted.Where(path => new[] { "System.Private.CoreLib.dll", "System.Runtime.dll" }.Contains(Path.GetFileName(path)))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("ViewportProbe", sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            Check(result.Success, "viewport contract compiles: " + string.Join("; ", result.Diagnostics.Select(item => item.ToString())));
            Assembly.Load(bytes.ToArray()).GetType("ViewportProbe")!.GetMethod("Run")!.Invoke(null, null);
        }

        private static MethodDeclarationSyntax Method(string file, string name) =>
            CSharpSyntaxTree.ParseText(Read(file)).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == name);

        private static string Read(string file) => File.ReadAllText(Path.Combine(Program.FindRepoRoot(), file));
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Overlay refresh failed: " + message);
        }
    }
}
