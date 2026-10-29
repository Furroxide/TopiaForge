using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Runs the production pool and feed with kit dispatch doubles; native rendering is qualified in-game.</summary>
    internal static partial class PresenceNametagViewTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var sources = new[]
            {
                File.ReadAllText(Path.Combine(root, "src/TopiaForge.ModManager/Overlay/NametagOverlay.cs")),
                File.ReadAllText(Path.Combine(root, "src/TopiaForge.ModManager/Multiplayer/Sessions/ScoreboardNametagFeed.cs")),
                Contracts.Replace("CORE_GAMEPLAY_LOOP", ReadClass(root,
                    "src/TopiaForge.ModManager/CoreGameplayServices.cs", "CoreGameplayLoop")),
                "using UnityEngine; namespace TopiaForge.Mods.UnityUi { " + ReadClass(root,
                    "src/TopiaForge.Mods.UnityUi/Widgets/TopiaForgeNametagLayer.cs", "TopiaForgeNametagDriver") + " }",
                Probe,
            };
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var compilation = CSharpCompilation.Create("PresenceNametagProbe" + Guid.NewGuid().ToString("N"),
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), trusted.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            That(result.Success, "production nametag overlay compiles: " + string.Join("; ", result.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)));
            var assembly = Assembly.Load(bytes.ToArray());
            try { assembly.GetType("NametagProbe")!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            Console.WriteLine("PresenceNametagViewTests passed: lazy bounded pool, moving anchors, hide/reuse, loss/rebuild, failed-build recovery and idempotent disposal.");
        }

        private static string ReadClass(string root, string path, string name) =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path))).GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().Single(item => item.Identifier.Text == name).ToFullString();

        private const string Probe = """
            using System;
            using System.Linq;
            using TopiaForge.ModManager;
            using TopiaForge.Mods.UnityUi;
            public static class NametagProbe
            {
                public static void Run()
                {
                    var show = false; var source = new Source(); IPresenceNametagSource current = source;
                    using var overlay = new NametagOverlay(() => current, () => show);
                    overlay.Tick(); Check(UiHost.Created == 0, "hidden gameplay does not create a pool");
                    show = true; current = null; overlay.Tick(); Check(UiHost.Created == 0, "missing source does not create a pool");
                    current = source; overlay.Tick();
                    var first = UiHost.Last; var layer = first.Layer; var tags = layer.Tags.Items;
                    Check(first.Persistent && tags.Count == 32 && tags.Count(tag => tag.Anchor.TryGetPosition(out _)) == 8, "32 pooled slots, eight active");
                    Check(tags.All(tag => tag.Offset == 0), "presence already supplies head clearance");
                    source.X = 8; source.Name = "Replacement"; overlay.Tick();
                    Check(tags[0].Anchor.TryGetPosition(out var moved) && moved.X == 8 && tags[0].Name == "Replacement", "positions and names follow source changes");
                    FrameOrder(overlay, source, layer.Tags);
                    show = false; overlay.Tick(); Check(!layer.Shown && tags.All(tag => !tag.Anchor.TryGetPosition(out _)), "gameplay departure hides all tags");
                    show = true; current = null; overlay.Tick(); Check(!layer.Shown, "source loss hides layer");
                    current = source; overlay.Tick(); Check(UiHost.Created == 1 && layer.Shown, "return reuses pool");
                    source.Count = 50; overlay.Tick(); Check(tags.Count(tag => tag.Anchor.TryGetPosition(out _)) == 32, "large roster stays bounded");
                    source.Count = 8; overlay.Tick(); Check(tags.Count(tag => tag.Anchor.TryGetPosition(out _)) == 8, "departures deactivate spare slots");
                    for (var i = 0; i < 200; i++) overlay.Tick();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 10000; i++) overlay.Tick();
                    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Check(allocated == 0, "production overlay dispatch allocates exactly zero");
                    Console.WriteLine("PresenceNametag overlay dispatch: " + allocated + " bytes; 200 warmups; 10,000 frames; 8 players.");
                    for (var i = 0; i < 200; i++) layer.Tags.Tick();
                    var renderBefore = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 10000; i++) layer.Tags.Tick();
                    var renderAllocated = GC.GetAllocatedBytesForCurrentThread() - renderBefore;
                    Check(renderAllocated == 0, "live render-time anchor reads allocate exactly zero");
                    Console.WriteLine("PresenceNametag live anchors: " + renderAllocated + " bytes; 200 warmups; 10,000 frames; 8 players.");
                    first.Lose(); Check(first.Disposals == 1, "layer loss disposes the old host once");
                    TopiaForgeNametagLayer.FailAt = 3;
                    try { overlay.Tick(); throw new Exception("partial build should fail"); }
                    catch (InvalidOperationException) { }
                    Check(UiHost.Last.Disposals == 1, "partial pool failure disposes its host");
                    TopiaForgeNametagLayer.FailAt = -1; overlay.Tick();
                    var rebuilt = UiHost.Last;
                    Check(UiHost.Created == 3 && rebuilt.Layer.Tags.Items.Count == 32 && rebuilt.Layer.Shown, "retry rebuilds a complete pool");
                    first.Lose(); Check(rebuilt.Disposals == 0, "old loss notification cannot dispose a replacement");
                    overlay.Dispose(); overlay.Dispose(); overlay.Tick();
                    Check(rebuilt.Disposals == 1 && UiHost.Created == 3, "dispose is idempotent and prevents resurrection");
                }
                private static void FrameOrder(NametagOverlay overlay, Source source, TopiaForgeNametagLayer layer)
                {
                    var core = new CoreGameplayServices();
                    var driver = new TopiaForgeNametagDriver { Layer = layer };
                    // Reverse registration prevents an unspecified same-order tie from accidentally passing.
                    var callbacks = new UnityEngine.MonoBehaviour[] { driver, core.Loop() }
                        .OrderBy(item => ((UnityEngine.DefaultExecutionOrder)Attribute.GetCustomAttribute(
                            item.GetType(), typeof(UnityEngine.DefaultExecutionOrder)))?.Order ?? 0).ToArray();
                    foreach (var pose in new[] { (9f, 2f), (100f, 2f), (100f, 1f) })
                    {
                        overlay.Tick(); // Update still sees the previous rendered head.
                        core.Interpolate = () => { source.X = pose.Item1; source.Y = pose.Item2; };
                        foreach (var callback in callbacks) callback.RunLateUpdate();
                        Check(layer.Items[0].Visible && layer.Items[0].Projected.X == pose.Item1
                            && layer.Items[0].Projected.Y == pose.Item2,
                            "same-frame movement, teleport and crouch project the interpolated head");
                    }
                    overlay.Tick(); source.Count = 0; driver.RunLateUpdate();
                    Check(!layer.Items[0].Visible, "render-time departure hides the anchor without another Update");
                    source.Count = 8;
                    Console.WriteLine("Nametag frame order: 3 current-pose projections; 1 render-time departure; production LateUpdate callbacks.");
                }
                private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
                private sealed class Source : IPresenceNametagSource
                {
                    public int Count = 8; public float X; public float Y = 2; public string Name = "Sam";
                    public int NametagCount => Count;
                    public int NametagVersion => 0;
                    public bool TryGetNametag(int slot, out PresenceNametag tag)
                    { tag = new PresenceNametag(Name, new TopiaForge.ModManager.Core.Multiplayer.NetVector3(X, Y, slot)); return true; }
                }
            }
            """;
    }
}
