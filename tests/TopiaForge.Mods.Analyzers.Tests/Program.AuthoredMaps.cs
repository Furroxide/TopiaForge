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
        private const string AuthoredContracts = @"
namespace TopiaForge.Mods {
 public interface IAuthoredMap { object Entity(string id); bool TryGetEntity(string id, out object entity); }
 public class WorldReadiness { public IAuthoredMap Map { get; } }
 public static class WorldReadinessExtensions { public static IAuthoredMap RequireMap(this WorldReadiness world) => world.Map; }
}
";
        private static string AuthoredManifest(string kind = "map") => "{\"name\":\"example.mode\",\"dependencies\":{\"io.github.furroxide.topiaforge.worlds\":\">=0.2.0 <0.3.0\"},\"contributions\":{" +
            "\"worlds\":[{\"id\":\"workshop\",\"content\":{\"kind\":\"" + kind + "\",\"map\":\"example-map\",\"scene\":\"scene\"}}]," +
            "\"gamemodes\":[{\"id\":\"mode\",\"implementation\":{\"type\":\"Factory\"}}]," +
            "\"launchTargets\":[{\"gamemode\":\"mode\",\"world\":{\"policy\":\"fixed\",\"default\":\"workshop\"}}]}}";
        private const string AuthoredSurvey = "{\"schemaVersion\":1,\"world\":\"workshop\",\"map\":\"example-map\",\"scene\":\"scene\",\"records\":[" +
            "{\"id\":\"door\",\"name\":\"Door\",\"modelId\":\"kit/door\"},{\"id\":\"crate\",\"name\":\"Supply\",\"modelId\":\"kit/box\"}]}";
        private const string AuthoredBody = "class Factory { public object Start(TopiaForge.Mods.WorldReadiness world) => new Controller(world); } " +
            "class Controller { public Controller(TopiaForge.Mods.WorldReadiness world) { var map = TopiaForge.Mods.WorldReadinessExtensions.RequireMap(world); var other = world.Map; map.Entity(\"doorr\"); } }";
        private static ImmutableArray<Diagnostic> AuthoredDiagnostics(string manifest, string? survey = AuthoredSurvey, string body = AuthoredBody, string? anchors = null)
        {
            var files = new List<AdditionalText> { new StringAdditionalText("topiaforge.mod.json", manifest) };
            if (survey != null) files.Add(new StringAdditionalText("obj/topiaforge-map-survey.json", survey));
            if (anchors != null) files.Add(new StringAdditionalText("source/courtyard.json", anchors));
            var contracts = CSharpCompilation.Create("TopiaForge.Mods.Worlds", new[] { CSharpSyntaxTree.ParseText(AuthoredContracts) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).ToMetadataReference();
            var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(body), new[] { contracts });
            Assert(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "authored analyzer fixture compiles");
            return compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TopiaForgeModAnalyzer()),
                new AnalyzerOptions(files.ToImmutableArray(), new MapOptions("anchors.json"))).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        }
        private static void RunAuthoredMapTests()
        {
            var diagnostics = AuthoredDiagnostics(AuthoredManifest());
            Assert(diagnostics.Count(d => d.Id == "TF1013") == 1 && !diagnostics.Any(d => d.Id == "TF1012"), "map target and missing literal");
            Assert(AuthoredDiagnostics(AuthoredManifest("provider")).Count(d => d.Id == "TF1012") == 2, "factory's separate controller needs a map for both access forms");
            Assert(!AuthoredDiagnostics(AuthoredManifest(), null).Any(d => d.Id == "TF1013"), "no snapshot is silent");
            Assert(!AuthoredDiagnostics(AuthoredManifest(), AuthoredSurvey.Replace("example-map", "other-map", StringComparison.Ordinal)).Any(d => d.Id == "TF1013"), "wrong map snapshot is silent");
            Assert(!AuthoredDiagnostics(AuthoredManifest(), AuthoredSurvey.Replace("\"scene\":\"scene\"", "\"scene\":\"other\"", StringComparison.Ordinal)).Any(d => d.Id == "TF1013"), "wrong scene snapshot is silent");
            Assert(!AuthoredDiagnostics(AuthoredManifest(), body: AuthoredBody.Replace("doorr", "door", StringComparison.Ordinal)).Any(d => d.Id == "TF1013"), "known entity accepted");
            Assert(!AuthoredDiagnostics(AuthoredManifest("provider").Replace("\"fixed\"", "\"open\"", StringComparison.Ordinal)).Any(d => d.Id == "TF1012"), "open picker does not prove map exclusion");
            var separate = AuthoredManifest("provider").Replace("\"worlds\":[", "\"worlds\":[{\"id\":\"unrelated\",\"content\":{\"kind\":\"map\",\"map\":\"another\"}},", StringComparison.Ordinal);
            Assert(AuthoredDiagnostics(separate).Count(d => d.Id == "TF1012") == 2, "unrelated map declaration does not satisfy this mode");
            const string anchors = "{\"schemaVersion\":1,\"anchors\":[{\"key\":\"supply\",\"entityId\":\"lost\",\"name\":\"Supply\",\"modelId\":\"kit/box\"}]}";
            var anchor = AuthoredDiagnostics(AuthoredManifest(), anchors: anchors).Single(d => d.Properties.ContainsKey("anchorFile"));
            Assert(anchor.Properties["entitySuggestions"]!.Split('|')[0] == "crate", "anchor fix ranks name and model");
            Assert(ApplyAuthoredFix(diagnostics.Single(d => d.Id == "TF1013"), null).Contains("map.Entity(\"door\")", StringComparison.Ordinal), "source id fix offered and applied");
            Assert(ApplyAuthoredFix(anchor, anchors).Contains("\"entityId\":\"crate\"", StringComparison.Ordinal), "anchors fix edits additional document");
            Assert(!AuthoredDiagnostics(AuthoredManifest(), "{bad").Any(d => d.Id == "AD0001" || d.Id == "TF1013"), "malformed snapshot stays silent");
            Console.WriteLine("Authored map analyzer tests passed (13 contract checks, source and anchors fixes).");
        }
        private static string ApplyAuthoredFix(Diagnostic diagnostic, string? anchors)
        {
            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("AuthoredFixture", LanguageNames.CSharp);
            var id = DocumentId.CreateNewId(project.Id);
            if (anchors != null) workspace.TryApplyChanges(workspace.CurrentSolution.AddAdditionalDocument(id, "source/courtyard.json", SourceText.From(anchors)));
            var document = workspace.AddDocument(project.Id, "Mode.cs", SourceText.From(AuthoredBody));
            var actions = new List<CodeAction>();
            new AuthoredEntityCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None)).GetAwaiter().GetResult();
            Assert(actions.Count == 2, "two surveyed candidates offered without guessing");
            var operation = actions[0].GetOperationsAsync(CancellationToken.None).Result.OfType<ApplyChangesOperation>().Single();
            return anchors == null ? operation.ChangedSolution.GetDocument(document.Id)!.GetTextAsync().Result.ToString()
                : operation.ChangedSolution.GetAdditionalDocument(id)!.GetTextAsync().Result.ToString();
        }
    }
}
