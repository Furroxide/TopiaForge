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
        private const string VersionApi = """
            using System;
            namespace TopiaForge.Mods {
                [AttributeUsage(AttributeTargets.All)]
                public sealed class RequiresTopiaForgeAttribute : Attribute {
                    public RequiresTopiaForgeAttribute(string version) { }
                }
                public class Api {
                    [RequiresTopiaForge("0.2.0")] public Api() { }
                    [RequiresTopiaForge("0.2.0")] public static void Call() { }
                    [RequiresTopiaForge("0.2.0")] public static int Value { get; set; }
                    public static int Getter { [RequiresTopiaForge("0.2.0")] get => 0; }
                    public static int Setter { get => 0; [RequiresTopiaForge("0.2.0")] set { } }
                    private static int storage;
                    public static ref int Reference { [RequiresTopiaForge("0.2.0")] get => ref storage; }
                    [RequiresTopiaForge("0.2.0")] public static event Action Changed { add { } remove { } }
                    [RequiresTopiaForge("0.2.0")] public const int Flag = 1;
                    [RequiresTopiaForge("0.2.0")] public static int Field;
                    public static void Old() { }
                }
                [RequiresTopiaForge("0.2.0")] public class NewApi { public static void Call() { } }
                public class NewAttribute : Attribute { [RequiresTopiaForge("0.2.0")] public NewAttribute(int value) { } }
                public class Box<T> { }
            }
            """;

        private static MetadataReference VersionApiReference()
        {
            var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(VersionApi), null)
                .WithAssemblyName("TopiaForge.Mods.Abstractions");
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert(emitted.Success, "version fixture compiles: " + string.Join("; ", emitted.Diagnostics));
            return MetadataReference.CreateFromImage(stream.ToArray());
        }

        private static string VersionManifest(string loader, string? sdk = null) =>
            "{\"name\":\"example.mod\",\"supportedLoaderVersionRange\":\"" + loader
            + "\",\"supportedSdkVersionRange\":\"" + (sdk ?? loader) + "\",\"x-example.note\":\"keep me\"}";

        private static void RunVersionGateTests()
        {
            BlockerVersionGate();
            WorldWidgetVersionGate();
            var reference = VersionApiReference();
            var cases = new[] {
                "void Run() { Api.Call(); }",
                "void Helper() { Api.Call(); } void Run() { Helper(); }",
                "int Run() => Api.Value;",
                "void Run() { Api.Value = 1; }",
                "int Run() => Api.Getter;",
                "void Run() { Api.Setter = 1; }",
                "void Run() { Api.Setter += 1; }",
                "void Run() { (Api.Setter, _) = (1, 2); }",
                "void Run() { Api.Reference = 1; }",
                "void Run() { Api.Changed += () => {}; }",
                "void Run() { Api.Changed -= Handler; } void Handler() {}",
                "int Run() => Api.Flag;",
                "object Run() => new Api();",
                "Api Run() => new();",
                "int Run() { return Api.Field; }",
                "Box<NewApi> Run() => null;",
                "string Run() => nameof(Api.Call);",
                "System.Action Run() => Api.Call;",
                "void Run() { NewApi.Call(); }",
                "[New(Api.Flag)] void Run() {}",
            };
            foreach (var body in cases)
            {
                var source = "using TopiaForge.Mods; class Mod { " + body + " }";
                Assert(!Compile(source, reference).Any(d => d.Severity == DiagnosticSeverity.Error), "fixture compiles: " + body);
                var diagnostics = Analyze(source, VersionManifest(">=0.1.0-rc.1 <1.0.0"), additionalReferences: new[] { reference });
                var gates = diagnostics.Where(d => d.Id == "TF1010").ToArray();
                Assert(gates.Length > 0, "TF1010 checks metadata use: " + body);
                if (body.StartsWith("[New(", StringComparison.Ordinal))
                    Assert(gates.Any(d => d.GetMessage().Contains("NewAttribute", StringComparison.Ordinal))
                        && gates.Any(d => d.GetMessage().Contains("Flag", StringComparison.Ordinal)),
                        "attribute constructor and constant argument are checked independently");
                Assert(gates.All(d => d.Severity == DiagnosticSeverity.Error
                    && d.GetMessage().Contains("0.2.0 or newer", StringComparison.Ordinal)
                    && d.GetMessage().Contains(">=0.1.0-rc.1 <1.0.0", StringComparison.Ordinal)
                    && d.GetMessage().Contains("Raise supportedLoaderVersionRange", StringComparison.Ordinal)), "actionable diagnostic");
                Assert(!Analyze(source, VersionManifest(">=0.2.0 <1.0.0"), additionalReferences: new[] { reference })
                    .Any(d => d.Id == "TF1010"), "sufficient floor: " + body);
            }

            foreach (var range in new[] { "*", "<1.0.0", "0.1.*", "0.2.0-rc.1", ">=0.2.0-rc.1 <1.0.0", ">=0.1.0 <0.3.0", "invalid" })
                Assert(VersionDiagnostics(range, reference).Length == 1, "too-low or unknown floor: " + range);
            foreach (var range in new[] { "0.2.0", "=0.2.0", "0.2.*", ">0.2.0", ">=0.3.0 <1.0.0", ">=0.2.0+build <1.0.0", "<1.0.0 >=0.2.0" })
                Assert(VersionDiagnostics(range, reference).Length == 0, "sufficient floor shape: " + range);
            Assert(!Analyze("using TopiaForge.Mods; class Mod { int Run() => Api.Setter; }", VersionManifest("*"),
                additionalReferences: new[] { reference }).Any(d => d.Id == "TF1010"), "a newer setter does not gate an old getter");
            Assert(!Analyze("using TopiaForge.Mods; class Mod { int Run() => -Api.Setter; }", VersionManifest("*"),
                additionalReferences: new[] { reference }).Any(d => d.Id == "TF1010"), "a unary read does not invoke the setter");
            RunVersionFixTests(reference);
            Console.WriteLine("SDK version gates passed (20 metadata uses, 14 range cases, 32 code-fix cases, 2 accessor read checks).");
        }

        private static Diagnostic[] VersionDiagnostics(string range, MetadataReference reference) =>
            Analyze("using TopiaForge.Mods; class Mod { void Run() { Api.Call(); } }", VersionManifest(range),
                additionalReferences: new[] { reference }).Where(d => d.Id == "TF1010").ToArray();

        private static void RunVersionFixTests(MetadataReference reference)
        {
            foreach (var range in new[] { ">=0.1.0 <1.0.0", ">=0.1.0-rc.1 <0.3.0", " >= 0.1.0\t< 1.0.0 " })
            {
                // JSON escapes the tab; the replacement retains the decoded range's layout.
                var json = VersionManifest(range).Replace("\t", "\\t", StringComparison.Ordinal);
                var actions = VersionFixes(json, reference, out var id);
                Assert(actions.Count == 1, "simple bounded range offers one fix: " + range);
                var applied = actions[0].GetOperationsAsync(CancellationToken.None).GetAwaiter().GetResult()
                    .OfType<ApplyChangesOperation>().Single();
                var fixedJson = applied.ChangedSolution.GetAdditionalDocument(id)!.GetTextAsync().Result.ToString();
                Assert(fixedJson == json.Replace(range.Contains("-rc.1", StringComparison.Ordinal) ? "0.1.0-rc.1" : "0.1.0", "0.2.0", StringComparison.Ordinal),
                    "both floors change and other bytes stay: " + fixedJson);
                Assert(!Analyze("using TopiaForge.Mods; class Mod { void Run() { Api.Call(); } }", fixedJson,
                    additionalReferences: new[] { reference }).Any(d => d.Id == "TF1010"), "fix clears TF1010");
            }

            foreach (var range in new[] { "*", "0.1.0", "=0.1.0", "0.1.*", ">=0.1.0", ">0.1.0 <1.0.0", ">=0.1.0 <=1.0.0",
                "<1.0.0 >=0.1.0", ">=0.1.0 <0.2.0", ">=0.1.0 <0.2.0-rc.1", ">=0.1.0 <1.0.0 >=0.1.1", "^0.1.0",
                ">=0.1.0 <1.0.0 || >=2.0.0" })
            {
                Assert(VersionFixes(VersionManifest(range), reference, out _).Count == 0, "no fix for loader range: " + range);
                Assert(VersionFixes(VersionManifest(">=0.1.0 <1.0.0", range), reference, out _).Count == 0, "no partial fix for SDK range: " + range);
            }
            Assert(VersionFixes("{\"supportedLoaderVersionRange\":\">=0.1.0 <1.0.0\"}", reference, out _).Count == 0, "no missing SDK edit");
            Assert(VersionFixes(VersionManifest(">=0.1.0 <1.0.0").Replace("{", "{\"supportedSdkVersionRange\":\"*\",", StringComparison.Ordinal), reference, out _).Count == 0,
                "no duplicate-key edit");
            var stricter = VersionManifest(">=0.1.0 <1.0.0", ">=0.3.0 <1.0.0");
            var fix = VersionFixes(stricter, reference, out var stricterId).Single();
            var solution = fix.GetOperationsAsync(CancellationToken.None).Result.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            Assert(solution.GetAdditionalDocument(stricterId)!.GetTextAsync().Result.ToString()
                == stricter.Replace("0.1.0", "0.2.0", StringComparison.Ordinal), "a stricter SDK floor is preserved");
        }

        private static List<CodeAction> VersionFixes(string json, MetadataReference reference, out DocumentId manifestId)
        {
            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
                "VersionFixture", "VersionFixture", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location), reference }));
            manifestId = DocumentId.CreateNewId(project.Id);
            workspace.TryApplyChanges(workspace.CurrentSolution.AddAdditionalDocument(manifestId, "topiaforge.mod.json", SourceText.From(json)));
            var document = workspace.AddDocument(project.Id, "Mod.cs", SourceText.From("using TopiaForge.Mods; class Mod { void Run() { Api.Call(); } }"));
            var compilation = document.Project.GetCompilationAsync().Result!;
            var diagnostics = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TopiaForgeModAnalyzer()),
                new AnalyzerOptions(ImmutableArray.Create<AdditionalText>(new StringAdditionalText("topiaforge.mod.json", json))))
                .GetAnalyzerDiagnosticsAsync().Result.Where(d => d.Id == "TF1010").ToArray();
            var actions = new List<CodeAction>();
            foreach (var diagnostic in diagnostics)
                new SdkVersionCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
                    (action, _) => actions.Add(action), CancellationToken.None)).GetAwaiter().GetResult();
            return actions;
        }
    }
}
