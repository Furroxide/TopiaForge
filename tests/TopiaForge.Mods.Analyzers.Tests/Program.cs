using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace TopiaForge.Mods.Analyzers.Tests
{
    internal static class Program
    {
        private static readonly ModuleFixture[] SpecialistModules =
        {
            new ModuleFixture(
                "TopiaForge.Mods.Chronos",
                "ITimeControlService",
                "io.github.furroxide.topiaforge.chronos"),
            new ModuleFixture(
                "TopiaForge.Mods.CreatorContent",
                "ICreatorContentService",
                "io.github.furroxide.topiaforge.creatorcontent"),
            new ModuleFixture(
                "TopiaForge.Mods.Multiplayer",
                "IMultiplayerSession",
                "io.github.furroxide.topiaforge.multiplayer"),
            new ModuleFixture(
                "TopiaForge.Mods.Prompts",
                "IPromptOverrideRegistry",
                "io.github.furroxide.topiaforge.prompts"),
            new ModuleFixture(
                "TopiaForge.Mods.RobotKit",
                "IRobotAgentService",
                "io.github.furroxide.topiaforge.robotkit"),
            new ModuleFixture(
                "TopiaForge.Mods.Worlds",
                "IWorldGamemodeService",
                "io.github.furroxide.topiaforge.worlds"),
            new ModuleFixture(
                "TopiaForge.Mods.Ugc",
                "IUgcLiveSyncService",
                "io.github.furroxide.topiaforge.ugc.livesync"),
        };

        private static int Main()
        {
            try
            {
                ReportsUnsafeNativeUsing();
                AllowsExplicitUnsafeNativeCapability();
                ReportsUnsupportedTargetFramework();
                SkipsNonModTestProjects();
                ReportsRetiredApi();
                ReportsMissingModuleDependency();
                AcceptsDeclaredModuleDependency();
                AcceptsOptionalModuleDependency();
                RejectsManifestStringSpoofing();
                CoversEverySpecialistModuleAssembly();
                RequiresCompileAndRuntimeModuleDependencies();
                RequiresUnsafeNativeForInteropAssembly();
                RejectsLoaderOwnedUnityUiReference();
                AllowsUnityUiForInternalProviderProject();
                ReportsBlockingWaitOnTask();
                ReportsBlockingResultProperty();
                ReportsBlockingWaitMethod();
                AllowsGuardedTaskDrain();
                AllowsConditionalAccessPoll();
                AllowsDrainSplitAcrossPartialFiles();
                ReportsBlockingWaitInNonSafeProject();
                AllowsAuthorsOwnMemberNamedLikeARetiredApi();
                Console.WriteLine("All TopiaForge analyzer tests passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void ReportsUnsafeNativeUsing()
        {
            var diagnostics = Analyze("using UnityEngine; public sealed class Mod { }", Manifest());
            Assert(diagnostics.Any(item => item.Id == "TF1001"), "UnityEngine should report TF1001");
        }

        private static void AllowsExplicitUnsafeNativeCapability()
        {
            var diagnostics = Analyze(
                "using UnityEngine; public sealed class Mod { }",
                Manifest(capabilities: "\"unsafe-native\""));
            Assert(diagnostics.All(item => item.Id != "TF1001"), "unsafe-native should opt into unstable interop");
        }

        private static void ReportsRetiredApi()
        {
            var diagnostics = Analyze("public sealed class Mod : ITopiaForgeMod { }", Manifest());
            Assert(diagnostics.Any(item => item.Id == "TF1005"), "ITopiaForgeMod should report TF1005");
        }

        private static void ReportsUnsupportedTargetFramework()
        {
            var diagnostics = Analyze(
                "public sealed class Mod { }",
                Manifest(),
                new DictionaryOptionsProvider("net10.0"));
            Assert(diagnostics.Any(item => item.Id == "TF1002"), "net10.0 should report TF1002");
        }

        private static void SkipsNonModTestProjects()
        {
            var diagnostics = Analyze(
                "using UnityEngine; public sealed class Mod : ITopiaForgeMod { IRobotAgentService value; }",
                Manifest(),
                new DictionaryOptionsProvider(
                    targetFramework: "net10.0",
                    safeProject: false,
                    isTestProject: true));
            Assert(diagnostics.IsEmpty, "non-mod test projects should not receive safe-mod diagnostics");
        }

        private static void ReportsMissingModuleDependency()
        {
            var module = SpecialistModules.Single(item => item.AssemblyName.EndsWith("RobotKit", StringComparison.Ordinal));
            var diagnostics = Analyze(
                "using TopiaForge.Mods; public sealed class Mod { IRobotAgentService value = null!; }",
                Manifest(),
                additionalReferences: new[] { CreateModuleReference(module) });
            Assert(diagnostics.Any(item => item.Id == "TF1004"), "RobotKit use without dependency should report TF1004");
        }

        private static void AcceptsDeclaredModuleDependency()
        {
            var module = SpecialistModules.Single(item => item.AssemblyName.EndsWith("RobotKit", StringComparison.Ordinal));
            var diagnostics = Analyze(
                "using TopiaForge.Mods; public sealed class Mod { IRobotAgentService value = null!; }",
                Manifest(dependencies: "\"" + module.ManifestId + "\": \"[1.0.0,2.0.0)\""),
                additionalReferences: new[] { CreateModuleReference(module) });
            Assert(diagnostics.All(item => item.Id != "TF1004"), "declared RobotKit dependency should satisfy TF1004");
        }

        private static void AcceptsOptionalModuleDependency()
        {
            var module = SpecialistModules.Single(item => item.AssemblyName.EndsWith("Prompts", StringComparison.Ordinal));
            var diagnostics = Analyze(
                "using TopiaForge.Mods; public sealed class Mod { IPromptOverrideRegistry? prompts; }",
                Manifest(optionalDependencies: "\"" + module.ManifestId + "\": \"[1.0.0,2.0.0)\""),
                additionalReferences: new[] { CreateModuleReference(module) });
            Assert(diagnostics.All(item => item.Id != "TF1004"),
                "a root optionalDependencies key should satisfy optional module use");
        }

        private static void RejectsManifestStringSpoofing()
        {
            var module = SpecialistModules.Single(item => item.AssemblyName.EndsWith("RobotKit", StringComparison.Ordinal));
            const string spoofedManifest = "{"
                + "\"schemaVersion\":5,"
                + "\"name\":\"example.spoof\","
                + "\"description\":\"unsafe-native io.github.furroxide.topiaforge.robotkit\","
                + "\"capabilities\":[],"
                + "\"dependencies\":{\"example.other\":\"io.github.furroxide.topiaforge.robotkit\"},"
                + "\"x-spoof\":{"
                + "\"capabilities\":[\"unsafe-native\"],"
                + "\"dependencies\":{\"io.github.furroxide.topiaforge.robotkit\":\"*\"}}}";
            var diagnostics = Analyze(
                "using UnityEngine; using TopiaForge.Mods; public sealed class Mod { IRobotAgentService value = null!; }",
                spoofedManifest,
                additionalReferences: new[] { CreateModuleReference(module) });
            Assert(diagnostics.Any(item => item.Id == "TF1001"),
                "unsafe-native text outside the root capabilities array must not disable safe API diagnostics");
            Assert(diagnostics.Any(item => item.Id == "TF1004"),
                "dependency text outside root dependency maps must not satisfy module runtime requirements");
        }

        private static void CoversEverySpecialistModuleAssembly()
        {
            foreach (var module in SpecialistModules)
            {
                var source = "using TopiaForge.Mods; public sealed class Mod { "
                    + module.TypeName + " value = null!; }";
                var reference = CreateModuleReference(module);
                var missing = Analyze(
                    source,
                    Manifest(),
                    additionalReferences: new[] { reference });
                Assert(missing.Count(item => item.Id == "TF1004") == 1,
                    module.AssemblyName + " should require exactly one runtime dependency");

                var declared = Analyze(
                    source,
                    Manifest(dependencies: "\"" + module.ManifestId + "\":\"[1.0.0,2.0.0)\""),
                    additionalReferences: new[] { reference });
                Assert(declared.All(item => item.Id != "TF1004"),
                    module.AssemblyName + " should accept its exact root dependency key");
            }
        }

        private static void RequiresCompileAndRuntimeModuleDependencies()
        {
            var module = SpecialistModules.Single(item => item.AssemblyName.EndsWith("Worlds", StringComparison.Ordinal));
            var source = "using TopiaForge.Mods; public sealed class Mod { IWorldGamemodeService value = null!; }";
            Assert(Compile(source).Any(item => item.Id == "CS0246"),
                "module type use without the compile-time contract reference should fail compilation");

            var reference = CreateModuleReference(module);
            Assert(Analyze(
                    source,
                    Manifest(),
                    additionalReferences: new[] { reference }).Any(item => item.Id == "TF1004"),
                "a compile-time module reference without its runtime manifest dependency should report TF1004");
            Assert(Analyze(
                    source,
                    Manifest(dependencies: "\"" + module.ManifestId + "\":\">=1.0.0 <2.0.0\""),
                    additionalReferences: new[] { reference }).All(item => item.Id != "TF1004"),
                "the compile-time contract and exact runtime dependency together should pass");
        }

        private static void RequiresUnsafeNativeForInteropAssembly()
        {
            var interop = CreateAssemblyReference(
                "TopiaForge.Mods.Interop.Unity",
                "TopiaForge.Mods.Interop.Unity",
                "IUnityInteropService");
            const string source = "using TopiaForge.Mods.Interop.Unity; public sealed class Mod { IUnityInteropService value = null!; }";
            var missing = Analyze(
                source,
                Manifest(),
                additionalReferences: new[] { interop });
            Assert(missing.Any(item => item.Id == "TF1006"),
                "Interop.Unity should require the unsafe-native capability");

            const string spoofed = "{\"schemaVersion\":5,\"name\":\"example.interop\","
                + "\"description\":\"unsafe-native\",\"capabilities\":[],\"dependencies\":{},"
                + "\"x-capabilities\":[\"unsafe-native\"]}";
            var spoofedDiagnostics = Analyze(
                source,
                spoofed,
                additionalReferences: new[] { interop });
            Assert(spoofedDiagnostics.Any(item => item.Id == "TF1006"),
                "description and x-* metadata must not satisfy Interop.Unity capability requirements");

            var declared = Analyze(
                source,
                Manifest(capabilities: "\"unsafe-native\""),
                additionalReferences: new[] { interop });
            Assert(declared.All(item => item.Id != "TF1006"),
                "a root unsafe-native capability should allow the explicit unstable interop package");
        }

        private static void RejectsLoaderOwnedUnityUiReference()
        {
            var unityUi = CreateAssemblyReference(
                "TopiaForge.Mods.UnityUi",
                "TopiaForge.Mods.UnityUi",
                "TopiaForgeUi");
            const string source =
                "using TopiaForge.Mods.UnityUi; public sealed class Mod { TopiaForgeUi value = null!; }";

            var ordinary = Analyze(
                source,
                Manifest(),
                additionalReferences: new[] { unityUi });
            var diagnostic = ordinary.SingleOrDefault(item => item.Id == "TF1007");
            Assert(diagnostic != null, "UnityUi should report TF1007 in a safe project");
            Assert(diagnostic!.GetMessage().Contains("Context.Ui", StringComparison.Ordinal),
                "TF1007 should direct authors to the safe Context.Ui service");

            var unsafeNative = Analyze(
                source,
                Manifest(capabilities: "\"unsafe-native\""),
                additionalReferences: new[] { unityUi });
            Assert(unsafeNative.Count(item => item.Id == "TF1007") == 1,
                "unsafe-native must not authorize the loader-owned UnityUi implementation");
        }

        private static void AllowsUnityUiForInternalProviderProject()
        {
            var unityUi = CreateAssemblyReference(
                "TopiaForge.Mods.UnityUi",
                "TopiaForge.Mods.UnityUi",
                "TopiaForgeUi");
            var diagnostics = Analyze(
                "using TopiaForge.Mods.UnityUi; public sealed class Provider { TopiaForgeUi value = null!; }",
                Manifest(),
                new DictionaryOptionsProvider(
                    targetFramework: "netstandard2.1",
                    safeProject: false),
                new[] { unityUi });
            Assert(diagnostics.IsEmpty,
                "an explicitly internal provider project should not receive safe-mod diagnostics");
        }

        // TF1008. SDK asset and scene tasks complete from Unity main-thread callbacks, so waiting on one from
        // the game loop stops the pump that would have completed it and hangs the process permanently.
        private static void ReportsBlockingWaitOnTask()
        {
            var diagnostics = Analyze(
                "using System.Threading.Tasks; public sealed class Mod { "
                + "Task<int> Work() => Task.FromResult(1); "
                + "public int Run() => Work().GetAwaiter().GetResult(); }",
                Manifest());
            Assert(diagnostics.Any(item => item.Id == "TF1008"),
                "GetAwaiter().GetResult() on a task should report TF1008");
        }

        private static void ReportsBlockingResultProperty()
        {
            var diagnostics = Analyze(
                "using System.Threading.Tasks; public sealed class Mod { "
                + "Task<int> Work() => Task.FromResult(1); "
                + "public int Run() => Work().Result; }",
                Manifest());
            Assert(diagnostics.Any(item => item.Id == "TF1008"), "Task<T>.Result should report TF1008");
        }

        private static void ReportsBlockingWaitMethod()
        {
            var diagnostics = Analyze(
                "using System.Threading.Tasks; public sealed class Mod { "
                + "Task Work() => Task.CompletedTask; "
                + "public void Run() { Work().Wait(); } }",
                Manifest());
            Assert(diagnostics.Any(item => item.Id == "TF1008"), "Task.Wait() should report TF1008");
        }

        // The supported pattern: keep the task, poll it from the per-frame update, and read the finished
        // result without ever waiting. This must stay legal or every correct drain would be flagged.
        private static void AllowsGuardedTaskDrain()
        {
            var diagnostics = Analyze(
                "using System.Threading.Tasks; public sealed class Mod { "
                + "Task<int> pending = Task.FromResult(1); "
                + "public int Drain() { "
                + "if (!pending.IsCompleted) { return 0; } "
                + "return pending.GetAwaiter().GetResult(); } }",
                Manifest());
            Assert(diagnostics.All(item => item.Id != "TF1008"),
                "an IsCompleted-guarded drain is the supported pattern and must not report TF1008");
        }

        // `task?.IsCompleted` is a member *binding*, not a member access. Missing that spelling rejected the
        // shared creator workbench, which polls exactly this way.
        private static void AllowsConditionalAccessPoll()
        {
            var diagnostics = Analyze(
                "using System.Threading.Tasks; public sealed class Mod { "
                + "Task<int> pending; "
                + "public int Drain() { "
                + "if (pending?.IsCompleted != true) { return 0; } "
                + "return pending.GetAwaiter().GetResult(); } }",
                Manifest());
            Assert(diagnostics.All(item => item.Id != "TF1008"),
                "a null-conditional IsCompleted poll must not report TF1008");
        }

        // A drain is routinely split into a guard and a small helper that reads the finished result, and for a
        // partial class those halves live in different files. A partial class is one type, so the poll scope
        // must follow the type symbol rather than a single method or file.
        private static void AllowsDrainSplitAcrossPartialFiles()
        {
            var diagnostics = Analyze(
                "using System.Threading.Tasks; public sealed partial class Mod { "
                + "Task<int> pending; "
                + "public int Drain() { if (!pending.IsCompleted) { return 0; } return Complete(pending); } } "
                + "public sealed partial class Mod { "
                + "static int Complete(Task<int> task) => task.GetAwaiter().GetResult(); }",
                Manifest());
            Assert(diagnostics.All(item => item.Id != "TF1008"),
                "a drain helper beside its guard in a partial class must not report TF1008");
        }

        // TF1005 matched on identifier text alone, so a mod declaring its own LoadConfig/SaveConfig/GetService
        // method was rejected. Pre-V1 source is still caught: the retired member no longer exists, so the name
        // does not bind at all.
        private static void AllowsAuthorsOwnMemberNamedLikeARetiredApi()
        {
            var diagnostics = Analyze(
                "public sealed class Mod { void LoadConfig() { } public void Run() { LoadConfig(); } }",
                Manifest());
            Assert(diagnostics.All(item => item.Id != "TF1005"),
                "a mod's own method named like a retired API must not report TF1005");
        }

        // A mod that opts out of the safe profile still runs inside the game loop, so main-thread safety is
        // not gated on TopiaForgeSafeProject. The first-party Worlds provider is exactly this shape, and this
        // is the configuration in which its custom-world load hung the game.
        private static void ReportsBlockingWaitInNonSafeProject()
        {
            var diagnostics = Analyze(
                "using System.Threading.Tasks; public sealed class Provider { "
                + "Task<int> Work() => Task.FromResult(1); "
                + "public int Run() => Work().GetAwaiter().GetResult(); }",
                Manifest(),
                new DictionaryOptionsProvider(
                    targetFramework: "netstandard2.1",
                    safeProject: false));
            Assert(diagnostics.Any(item => item.Id == "TF1008"),
                "TF1008 must apply to non-safe mod projects, which still run inside the game loop");
        }

        private static ImmutableArray<Diagnostic> Analyze(
            string source,
            string manifest,
            AnalyzerConfigOptionsProvider? optionsProvider = null,
            MetadataReference[]? additionalReferences = null)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var compilation = CreateCompilation(tree, additionalReferences);
            var additionalFiles = ImmutableArray.Create<AdditionalText>(
                new StringAdditionalText("topiaforge.mod.json", manifest));
            var options = optionsProvider == null
                ? new AnalyzerOptions(additionalFiles)
                : new AnalyzerOptions(additionalFiles, optionsProvider);
            return compilation
                .WithAnalyzers(
                    ImmutableArray.Create<DiagnosticAnalyzer>(new TopiaForgeModAnalyzer()),
                    options)
                .GetAnalyzerDiagnosticsAsync()
                .GetAwaiter()
                .GetResult();
        }

        private static ImmutableArray<Diagnostic> Compile(
            string source,
            params MetadataReference[] additionalReferences)
        {
            return CreateCompilation(CSharpSyntaxTree.ParseText(source), additionalReferences)
                .GetDiagnostics();
        }

        private static CSharpCompilation CreateCompilation(
            SyntaxTree tree,
            IEnumerable<MetadataReference>? additionalReferences)
        {
            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            };
            if (additionalReferences != null)
            {
                references.AddRange(additionalReferences);
            }

            return CSharpCompilation.Create(
                "AnalyzerFixture",
                new[] { tree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        private static MetadataReference CreateModuleReference(ModuleFixture module) =>
            CreateAssemblyReference(module.AssemblyName, "TopiaForge.Mods", module.TypeName);

        private static MetadataReference CreateAssemblyReference(
            string assemblyName,
            string namespaceName,
            string typeName)
        {
            var tree = CSharpSyntaxTree.ParseText(
                "namespace " + namespaceName + " { public interface " + typeName + " { } }");
            return CSharpCompilation.Create(
                    assemblyName,
                    new[] { tree },
                    new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .ToMetadataReference();
        }

        private static string Manifest(
            string capabilities = "",
            string dependencies = "",
            string optionalDependencies = "")
        {
            return "{\"schemaVersion\":5,\"name\":\"example.mod\",\"capabilities\":["
                + capabilities + "],\"dependencies\":{" + dependencies
                + "},\"optionalDependencies\":{" + optionalDependencies + "}}";
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private sealed class StringAdditionalText : AdditionalText
        {
            private readonly SourceText text;

            public StringAdditionalText(string path, string content)
            {
                Path = path;
                text = SourceText.From(content);
            }

            public override string Path { get; }
            public override SourceText GetText(CancellationToken cancellationToken = default) => text;
        }

        private readonly struct ModuleFixture
        {
            public ModuleFixture(string assemblyName, string typeName, string manifestId)
            {
                AssemblyName = assemblyName;
                TypeName = typeName;
                ManifestId = manifestId;
            }

            public string AssemblyName { get; }
            public string TypeName { get; }
            public string ManifestId { get; }
        }

        private sealed class DictionaryOptionsProvider : AnalyzerConfigOptionsProvider
        {
            private readonly AnalyzerConfigOptions global;

            public DictionaryOptionsProvider(
                string targetFramework,
                bool safeProject = true,
                bool isTestProject = false)
            {
                global = new BuildPropertyOptions(targetFramework, safeProject, isTestProject);
            }

            public override AnalyzerConfigOptions GlobalOptions => global;
            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => EmptyOptions.Instance;
            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => EmptyOptions.Instance;
        }

        private sealed class BuildPropertyOptions : AnalyzerConfigOptions
        {
            private readonly string targetFramework;
            private readonly bool safeProject;
            private readonly bool isTestProject;

            public BuildPropertyOptions(
                string targetFramework,
                bool safeProject,
                bool isTestProject)
            {
                this.targetFramework = targetFramework;
                this.safeProject = safeProject;
                this.isTestProject = isTestProject;
            }

            public override bool TryGetValue(string key, out string value)
            {
                if (string.Equals(key, "build_property.TargetFramework", StringComparison.OrdinalIgnoreCase))
                {
                    value = targetFramework;
                    return true;
                }

                if (string.Equals(key, "build_property.TopiaForgeSafeProject", StringComparison.OrdinalIgnoreCase))
                {
                    value = safeProject.ToString();
                    return true;
                }

                if (string.Equals(key, "build_property.IsTestProject", StringComparison.OrdinalIgnoreCase))
                {
                    value = isTestProject.ToString();
                    return true;
                }

                value = string.Empty;
                return false;
            }
        }

        private sealed class EmptyOptions : AnalyzerConfigOptions
        {
            public static readonly EmptyOptions Instance = new EmptyOptions();

            public override bool TryGetValue(string key, out string value)
            {
                value = string.Empty;
                return false;
            }
        }
    }
}
