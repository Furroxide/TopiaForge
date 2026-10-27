using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace TopiaForge.Mods.Analyzers.Tests
{
    internal static partial class Program
    {
        private const string ViewContracts = @"
namespace TopiaForge.Mods {
 public interface IViewControls { void Environment(string preset); void Fog(int fog); void Hide(object entity); }
 public interface IWorldView : IViewControls { }
 public interface IPhysicsService { void AddBlocker(int request); }
 public interface IGamemodeParticipantController { }
 public interface IGamemodeParticipantSession { }
}";
        private const string ViewBody = @"using TopiaForge.Mods;
class Factory {
 public void StartParticipant(IGamemodeParticipantSession session, IWorldView view, IPhysicsService physics) { Helper.Change(view, physics); }
 public void StartServer(IWorldView view) { view.Environment(""night""); }
}
static class Helper { public static void Change(IViewControls view, IPhysicsService physics) { view.Fog(6); physics.AddBlocker(1); view.Environment(""nite""); } }
class Participant : IGamemodeParticipantController { public void Update(IWorldView view) { view.Hide(null); } }
class Unrelated { public void Environment(string key) { } public void Own() { Environment(""nite""); } }";
        private static ImmutableArray<Diagnostic> ViewDiagnostics(string body, bool verified = true)
        {
            var contracts = CSharpCompilation.Create("TopiaForge.Mods.Worlds", new[] { CSharpSyntaxTree.ParseText(ViewContracts) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).ToMetadataReference();
            var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(body), new[] { contracts });
            Assert(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "world view analyzer fixture compiles");
            var analyzer = new TopiaForgeModAnalyzer(new EnvironmentPresetCatalogue("0.0.2545", verified, new[] { "day", "night", "overcast" }));
            return compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer), new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty))
                .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        }
        private static void RunWorldViewTests()
        {
            var values = ViewDiagnostics(ViewBody);
            Assert(values.Count(d => d.Id == "TF1014") == 4, "participant helpers, blockers, view calls and controller methods are covered");
            var preset = values.Single(d => d.Id == "TF1015");
            Assert(preset.Properties["presetSuggestions"]!.Split('|')[0] == "night", "nearest verified preset");
            Assert(!ViewDiagnostics(ViewBody, false).Any(d => d.Id == "TF1015"), "unqualified native catalogue is silent");
            Assert(!EnvironmentPresetCatalogue.Shipped.Verified && EnvironmentPresetCatalogue.Shipped.Keys.Length == 0, "no invented native preset capture");
            Assert(!ViewDiagnostics("using TopiaForge.Mods; class Server { public void Run(IWorldView view) { view.Fog(6); view.Environment(\"night\"); } }").Any(d => d.Id == "TF1014" || d.Id == "TF1015"), "server and valid preset accepted");
            Assert(!ViewDiagnostics("using TopiaForge.Mods; class Server { public void Run(IWorldView view, string preset) { view.Environment(preset); } }").Any(d => d.Id == "TF1015"), "dynamic preset is not guessed");
            const string local = "using TopiaForge.Mods; class P { public void Run(IGamemodeParticipantSession session, IWorldView view) { void Later() { view.Fog(1); } Later(); } }";
            Assert(ViewDiagnostics(local).Count(d => d.Id == "TF1014") == 1, "local helper follows participant root without duplicate warnings");
            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("ViewFix", LanguageNames.CSharp);
            var document = workspace.AddDocument(project.Id, "Mode.cs", SourceText.From(ViewBody));
            var actions = new List<CodeAction>();
            new EnvironmentPresetCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, preset,
                (action, _) => actions.Add(action), CancellationToken.None)).GetAwaiter().GetResult();
            Assert(actions.Count == 3, "three verified choices offered");
            var operation = actions[0].GetOperationsAsync(CancellationToken.None).GetAwaiter().GetResult().OfType<ApplyChangesOperation>().Single();
            var changed = operation.ChangedSolution.GetDocument(document.Id)!.GetTextAsync().GetAwaiter().GetResult().ToString();
            Assert(changed.Contains("view.Environment(\"night\")", StringComparison.Ordinal) && !changed.Contains("view.Environment(\"nite\")", StringComparison.Ordinal), "preset fix replaces only diagnosed expression");
            Console.WriteLine("World-view analyzers passed (participant call graph, qualified catalogue and code fix).");
        }
    }
}
