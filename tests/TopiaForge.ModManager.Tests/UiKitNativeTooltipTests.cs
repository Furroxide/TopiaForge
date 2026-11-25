using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitNativeTooltipTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            const string kit = "src/TopiaForge.Mods.UnityUi/";
            var paths = new[]
            {
                kit + "Widgets/TopiaForgeScopedTooltip.cs",
                kit + "Widgets/TopiaForgeScopedTooltipPanel.cs",
                kit + "Runtime/TopiaForgeTooltipLayer.cs",
                kit + "Runtime/TopiaForgeDismissStack.cs",
                kit + "Core/TopiaForgeDismissOrder.cs",
                kit + "Core/ITopiaForgeDismissable.cs",
                kit + "Core/TopiaForgeInputClaim.cs",
                kit + "Core/TopiaForgeInputGate.cs",
                kit + "Core/TopiaForgeLayerBands.cs",
                kit + "Core/TopiaForgeNavigationDriver.cs",
                "tests/Shared/AllocationProbe.cs",
            };
            var trees = paths.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path)), path: path))
                .Append(CSharpSyntaxTree.ParseText(TooltipEngineBoundary, path: "TooltipEngineBoundary.cs"))
                .Append(CSharpSyntaxTree.ParseText(TooltipKitBoundary, path: "TooltipKitBoundary.cs"))
                .Append(CSharpSyntaxTree.ParseText(TooltipScenarios, path: "TooltipScenarios.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    Path.GetFileName(path) != "TopiaForge.Mods.UnityUi.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.Mods.UnityUi", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emit = compilation.Emit(bytes);
            if (!emit.Success)
                throw new InvalidOperationException(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TooltipScenarios", throwOnError: true)!;
            probe.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Action>()();
            Console.WriteLine("Native tooltip passed timing, focus, containment, scoped paint, Back, teardown and allocation scenarios.");
        }
    }
}
