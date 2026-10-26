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
            GalleryWindowsSurviveSceneLoads();
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
            var tick = Method("src/TopiaForge.ModManager/Overlay/ManagerOverlay.cs", "Tick");
            Check(PluginSourceSyntax.CallsOn(tick, "status", "Tick") && tick.ToString().Contains("Time.unscaledDeltaTime", StringComparison.Ordinal),
                "status ticks outside tab visibility with unscaled time");
        }

        private static void TargetControlsAreMeasuredAsAColumn()
        {
            var build = Method("src/TopiaForge.ModManager/Overlay/GamemodesTab.cs", "BuildTarget");
            var card = build.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(node => node.Identifier.ValueText == "card");
            Check(card.Initializer?.Value is InvocationExpressionSyntax call && call.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.ValueText == "Column", "each target reserves the measured height of a column");
            Check(PluginSourceSyntax.CallsOn(build, "card", "Label") && PluginSourceSyntax.CallsOn(build, "card", "Row"), "title and launch controls share that column");
        }

        private static void GalleryWindowsSurviveSceneLoads()
        {
            var build = Method("mods/TopiaForge.UiGallery/GalleryWindow.cs", "Build");
            var window = build.DescendantNodes().OfType<InvocationExpressionSyntax>().Single(call =>
                call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Window");
            Check(window.ArgumentList.Arguments.Any(argument => argument.NameColon?.Name.Identifier.ValueText == "persistent" &&
                argument.Expression.IsKind(SyntaxKind.TrueLiteralExpression)), "both gallery schemes use persistent windows");
        }

        private static void ViewportResizeRebindsTheProductionWatcher()
        {
            var list = CSharpSyntaxTree.ParseText(Read("src/TopiaForge.Mods.UnityUi/Widgets/TopiaForgeListView.cs")).GetRoot();
            var initialize = list.DescendantNodes().OfType<InvocationExpressionSyntax>().SingleOrDefault(call =>
                call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Initialize" &&
                member.Expression.ToString().Contains("TopiaForgeListViewRuntime", StringComparison.Ordinal));
            Check(initialize != null && PluginSourceSyntax.Calls(initialize, "Refresh"), "each list wires viewport changes to its real refresh");

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
