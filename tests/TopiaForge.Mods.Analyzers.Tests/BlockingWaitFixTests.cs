using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace TopiaForge.Mods.Analyzers.Tests
{
    /// <summary>
    /// TF1008 on a multiplayer command: the message names the generated method that answers through a callback, and
    /// the code fix rewrites the wait into a call to it.
    /// </summary>
    internal static partial class Program
    {
        // The shapes the generator emits for a command handler named Ping, reduced to what the analyzer reads.
        private const string GeneratedCommandStubs = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TopiaForge.Mods
            {
                public sealed class MultiplayerCommandConfirmation<T> where T : class
                {
                    public bool Succeeded { get; set; }
                }

                public interface IMultiplayerSession
                {
                    Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
                        where TRequest : class
                        where TResponse : class;
                }
            }

            public sealed class PingRequest { }

            public sealed class PingReply { }

            public partial class PingMod
            {
                public Task<TopiaForge.Mods.MultiplayerCommandConfirmation<PingReply>> SubmitPingAsync(PingRequest request, CancellationToken cancellationToken = default) =>
                    Task.FromResult(new TopiaForge.Mods.MultiplayerCommandConfirmation<PingReply>());

                public void SubmitPing(PingRequest request, Action<TopiaForge.Mods.MultiplayerCommandConfirmation<PingReply>> answered = null, CancellationToken cancellationToken = default)
                {
                }

                public void Log(string message)
                {
                }
            }
            """;

        private static void RunBlockingWaitFixTests()
        {
            NamesTheAnsweredSubmitForAGeneratedCommand();
            NamesTheGeneratedMethodForARawSessionSubmit();
            KeepsThePollRemedyForOtherTasks();
            FixMovesTheFollowingStatementsIntoTheAnswer();
            FixReplacesAWaitStatement();
            FixReplacesADiscardedResult();
            FixKeepsANamedRequestAndToken();
            NoFixWhenTheFollowingCodeLeavesTheMethod();
            NoFixWhenTheFollowingCodeAwaits();
            NoFixInsideAStruct();
            NoFixWhereTheCallbackWouldOutliveAScope();
            NoFixForOtherTasks();
        }

        private static void NamesTheAnsweredSubmitForAGeneratedCommand()
        {
            var diagnostic = SingleBlockingWait(PingSource("var answer = SubmitPingAsync(new PingRequest()).Result; Log(answer.Succeeded.ToString());"));
            var message = diagnostic.GetMessage();
            Assert(message.Contains("call 'SubmitPing' instead", StringComparison.Ordinal)
                   && message.Contains("never once the mod has stopped", StringComparison.Ordinal)
                   && !message.Contains("PendingOperation", StringComparison.Ordinal),
                "TF1008 on a generated command names its answered twin: " + message);
            Assert(diagnostic.Properties.TryGetValue("TopiaForge.AnsweredSubmit", out var answered) && answered == "SubmitPing",
                "TF1008 tells the code fix which method to call");
        }

        private static void NamesTheGeneratedMethodForARawSessionSubmit()
        {
            var diagnostic = SingleBlockingWait(GeneratedCommandStubs + """

                public sealed class RawMod
                {
                    public TopiaForge.Mods.IMultiplayerSession Session;

                    public void Run() { var answer = Session.SubmitAsync<PingRequest, PingReply>(new PingRequest()).GetAwaiter().GetResult(); }
                }
                """);
            Assert(diagnostic.GetMessage().Contains("generated Submit method", StringComparison.Ordinal)
                   && !diagnostic.Properties.ContainsKey("TopiaForge.AnsweredSubmit"),
                "a raw session submit is pointed at the generated method, with nothing for the fix to call: " + diagnostic.GetMessage());
        }

        private static void KeepsThePollRemedyForOtherTasks()
        {
            var diagnostic = SingleBlockingWait(
                "using System.Threading.Tasks; public sealed class Mod { "
                + "Task<int> Work() => Task.FromResult(1); "
                + "public int Run() => Work().Result; }");
            Assert(diagnostic.GetMessage().Contains("drive the work with PendingOperation<T>", StringComparison.Ordinal),
                "other SDK tasks keep the poll remedy: " + diagnostic.GetMessage());
        }

        private static void FixMovesTheFollowingStatementsIntoTheAnswer()
        {
            var fixedSource = ApplyBlockingWaitFix(PingSource("""
                Log("pressed");
                var answer = SubmitPingAsync(new PingRequest()).Result;
                if (!answer.Succeeded)
                {
                    Log("refused");
                }

                Log("done");
                """));
            AssertFixed(fixedSource, "SubmitPing(new PingRequest(), answer =>", "the wait becomes the answered call");
            Assert(fixedSource.IndexOf("Log(\"pressed\")", StringComparison.Ordinal) < fixedSource.IndexOf("SubmitPing(new PingRequest()", StringComparison.Ordinal)
                   && fixedSource.IndexOf("Log(\"done\")", StringComparison.Ordinal) > fixedSource.IndexOf("answer =>", StringComparison.Ordinal),
                "statements before the wait stay; statements after it move into the answer:\n" + fixedSource);
        }

        private static void FixReplacesAWaitStatement()
        {
            var fixedSource = ApplyBlockingWaitFix(PingSource("SubmitPingAsync(new PingRequest()).GetAwaiter().GetResult();"));
            AssertFixed(fixedSource, "SubmitPing(new PingRequest());", "a waited statement becomes a submit without an answer");

            fixedSource = ApplyBlockingWaitFix(PingSource("SubmitPingAsync(new PingRequest()).Wait();"));
            AssertFixed(fixedSource, "SubmitPing(new PingRequest());", "Wait() becomes a submit without an answer");
        }

        private static void FixReplacesADiscardedResult()
        {
            var fixedSource = ApplyBlockingWaitFix(PingSource("_ = SubmitPingAsync(new PingRequest()).Result;"));
            AssertFixed(fixedSource, "SubmitPing(new PingRequest());", "a discarded result becomes a submit without an answer");
        }

        private static void FixKeepsANamedRequestAndToken()
        {
            var fixedSource = ApplyBlockingWaitFix(PingSource(
                "var stop = new CancellationTokenSource(); "
                + "var answer = SubmitPingAsync(cancellationToken: stop.Token, request: new PingRequest()).Result; Log(\"x\");"));
            AssertFixed(fixedSource, "cancellationToken: stop.Token", "the caller's token still withdraws the answer");
            Assert(fixedSource.Contains("SubmitPing(new PingRequest(), answer =>", StringComparison.Ordinal),
                "the request leads and the answer follows:\n" + fixedSource);
        }

        private static void NoFixWhenTheFollowingCodeLeavesTheMethod()
        {
            AssertNoFix(PingSource("var answer = SubmitPingAsync(new PingRequest()).Result; if (!answer.Succeeded) { return; } Log(\"x\");"),
                "a return after the wait would return from the callback instead");
            AssertNoFix(
                PingSource("for (var i = 0; i < 2; i++) { var answer = SubmitPingAsync(new PingRequest()).Result; if (!answer.Succeeded) break; }"),
                "a break after the wait would leave no loop");
        }

        private static void NoFixWhenTheFollowingCodeAwaits()
        {
            AssertNoFix(
                GeneratedCommandStubs + """

                public partial class PingMod
                {
                    public async Task Run()
                    {
                        var answer = SubmitPingAsync(new PingRequest()).Result;
                        await Task.Yield();
                        Log(answer.Succeeded.ToString());
                    }
                }
                """,
                "an await cannot move into a synchronous callback");
        }

        private static void NoFixInsideAStruct()
        {
            AssertNoFix(
                GeneratedCommandStubs + """

                public struct Presser
                {
                    public PingMod Mod;
                    public int Count;

                    public void Run()
                    {
                        var answer = Mod.SubmitPingAsync(new PingRequest()).Result;
                        Count++;
                    }
                }
                """,
                "a struct's members cannot be captured by the callback");
        }

        private static void NoFixWhereTheCallbackWouldOutliveAScope()
        {
            AssertNoFix(
                PingSource("try { var answer = SubmitPingAsync(new PingRequest()).Result; Log(\"x\"); } catch (System.Exception) { Log(\"caught\"); }"),
                "a try would no longer catch what the callback throws");
            AssertNoFix(
                PingSource("using var stop = new CancellationTokenSource(); var answer = SubmitPingAsync(new PingRequest()).Result; Log(stop.Token.ToString());"),
                "a using declaration would end before the callback runs");
            AssertNoFix(
                PingSource("lock (this) { var answer = SubmitPingAsync(new PingRequest()).Result; Log(\"x\"); }"),
                "a lock would be released before the callback runs");
        }

        private static void NoFixForOtherTasks() =>
            AssertNoFix(
                "using System.Threading.Tasks; public sealed class Mod { "
                + "Task<int> Work() => Task.FromResult(1); "
                + "public void Run() { var value = Work().Result; System.Console.WriteLine(value); } }",
                "only generated commands have an answered twin to call");

        private static string PingSource(string body) => GeneratedCommandStubs + """

            public partial class PingMod
            {
                public void Run()
                {
            """ + body + """

                }
            }
            """;

        private static Diagnostic SingleBlockingWait(string source)
        {
            var waits = Analyze(source, Manifest()).Where(item => item.Id == "TF1008").ToArray();
            Assert(waits.Length == 1, "expected one TF1008, got " + waits.Length);
            return waits[0];
        }

        private static void AssertFixed(string fixedSource, string expected, string message)
        {
            Assert(fixedSource.Contains(expected, StringComparison.Ordinal), message + ":\n" + fixedSource);
            var tree = CSharpSyntaxTree.ParseText(fixedSource);
            var errors = CreateCompilation(tree, null).GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
            Assert(errors.Length == 0, "the fixed code compiles: " + string.Join("; ", errors.Select(item => item.ToString())) + "\n" + fixedSource);
            Assert(Analyze(fixedSource, Manifest()).All(item => item.Id != "TF1008"), "the fixed code waits on nothing:\n" + fixedSource);
        }

        private static void AssertNoFix(string source, string message)
        {
            var actions = BlockingWaitFixes(source, out _);
            Assert(actions.Count == 0, message + ", so no fix is offered");
        }

        private static string ApplyBlockingWaitFix(string source)
        {
            var actions = BlockingWaitFixes(source, out var document);
            Assert(actions.Count == 1, "expected one TF1008 fix, got " + actions.Count);
            var operations = actions[0].GetOperationsAsync(CancellationToken.None).GetAwaiter().GetResult();
            var applied = operations.OfType<ApplyChangesOperation>().Single();
            return applied.ChangedSolution.GetDocument(document.Id)!.GetTextAsync().GetAwaiter().GetResult().ToString();
        }

        private static List<CodeAction> BlockingWaitFixes(string source, out Document document)
        {
            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "AnalyzerFixture",
                "AnalyzerFixture",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }));
            document = workspace.AddDocument(project.Id, "Mod.cs", SourceText.From(source));
            var compilation = document.Project.GetCompilationAsync().GetAwaiter().GetResult()!;
            var options = new AnalyzerOptions(ImmutableArray.Create<AdditionalText>(
                new StringAdditionalText("topiaforge.mod.json", Manifest())));
            var diagnostics = compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TopiaForgeModAnalyzer()), options)
                .GetAnalyzerDiagnosticsAsync()
                .GetAwaiter()
                .GetResult()
                .Where(item => item.Id == "TF1008")
                .ToArray();
            var actions = new List<CodeAction>();
            var provider = new BlockingWaitCodeFixProvider();
            foreach (var diagnostic in diagnostics)
            {
                var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
                provider.RegisterCodeFixesAsync(context).GetAwaiter().GetResult();
            }

            return actions;
        }
    }
}
