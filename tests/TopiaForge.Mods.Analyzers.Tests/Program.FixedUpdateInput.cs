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
            + "public interface IModScheduler { System.IDisposable NextFrame(System.Action action); } "
            + "public readonly struct SceneLoadEvent { } public readonly struct SceneLifecycleEvent { } "
            + "public interface ISceneLoadEventSource { System.IDisposable SubscribeSceneLoaded(System.Action<SceneLoadEvent> handler); } "
            + "public static class ModEventExtensions { "
            + "public static System.IDisposable SubscribeSceneLoaded(this IModEvents events, System.Action<SceneLoadEvent> handler) => null!; "
            + "public static System.IDisposable SubscribeSceneLifecycle(this IModEvents events, System.Action<SceneLifecycleEvent> handler) => null!; } "
            + "public interface IInputAction : System.IDisposable { "
            + "bool WasPressed { get; } bool WasReleased { get; } bool IsHeld { get; } float Value { get; } } }";

        private static void RunFixedUpdateInputTests()
        {
            ReportsAnEdgeReadInAFixedUpdateLambda();
            ReportsAnEdgeReadInAFixedUpdateMethodAcrossPartialFiles();
            ReportsAnEdgeReadInAFixedUpdateLocalFunction();
            AllowsEdgesInUpdateAndHeldInputInFixedUpdate();
            IgnoresAnotherTypesMemberNamedLikeAnEdge();
            AllowsWhatAFixedUpdateHandlerHandsTheSdkForLater();
            AllowsSceneSubscriptionsAndHandOffsKeptAside();
            ReportsALambdaTheHandlerRunsAtOnce();
            ReportsALocalFunctionTheHandlerCalls();
            ReportsAHandlerKeptInALocal();
            NamesAConditionalReadWithItsReceiverAndNeverASetter();
        }

        // What a FixedUpdate handler subscribes or schedules runs on the frame's own clock, so an edge read there is right
        // where the rule sends it, and must not be reported.
        private static void AllowsWhatAFixedUpdateHandlerHandsTheSdkForLater()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public sealed class Mod { IInputAction jump = null!; IModScheduler scheduler = null!; IModEvents events = null!; "
                + "public void Load() { "
                + "events.SubscribeFixedUpdate(_ => { scheduler.NextFrame(() => { if (jump.WasPressed) { } }); }); "
                + "events.SubscribeFixedUpdate(_ => { events.SubscribeUpdate(__ => { if (jump.WasReleased) { } }); }); "
                + "events.SubscribeFixedUpdate(OnFixedUpdate); } "
                + "void OnFixedUpdate(GameTimeSample time) { scheduler.NextFrame(() => { if (jump.WasPressed) { } }); } }");
            var found = diagnostics.Where(item => item.Id == "TF1009").ToArray();
            Assert(found.Length == 0, "an edge read in what a FixedUpdate handler schedules or subscribes for later must not report TF1009 ("
                + string.Join("; ", found.Select(item => item.GetMessage())) + ")");
        }

        // A lambda the handler runs at once, such as a test over its actions, reads in FixedUpdate all the same.
        private static void ReportsALambdaTheHandlerRunsAtOnce()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public static class Actions { public static bool Any(this IInputAction[] actions, System.Func<IInputAction, bool> test) "
                + "{ foreach (var action in actions) if (test(action)) return true; return false; } } "
                + "public sealed class Mod { public void Load(IModEvents events, IInputAction[] actions) { "
                + "events.SubscribeFixedUpdate(_ => { if (actions.Any(action => action.WasPressed)) { } }); } }");
            Assert(diagnostics.Count(item => item.Id == "TF1009") == 1, "an edge read in a lambda the FixedUpdate handler runs at once should report TF1009");
        }

        // The SDK's scene subscriptions run later too, its own extensions among them, and so do a local function and a
        // delegate kept in a local that the handler only hands on that way: an author must not be told to move any of it.
        private static void AllowsSceneSubscriptionsAndHandOffsKeptAside()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public sealed class Mod { IInputAction jump = null!; IModScheduler scheduler = null!; IModEvents events = null!; "
                + "public void Load() { "
                + "events.SubscribeFixedUpdate(_ => { events.SubscribeSceneLoaded((SceneLoadEvent scene) => { if (jump.WasPressed) { } }); }); "
                + "events.SubscribeFixedUpdate(_ => { ModEventExtensions.SubscribeSceneLifecycle(events, change => { if (jump.WasPressed) { } }); }); "
                + "events.SubscribeFixedUpdate(_ => { ((ISceneLoadEventSource)events).SubscribeSceneLoaded(scene => { if (jump.WasReleased) { } }); }); "
                + "events.SubscribeFixedUpdate(_ => { void Later() { if (jump.WasPressed) { } } scheduler.NextFrame(Later); }); "
                + "events.SubscribeFixedUpdate(_ => { System.Action later = () => { if (jump.WasReleased) { } }; scheduler.NextFrame(later); }); "
                + "events.SubscribeFixedUpdate(OnFixedUpdate); } "
                + "void OnFixedUpdate(GameTimeSample time) { System.Action later; later = () => { if (jump.WasPressed) { } }; scheduler.NextFrame(later); "
                + "void Again() { if (jump.WasReleased) { } } scheduler.NextFrame(Again); } }");
            var found = diagnostics.Where(item => item.Id == "TF1009").ToArray();
            Assert(found.Length == 0, "an edge read in a scene subscription, or in a local function or a delegate in a local that a FixedUpdate handler "
                + "only hands on for later, must not report TF1009 (" + string.Join("; ", found.Select(item => item.Location.SourceSpan.Start + ": " + item.GetMessage().Split('\'')[1])) + ")");
        }

        // A local function or a kept delegate that the handler calls runs in FixedUpdate, whichever kind of handler calls it,
        // and so does one that it both calls and hands on.
        private static void ReportsALocalFunctionTheHandlerCalls()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public sealed class Mod { IInputAction jump = null!; IModScheduler scheduler = null!; "
                + "public void Load(IModEvents events) { "
                + "events.SubscribeFixedUpdate(OnFixedUpdate); "
                + "events.SubscribeFixedUpdate(_ => { void Check() { if (jump.WasPressed) { } } Check(); scheduler.NextFrame(Check); }); "
                + "events.SubscribeFixedUpdate(_ => { System.Action check = () => { if (jump.WasReleased) { } }; check(); scheduler.NextFrame(check); }); } "
                + "void OnFixedUpdate(GameTimeSample time) { bool Pressed() => jump.WasPressed; if (Pressed()) { } } }");
            Assert(diagnostics.Count(item => item.Id == "TF1009") == 3,
                "a local function or a delegate in a local that a FixedUpdate handler calls, lambda or method, reports TF1009, though it is also handed on ("
                + diagnostics.Count(item => item.Id == "TF1009") + ")");
        }

        private static void ReportsAHandlerKeptInALocal()
        {
            var diagnostics = AnalyzeSources(
                "using TopiaForge.Mods; public sealed class Mod { IInputAction jump = null!; "
                + "public void Load(IModEvents events) { "
                + "System.Action<GameTimeSample> step = _ => { if (jump.WasPressed) { } }; "
                + "events.SubscribeFixedUpdate(step); "
                + "System.Action<GameTimeSample> named = OnFixedUpdate; "
                + "events.SubscribeFixedUpdate(named); } "
                + "void OnFixedUpdate(GameTimeSample time) { if (jump.WasReleased) { } } }");
            Assert(diagnostics.Count(item => item.Id == "TF1009") == 2,
                "a FixedUpdate handler kept in a local before it is subscribed, lambda or method, should report TF1009 ("
                + diagnostics.Count(item => item.Id == "TF1009") + ")");
        }

        private static void NamesAConditionalReadWithItsReceiverAndNeverASetter()
        {
            var conditional = AnalyzeSources(
                "using TopiaForge.Mods; public sealed class Mod { public void Load(IModEvents events, IInputAction? jump) { "
                + "events.SubscribeFixedUpdate(_ => { if (jump?.WasPressed == true) { } }); } }").Single(item => item.Id == "TF1009");
            Assert(conditional.GetMessage().StartsWith("'jump?.WasPressed' reads"), "TF1009 names a conditional read with its receiver: " + conditional.GetMessage());

            // A mod's own action that keeps a press for FixedUpdate: setting it is no read, though reading it still counts.
            const string LatchedSource =
                "using TopiaForge.Mods; public sealed class Latched : IInputAction { public bool WasPressed { get; set; } public bool WasReleased => false; "
                + "public bool IsHeld => false; public float Value => 0f; public void Dispose() { } } "
                + "public sealed class Mod { public void Load(IModEvents events, Latched jump) { "
                + "events.SubscribeFixedUpdate(_ => { if (jump.WasPressed) jump.WasPressed = false; }); } }";
            var latched = AnalyzeSources(LatchedSource).Where(item => item.Id == "TF1009").ToArray();
            Assert(latched.Length == 1 && latched[0].Location.SourceSpan.Start == LatchedSource.IndexOf("if (jump.WasPressed)") + "if (".Length,
                "setting an action's WasPressed is not reported as reading it, only the read is (" + latched.Length + ")");
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
