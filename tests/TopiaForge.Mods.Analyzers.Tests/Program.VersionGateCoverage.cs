using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TopiaForge.Mods.Analyzers.Tests
{
    internal static partial class Program
    {
        // Mods build with the .NET 10 SDK's C# 14 (Roslyn 4.14 calls it Preview) and with documentation on.
        private static readonly CSharpParseOptions ModParseOptions =
            new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose);

        private const string CoverageApi = """
            using System;
            namespace TopiaForge.Mods {
                [AttributeUsage(AttributeTargets.All)]
                public sealed class RequiresTopiaForgeAttribute : Attribute {
                    public RequiresTopiaForgeAttribute(string version) { }
                }
                public class Holder { public int Value; }
                public class Shelf {
                    public static Shelf Shared => null;
                    public int Slot { get => 0; [RequiresTopiaForge("0.2.0")] set { } }
                    [RequiresTopiaForge("0.2.0")] public int this[int index] { get => 0; set { } }
                    public int this[string key] { get => 0; [RequiresTopiaForge("0.2.0")] set { } }
                    public static Holder Box { get => null; [RequiresTopiaForge("0.2.0")] set { } }
                    public static Holder Peek { [RequiresTopiaForge("0.2.0")] get => null; set { } }
                    public Holder Inner { get => null; [RequiresTopiaForge("0.2.0")] set { } }
                }
                public class ModBase {
                    [RequiresTopiaForge("0.2.0")] protected virtual void OnNew() { }
                    [RequiresTopiaForge("0.2.0")] public virtual int NewValue => 0;
                    [RequiresTopiaForge("0.2.0")] public virtual event Action NewEvent { add { } remove { } }
                    public virtual int LaterSetter { get => 0; [RequiresTopiaForge("0.2.0")] set { } }
                    public virtual int LaterGetter { [RequiresTopiaForge("0.2.0")] get => 0; set { } }
                    protected virtual void OnOld() { }
                }
                public interface IHandler { void Existing(); [RequiresTopiaForge("0.2.0")] void Added() { } }
                public class OldAttribute : Attribute { public int Named { get => 0; [RequiresTopiaForge("0.2.0")] set { } } }
                [RequiresTopiaForge("0.2.0")] public class NewApi { public static void Call() { } }
            }
            """;

        private static void RunVersionGateCoverageTests()
        {
            var reference = CoverageApiReference();
            var gated = new (string Source, string Member)[]
            {
                // C# 14 null-conditional assignment calls the setter when the target is not null.
                ("using TopiaForge.Mods; class Mod { void Run() { Shelf.Shared?.Slot = 1; } }", "Shelf.Slot"),
                ("using TopiaForge.Mods; class Mod { void Run() { Shelf.Shared?.Slot += 1; } }", "Shelf.Slot"),
                ("using TopiaForge.Mods; class Mod { int Run(Shelf shelf) => shelf?[0] ?? 0; }", "Shelf.this[int]"),
                ("using TopiaForge.Mods; class Mod { void Run(Shelf shelf) { shelf?[0] = 1; } }", "Shelf.this[int]"),
                ("using TopiaForge.Mods; class Mod { int Run(Shelf shelf) => shelf[0]; }", "Shelf.this[int]"),
                ("using TopiaForge.Mods; class Mod { object Run() => new Shelf { [0] = 1 }; }", "Shelf.this[int]"),
                ("using TopiaForge.Mods; class Mod { object Run() => new Shelf { [\"key\"] = 1 }; }", "Shelf.this[string]"),
                ("using TopiaForge.Mods; class Mod { void Run(Shelf shelf) { shelf?[\"key\"] = 1; } }", "Shelf.this[string]"),
                // An unqualified property at the start of a member access is read, whatever is assigned after it.
                ("using static TopiaForge.Mods.Shelf; class Mod { void Run() { Peek.Value = 1; } }", "Shelf.Peek"),
                ("using TopiaForge.Mods; class Mod { object Run() => new Shelf { Inner = new Holder() }; }", "Shelf.Inner"),
                ("using TopiaForge.Mods; [Old(Named = 1)] class Mod { }", "OldAttribute.Named"),
                // An override or explicit implementation only runs on a loader that has the member.
                ("using TopiaForge.Mods; class Mod : ModBase { protected override void OnNew() { } }", "ModBase.OnNew()"),
                ("using TopiaForge.Mods; class Mod : ModBase { public override int NewValue => 1; }", "ModBase.NewValue"),
                ("using TopiaForge.Mods; class Mod : ModBase { public override event System.Action NewEvent { add { } remove { } } }", "ModBase.NewEvent"),
                ("using TopiaForge.Mods; class Mod : IHandler { public void Existing() { } void IHandler.Added() { } }", "IHandler.Added()"),
                ("using TopiaForge.Mods; class Mod : ModBase { public override int LaterSetter { set { } } }", "ModBase.LaterSetter"),
                ("using TopiaForge.Mods; class Mod : ModBase { public override int LaterGetter => 1; }", "ModBase.LaterGetter"),
            };
            foreach (var (source, member) in gated)
            {
                Assert(!CompileForMod(source, reference).Any(d => d.Severity == DiagnosticSeverity.Error), "coverage fixture compiles: " + source);
                var gates = GatesForMod(source, VersionManifest(">=0.1.0-rc.1 <1.0.0"), reference);
                Assert(gates.Length > 0 && gates.All(d => d.GetMessage().Contains("'" + member + "' needs TopiaForge 0.2.0", StringComparison.Ordinal)),
                    "TF1010 names the newer member: " + source + " => " + string.Join(" | ", gates.Select(d => d.GetMessage())));
                Assert(GatesForMod(source, VersionManifest(">=0.2.0 <1.0.0"), reference).Length == 0, "sufficient floor: " + source);
            }

            var ungated = new[]
            {
                // Reading Box to assign a field of the object it returns never calls Box's newer setter.
                "using static TopiaForge.Mods.Shelf; class Mod { void Run() { Box.Value = 1; } }",
                "using TopiaForge.Mods; class Mod : Shelf { void Run() { Inner.Value = 1; } }",
                "using TopiaForge.Mods; class Mod { int Run(Shelf shelf) => shelf?[\"key\"] ?? 0; }",
                // A nested object initializer reads Inner and fills the object it returns.
                "using TopiaForge.Mods; class Mod { object Run() => new Shelf { Inner = { Value = 1 } }; }",
                "using TopiaForge.Mods; class Mod : ModBase { protected override void OnOld() { } }",
                "using TopiaForge.Mods; class Mod : ModBase { public override int LaterSetter => 1; }",
                "using TopiaForge.Mods; class Mod : ModBase { public override int LaterGetter { set { } } }",
                // Documentation is not code: a cref to a newer API runs nothing.
                "using TopiaForge.Mods; /// <summary>Prefer <see cref=\"NewApi\"/> on newer loaders.</summary>\npublic class Mod { }",
            };
            foreach (var source in ungated)
            {
                Assert(!CompileForMod(source, reference).Any(d => d.Severity == DiagnosticSeverity.Error), "coverage fixture compiles: " + source);
                var gates = GatesForMod(source, VersionManifest(">=0.1.0-rc.1 <1.0.0"), reference);
                Assert(gates.Length == 0, "no TF1010 for an older or absent use: " + source + " => " + string.Join(" | ", gates.Select(d => d.GetMessage())));
            }

            // A library without its own manifest, such as a service's contracts project, has no floor to raise.
            Assert(GatesForMod("using TopiaForge.Mods; class Mod { void Run() { NewApi.Call(); } }", null, reference).Length == 0,
                "a project without topiaforge.mod.json is not given an error it cannot fix");
            Assert(GatesForMod("using TopiaForge.Mods; class Mod { void Run() { NewApi.Call(); } }", "{", reference).Length > 0,
                "a malformed manifest still reports TF1010");
            var unreadable = CreateCompilation(CSharpSyntaxTree.ParseText(
                    "using TopiaForge.Mods; class Mod { void Run() { NewApi.Call(); } }", ModParseOptions), new[] { reference })
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TopiaForgeModAnalyzer()),
                    new AnalyzerOptions(ImmutableArray.Create<AdditionalText>(new UnreadableVersionManifest())))
                .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
            Assert(unreadable.Any(d => d.Id == "TF1010"), "an unreadable AdditionalFile still reports TF1010");

            // Raising the floor alone cannot admit the API when the range's ceiling is below it, as in every 0.1 mod.
            var call = "using TopiaForge.Mods; class Mod { void Run() { NewApi.Call(); } }";
            var ceiling = GatesForMod(call, VersionManifest(">=0.1.0-rc.1 <0.2.0"), reference);
            Assert(ceiling.Length > 0 && ceiling.All(d => d.GetMessage().EndsWith(
                    "admits >=0.1.0-rc.1 <0.2.0. Raise supportedLoaderVersionRange and supportedSdkVersionRange to admit 0.2.0, for example \">=0.2.0 <0.3.0\".",
                    StringComparison.Ordinal)),
                "a ceiling below the API says which bounds to write: " + string.Join(" | ", ceiling.Select(d => d.GetMessage())));
            var open = GatesForMod(call, VersionManifest(">=0.1.0-rc.1 <1.0.0"), reference);
            Assert(open.Length > 0 && open.All(d => d.GetMessage().EndsWith("admits >=0.1.0-rc.1 <1.0.0. Raise supportedLoaderVersionRange.", StringComparison.Ordinal)),
                "a ceiling above the API keeps the short advice");
            Console.WriteLine("SDK version gate coverage passed (" + gated.Length + " gated uses, " + ungated.Length + " ungated uses, 5 manifest cases).");
        }

        private static MetadataReference CoverageApiReference()
        {
            var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(CoverageApi, ModParseOptions), null)
                .WithAssemblyName("TopiaForge.Mods.Abstractions");
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert(emitted.Success, "coverage fixture compiles: " + string.Join("; ", emitted.Diagnostics));
            return MetadataReference.CreateFromImage(stream.ToArray());
        }

        private sealed class UnreadableVersionManifest : AdditionalText
        {
            public override string Path => "topiaforge.mod.json";
            public override Microsoft.CodeAnalysis.Text.SourceText? GetText(System.Threading.CancellationToken cancellationToken = default) => null;
        }

        private static ImmutableArray<Diagnostic> CompileForMod(string source, MetadataReference reference) =>
            CreateCompilation(CSharpSyntaxTree.ParseText(source, ModParseOptions), new[] { reference }).GetDiagnostics();

        private static Diagnostic[] GatesForMod(string source, string? manifest, MetadataReference reference)
        {
            var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(source, ModParseOptions), new[] { reference });
            var files = manifest == null
                ? ImmutableArray<AdditionalText>.Empty
                : ImmutableArray.Create<AdditionalText>(new StringAdditionalText("topiaforge.mod.json", manifest));
            return compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TopiaForgeModAnalyzer()), new AnalyzerOptions(files))
                .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
                .Where(d => d.Id == "TF1010").ToArray();
        }
    }
}
