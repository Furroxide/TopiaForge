using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class WorldWidgetAdapterTests
    {
        // Execute the shipped adapter source. Doubles model engine/kit boundaries, not the
        // ownership, clock, raycast filtering or cleanup decisions being tested.
        internal static void Run()
        {
            var files = new[]
            {
                "src/TopiaForge.ModManager/UnityWorldLabels.cs",
                "src/TopiaForge.ModManager/UnityUiWorldWidgets.cs",
                "src/TopiaForge.ModManager/UnityMainThreadGuard.cs",
                "src/TopiaForge.Mods.Abstractions/Internal/UiLabelState.cs",
                "src/TopiaForge.Mods.Abstractions/UiWorldWidgets.cs",
            };
            var sources = files.Select(path => File.ReadAllText(Path.Combine(Program.FindRepoRoot(), path)))
                .Concat(new[] { EngineContract, KitContract, Scenarios });
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager",
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("WidgetScenarios", throwOnError: true)!;
            var failures = new List<string>();
            var names = new[] { "ClockAndCountdown", "OwnershipAndPool", "ScenesAndTargets", "Occlusion",
                "CanvasRecovery", "FailedLayerCreation", "RestingAllocations", "ThreadOwnership" };
            foreach (var name in names)
            {
                try { ((Action)probe.GetMethod(name)!.CreateDelegate(typeof(Action)))(); }
                catch (Exception exception) { failures.Add(name + ": " + exception.Message); }
            }
            if (failures.Count != 0) throw new InvalidOperationException(string.Join("\n", failures));
            Console.WriteLine("WorldWidgetAdapterTests passed: " + names.Length + " production-path scenarios.");
        }
    }
}
