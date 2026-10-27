extern alias sdk;
extern alias worlds;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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
        private const string RealAuthoredBody = """
            using System.Threading;
            using System.Threading.Tasks;
            using TopiaForge.Mods;
            public sealed class Factory : IGamemodeFactory {
                private static Controller Create(IGamemodeSession session) => new Controller(session);
                public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken token) =>
                    Task.FromResult(OperationResult<IGamemodeController>.Success(Create(session)));
            }
            public sealed class Controller : IGamemodeController {
                public Controller(IGamemodeSession session) {
                    var map = session.World.RequireMap();
                    map.Entity("doorr");
                }
                public void Dispose() { }
            }
            """;

        private static MetadataReference[] RealAuthoredReferences() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Append(typeof(sdk::TopiaForge.Mods.IModContext).Assembly.Location)
            .Append(typeof(worlds::TopiaForge.Mods.IAuthoredMap).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => MetadataReference.CreateFromFile(p)).ToArray();

        private static ImmutableArray<Diagnostic> RealAuthoredDiagnostics(string body, string kind = "map", string floor = ">=0.2.0 <0.3.0")
        {
            var compilation = CSharpCompilation.Create("AuthorMapMode", new[] { CSharpSyntaxTree.ParseText(body) }, RealAuthoredReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var emitted = new MemoryStream();
            var result = compilation.Emit(emitted);
            Assert(result.Success, "real authored SDK example compiles: " + string.Join("; ", result.Diagnostics));
            var manifest = AuthoredManifest(kind).Insert(1, "\"supportedLoaderVersionRange\":\"" + floor + "\",\"supportedSdkVersionRange\":\"" + floor + "\",");
            var files = ImmutableArray.Create<AdditionalText>(new StringAdditionalText("topiaforge.mod.json", manifest),
                new StringAdditionalText("obj/topiaforge-map-survey.json", AuthoredSurvey));
            return compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TopiaForgeModAnalyzer()),
                new AnalyzerOptions(files, new MapOptions("anchors.json"))).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        }

        private static void RunRealAuthoredMapTests()
        {
            var ordinary = RealAuthoredDiagnostics(RealAuthoredBody);
            Assert(ordinary.Count(d => d.Id == "TF1013") == 1 && !ordinary.Any(d => d.Id == "TF1012" || d.Id == "TF1010" || d.Id == "AD0001"),
                "real SDK calls bind surveys at the stable 0.2 floor");
            var fixedBody = ApplyRealAuthoredFix(RealAuthoredBody, ordinary.Single(d => d.Id == "TF1013"));
            Assert(!RealAuthoredDiagnostics(fixedBody).Any(d => d.Id == "TF1013"), "real source code fix compiles and clears missing-id warning");
            Assert(RealAuthoredDiagnostics(RealAuthoredBody, floor: ">=0.1.0 <0.3.0").Any(d => d.Id == "TF1010"), "real authored SDK keeps stable 0.2 feature floor");
            var implicitCreation = RealAuthoredBody.Replace("new Controller(session)", "new(session)", StringComparison.Ordinal);
            Assert(RealAuthoredDiagnostics(implicitCreation, "provider").Count(d => d.Id == "TF1012") == 1,
                "target-typed controller construction still requires a map world");
            Assert(RealAuthoredDiagnostics(implicitCreation).Count(d => d.Id == "TF1013") == 1,
                "target-typed controller construction retains survey ownership");
            var named = RealAuthoredBody.Replace("map.Entity(\"doorr\")", "map.TryGetEntity(entity: out var found, entityId: \"doorr\")", StringComparison.Ordinal);
            var diagnostic = RealAuthoredDiagnostics(named).SingleOrDefault(d => d.Id == "TF1013");
            Assert(diagnostic != null, "reordered named entityId argument is checked");
            Assert(!RealAuthoredDiagnostics(ApplyRealAuthoredFix(named, diagnostic!)).Any(d => d.Id == "TF1013"),
                "named-argument code fix edits the entity id and recompiles");
            Console.WriteLine("Real authored SDK analyzer checks passed (7 checks, 2 recompiled fixes).");
        }

        private static string ApplyRealAuthoredFix(string source, Diagnostic diagnostic)
        {
            using var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("AuthorMapMode", LanguageNames.CSharp);
            var document = workspace.AddDocument(project.Id, "Mode.cs", SourceText.From(source));
            var actions = new List<CodeAction>();
            new AuthoredEntityCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), CancellationToken.None)).GetAwaiter().GetResult();
            Assert(actions.Count > 0, "real SDK missing id offers a fix");
            var operation = actions[0].GetOperationsAsync(CancellationToken.None).Result.OfType<ApplyChangesOperation>().Single();
            return operation.ChangedSolution.GetDocument(document.Id)!.GetTextAsync().Result.ToString();
        }
    }
}
