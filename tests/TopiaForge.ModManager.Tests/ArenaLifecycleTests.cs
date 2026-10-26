using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ArenaLifecycleTests
    {
        internal static void Run()
        {
            var environment = Read("mods/TopiaForge.Worlds/HdrpEnvironment.cs");
            Check(environment.Contains("resources.Add(new ArenaSunSuppression())", StringComparison.Ordinal)
                && environment.Contains("inherited.Apply(UnityEngine.Object.FindObjectsByType<Light>", StringComparison.Ordinal),
                "the environment owns and applies inherited sunlight suppression");
            foreach (var file in new[] { "mods/TopiaForge.Worlds/NativeWorldPlayer.cs", "src/TopiaForge.ModManager/UnityWorldRuntimeBackend.Readiness.cs" })
            {
                var source = Read(file);
                Check(source.Contains("NativePlayerTeleport.Place(", StringComparison.Ordinal)
                    && !source.Contains("SetPositionAndRotation(", StringComparison.Ordinal), "all world placement uses native locomotion");
            }
            var sources = new[]
            {
                Read("mods/TopiaForge.Worlds/ArenaSunSuppression.cs"),
                Read("mods/TopiaForge.Worlds/UnityWorldResources.cs"),
                Read("mods/Shared/NativePlayerTeleport.cs"),
                Probe
            };
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = trusted.Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("ArenaProbe", sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            Check(result.Success, "production adapters compile: " + string.Join("; ", result.Diagnostics.Select(item => item.ToString())));
            Assembly.Load(bytes.ToArray()).GetType("ArenaProbe")!.GetMethod("Run")!.Invoke(null, null);
            WorldResourceScopeTests.Run();
            WorldPlayerPlacementTests.Run();
            WorldRuntimeReadinessTests.Run();
            Console.WriteLine("ArenaLifecycleTests passed: inherited suns, overlapping leases, restoration, deferred destruction, native teleport and failure contracts.");
        }

        private static string Read(string path) => File.ReadAllText(Path.Combine(Program.FindRepoRoot(), path));
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Arena lifecycle failed: " + message);
        }
    }
}
