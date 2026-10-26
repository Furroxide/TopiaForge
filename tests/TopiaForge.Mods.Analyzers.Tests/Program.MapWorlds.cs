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
        private static string MapManifest(string content = "\"kind\":\"map\",\"map\":\"courtyard\",\"scene\":\"scene\"", bool dependency = true) =>
            "{\"name\":\"example.mode\",\"dependencies\":{" + (dependency ? "\"io.github.furroxide.topiaforge.worlds\":\">=0.2.0 <0.3.0\"" : "") +
            "},\"contributions\":{\"worlds\":[{\"id\":\"example.mode.world\",\"content\":{" + content + "}}]}}";
        private static void RunMapWorldTests()
        {
            ImmutableArray<Diagnostic> Check(string json) => Analyze("class MapMode {}", json);
            Assert(Check(MapManifest()).All(d => d.Id != "TF1011"), "valid published map compiles");
            foreach (var content in new[] { "\"kind\":\"map\"", "\"kind\":\"map\",\"map\":\"bad/id\"",
                "\"kind\":\"map\",\"map\":\"https://other.example/map/id\"", "\"kind\":\"map\",\"file\":\"missing.roboworld\"",
                "\"kind\":\"map\",\"file\":\"../map.json\"", "\"kind\":\"map\",\"map\":\"id\",\"scene\":\"bad/id\"",
                "\"kind\":\"map\",\"map\":\"id\",\"file\":\"map.json\"" })
                Assert(Check(MapManifest(content)).Any(d => d.Id == "TF1011"), "invalid map declaration is diagnosed");
            Assert(Check(MapManifest(dependency: false)).Any(d => d.Id == "TF1011"), "required Worlds dependency");
            Assert(Check(MapManifest().Replace("\"dependencies\"", "\"optionalDependencies\"", StringComparison.Ordinal)).Any(d => d.Id == "TF1011"), "optional Worlds does not suffice");
            Assert(Check(MapManifest().Replace("\"id\":\"example.mode.world\"", "\"transitions\":[\"additive-arena\"],\"id\":\"example.mode.world\"", StringComparison.Ordinal))
                .Any(d => d.Id == "TF1011"), "additive map import is refused");
            var link = MapManifest().Replace("\"courtyard\"", "\"https://robotopia.gg/map/courtyard/\"", StringComparison.Ordinal);
            Assert(ApplyMapFix(link).Contains("\"map\":\"courtyard\"", StringComparison.Ordinal), "link fix preserves id");
            var dependencyFix = ApplyMapFix(MapManifest(dependency: false));
            Assert(dependencyFix.Contains("io.github.furroxide.topiaforge.worlds", StringComparison.Ordinal) &&
                Check(dependencyFix).All(d => d.Id != "TF1011"), "dependency fix is valid and reparsed");
            foreach (var packed in new[] { "maps/courtyard.json", "maps/elsewhere.json", "" })
            {
                var manifest = new StringAdditionalText("topiaforge.mod.json", MapManifest("\"kind\":\"map\",\"file\":\"maps/courtyard.json\""));
                var map = new StringAdditionalText("source/courtyard.json", "{}");
                var options = new AnalyzerOptions(ImmutableArray.Create<AdditionalText>(manifest, map), new MapOptions(packed));
                var diagnostics = CreateCompilation(CSharpSyntaxTree.ParseText("class MapMode {}"), null)
                    .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TopiaForgeModAnalyzer()), options)
                    .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
                Assert(diagnostics.Any(d => d.Id == "TF1011") == (packed != "maps/courtyard.json"),
                    "file declaration must match the copied package destination");
            }
            Console.WriteLine("Map analyzer and code-fix tests passed.");
        }
        private sealed class MapOptions : AnalyzerConfigOptionsProvider
        {
            private readonly string packed;
            internal MapOptions(string packed) { this.packed = packed; }
            public override AnalyzerConfigOptions GlobalOptions => new BuildPropertyOptions("netstandard2.1", true, false);
            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => EmptyOptions.Instance;
            public override AnalyzerConfigOptions GetOptions(AdditionalText file) => file.Path == "source/courtyard.json" ? new PackedPath(packed) : EmptyOptions.Instance;
        }
        private sealed class PackedPath : AnalyzerConfigOptions
        {
            private readonly string packed;
            internal PackedPath(string packed) { this.packed = packed; }
            public override bool TryGetValue(string key, out string value)
            { value = packed; return key == "build_metadata.AdditionalFiles.TopiaForgeMapPackedPath" && packed.Length > 0; }
        }
        private static string ApplyMapFix(string json)
        {
            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("MapFixture", LanguageNames.CSharp);
            var manifestId = DocumentId.CreateNewId(project.Id);
            workspace.TryApplyChanges(workspace.CurrentSolution.AddAdditionalDocument(manifestId, "topiaforge.mod.json", SourceText.From(json)));
            var document = workspace.AddDocument(project.Id, "Mod.cs", SourceText.From("class MapMode {}"));
            var diagnostic = Analyze("class MapMode {}", json).First(d => d.Id == "TF1011");
            var actions = new List<CodeAction>();
            new MapWorldCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None)).GetAwaiter().GetResult();
            Assert(actions.Count == 1, "one unambiguous repair");
            var operation = actions[0].GetOperationsAsync(CancellationToken.None).Result.OfType<ApplyChangesOperation>().Single();
            return operation.ChangedSolution.GetAdditionalDocument(manifestId)!.GetTextAsync().Result.ToString();
        }
    }
}
