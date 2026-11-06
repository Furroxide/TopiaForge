using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class NativeSpawnWiringTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            string Read(string path) => File.ReadAllText(Path.Combine(root, path));
            var readiness = CSharpSyntaxTree.ParseText(Read("src/TopiaForge.ModManager/UnityWorldRuntimeBackend.Readiness.cs"));
            var binding = new PlayerTypeBinding();
            var rewritten = binding.Visit(readiness.GetRoot())!;
            if (binding.Replacements != 1) throw new InvalidOperationException("Expected exactly one readiness native player TYPE lookup.");
            var teleport = CSharpSyntaxTree.ParseText(Read("mods/Shared/NativePlayerTeleport.cs"));
            binding = new PlayerTypeBinding();
            var rewrittenTeleport = binding.Visit(teleport.GetRoot())!;
            if (binding.Replacements != 1) throw new InvalidOperationException("Expected exactly one teleport native player TYPE lookup.");
            var methods = new[] { "ApplySpawn", "ResolvePlayer", "ReadTransform", "WarnSpawnOffset", "ReadDefaultSpawn", "ValidateContent", "FindSpawnMarkers" };
            var production = string.Join("\n", rewritten.DescendantNodes().OfType<FieldDeclarationSyntax>().Select(node => node.ToFullString()))
                + string.Join("\n", rewritten.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(node => methods.Contains(node.Identifier.Text)).Select(node => node.ToFullString()));
            var loading = CSharpSyntaxTree.ParseText(Read("src/TopiaForge.ModManager/UnityWorldRuntimeBackend.Loading.cs")).GetRoot();
            foreach (var name in new[] { "CaptureScene", "ValidateScene", "Dispose", "OnSceneLoaded", "OnSceneUnloaded" })
                production += Method(loading, name);
            production += loading.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single(node => node.Identifier.Text == "RequiresDispatch").ToFullString();
            var backend = CSharpSyntaxTree.ParseText(Read("src/TopiaForge.ModManager/UnityWorldRuntimeBackend.cs")).GetRoot();
            foreach (var name in new[] { "Matches", "Unwrap", "Failure" }) production += Method(backend, name);
            var sources = new[]
            {
                Boundaries, Adapter.Replace("/* production */", production), Scenarios,
                rewrittenTeleport.ToFullString(), Read("src/TopiaForge.ModManager/NativeSpawnSettlement.cs"),
                Read("src/TopiaForge.ModManager/WorldPreparation.cs"), Read("src/TopiaForge.ModManager/WorldRuntimeBackend.cs"),
                Read("src/TopiaForge.ModManager/HostDispatcher.cs"), Read("src/TopiaForge.ModManager/IHostDispatcher.cs"),
                Read("src/TopiaForge.ModManager/UnityMainThreadGuard.cs")
            };
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager", sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException("Spawn wiring compile failure (not a behavioral red):\n"
                + string.Join("\n", emitted.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)));
            var scenario = Assembly.Load(bytes.ToArray()).GetType("SettlementProbe.Scenarios", true)!;
            ((Action)scenario.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
            Console.WriteLine("Actual native ApplySpawn and WorldPreparation wiring passed controlled boundaries.");
        }

        private static string Method(SyntaxNode source, string name) => source.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(node => node.Identifier.Text == name).ToFullString();

        private sealed class PlayerTypeBinding : CSharpSyntaxRewriter
        {
            internal int Replacements;
            public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (node.Expression.ToString() == "Type.GetType" && node.ArgumentList.Arguments.Count > 0
                    && node.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal
                    && literal.Token.ValueText == "PlayerController, GameCode")
                {
                    Replacements++;
                    return SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName("SettlementProbe.PlayerController")).WithTriviaFrom(node);
                }
                return base.VisitInvocationExpression(node);
            }
        }
    }
}
