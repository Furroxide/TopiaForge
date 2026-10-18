using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TopiaForge.Mods.Analyzers.Tests
{
    internal static partial class Program
    {
        // The SDK's event and input contracts, as far as TF1009 reads them.
        private const string InputContracts =
            "namespace TopiaForge.Mods { "
            + "public readonly struct GameTimeSample { } "
            + "public interface IModEvents { "
            + "System.IDisposable SubscribeUpdate(System.Action<float> handler); "
            + "System.IDisposable SubscribeFixedUpdate(System.Action<GameTimeSample> handler); "
            + "System.IDisposable SubscribeLateUpdate(System.Action<GameTimeSample> handler); } "
            + "public interface IInputAction : System.IDisposable { "
            + "bool WasPressed { get; } bool WasReleased { get; } bool IsHeld { get; } float Value { get; } } }";

        private static void RunFixedUpdateInputTests()
        {
            ReportsAnEdgeReadInAFixedUpdateLambda();
            ReportsAnEdgeReadInAFixedUpdateMethodAcrossPartialFiles();
            ReportsAnEdgeReadInAFixedUpdateLocalFunction();
            AllowsEdgesInUpdateAndHeldInputInFixedUpdate();
            IgnoresAnotherTypesMemberNamedLikeAnEdge();
        }

        // TF1009. FixedUpdate runs zero or more times a frame, before the input sample, so an edge read there is missed or
        // repeated, and in multiplayer it never counts as the player's own action.
        private static void ReportsAnEdgeReadInAFixedUpdateLambda()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public sealed class Mod { "
                + "public void Load(IModEvents events, IInputAction jump) { "
                + "events.SubscribeFixedUpdate(_ => { if (jump.WasPressed) { } }); } }");
            var found = diagnostics.Where(item => item.Id == "TF1009").ToArray();
            Assert(found.Length == 1, "an edge read in a FixedUpdate lambda should report TF1009 once (" + found.Length + ")");
            Assert(found[0].Severity == DiagnosticSeverity.Warning, "TF1009 is a warning, so a mod that reads input this way still builds");
            Assert(found[0].GetMessage().Contains("'jump.WasPressed'") && found[0].GetMessage().Contains("Update or LateUpdate"),
                "TF1009 names the read and the fix: " + found[0].GetMessage());
            Assert(found[0].Descriptor.HelpLinkUri.EndsWith("TF1009"), "TF1009 links to its remediation");
        }

        private static void ReportsAnEdgeReadInAFixedUpdateMethodAcrossPartialFiles()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public sealed partial class Mod { IInputAction jump = null!; "
                + "public void Load(IModEvents events) { events.SubscribeFixedUpdate(OnFixedUpdate); } }",
                "using TopiaForge.Mods; public sealed partial class Mod { "
                + "void OnFixedUpdate(GameTimeSample time) { if (jump.WasReleased) { } } }");
            Assert(diagnostics.Count(item => item.Id == "TF1009") == 1,
                "an edge read in a FixedUpdate handler method declared in another file should report TF1009");
        }

        private static void ReportsAnEdgeReadInAFixedUpdateLocalFunction()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public sealed class Mod { "
                + "public void Load(IModEvents events, IInputAction jump) { "
                + "void Step(GameTimeSample time) { if (jump.WasPressed) { } } "
                + "events.SubscribeFixedUpdate(Step); } }");
            Assert(diagnostics.Count(item => item.Id == "TF1009") == 1, "an edge read in a FixedUpdate local function should report TF1009");
        }

        // Edges belong in Update and LateUpdate; held input and analog values in FixedUpdate are how physics reads input.
        private static void AllowsEdgesInUpdateAndHeldInputInFixedUpdate()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public sealed class Mod { bool jumping; "
                + "public void Load(IModEvents events, IInputAction jump, IInputAction thrust) { "
                + "events.SubscribeUpdate(_ => { if (jump.WasPressed) jumping = true; }); "
                + "events.SubscribeLateUpdate(_ => { if (jump.WasReleased) jumping = false; }); "
                + "events.SubscribeFixedUpdate(OnFixedUpdate); "
                + "events.SubscribeFixedUpdate(_ => { if (thrust.IsHeld && thrust.Value > 0f && jumping) { } }); } "
                + "void OnFixedUpdate(GameTimeSample time) { } "
                + "void Elsewhere(IInputAction jump) { if (jump.WasPressed) { } } }");
            Assert(diagnostics.All(item => item.Id != "TF1009"),
                "edges read in Update or LateUpdate, and held or analog input read in FixedUpdate, must not report TF1009");
        }

        private static void IgnoresAnotherTypesMemberNamedLikeAnEdge()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public sealed class Latch { public bool WasPressed { get; set; } } "
                + "public sealed class Mod { public void Load(IModEvents events, Latch latch) { "
                + "events.SubscribeFixedUpdate(_ => { if (latch.WasPressed) latch.WasPressed = false; }); } }");
            Assert(diagnostics.All(item => item.Id != "TF1009"), "a mod's own WasPressed is not an SDK input edge");
        }

        private static ImmutableArray<Diagnostic> AnalyzeSources(params string[] sources)
        {
            var trees = new[] { InputContracts }.Concat(sources).Select(source => CSharpSyntaxTree.ParseText(source));
            var compilation = CSharpCompilation.Create(
                "AnalyzerFixture",
                trees,
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            var errors = compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
            Assert(errors.Length == 0, "the fixture compiles: " + string.Join("; ", errors.Select(item => item.ToString())));
            var options = new AnalyzerOptions(ImmutableArray.Create<AdditionalText>(new StringAdditionalText("topiaforge.mod.json", Manifest())));
            return compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TopiaForgeModAnalyzer()), options)
                .GetAnalyzerDiagnosticsAsync()
                .GetAwaiter()
                .GetResult();
        }
    }
}
