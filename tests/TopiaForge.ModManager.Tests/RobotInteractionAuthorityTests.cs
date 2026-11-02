using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotInteractionAuthorityTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            string Read(string path) => File.ReadAllText(Path.Combine(root, path));
            var agent = CSharpSyntaxTree.ParseText(Read("mods/TopiaForge.RobotKit/RobotAgent.cs")).GetRoot();
            var install = agent.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.Text == "InstallCustomInteractionBridge").ToFullString();
            // Compile the complete shipped owner facade and bridge, and the exact native installation method.
            // Only the engine/native body boundary is substituted. Routing and guest origin use the real runtime.
            var sources = new[]
            {
                Read("mods/TopiaForge.RobotKit/RobotAgentService.OwnerFacade.cs"),
                Read("mods/TopiaForge.RobotKit/RobotInteractionBridge.cs"),
                Read("mods/TopiaForge.RobotKit/RobotKitLog.cs"),
                Engine,
                Boundary.Replace("/* production installation */", install),
                Scenarios
            };
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager",
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success)
                throw new InvalidOperationException("Interaction probe compilation failed: " + string.Join("\n",
                    emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            Console.WriteLine("Robot interaction authority: production callback sources compiled.");
            var probeType = Assembly.Load(bytes.ToArray()).GetType("InteractionProbe", true)!;
            foreach (var scenario in new[] { "retained", "guest-custom", "replacement", "owner-stop", "world-retired", "policies" })
            {
                var rig = AuthorityRig.Create(players: 1);
                rig.Host.World.Proxies.IsSharedMod = mod => mod == WorldPeer.ModId || mod == "io.example.robot-provider";
                var probe = Activator.CreateInstance(probeType)!;
                var decorate = (Func<IRobotAgent, RobotAgentSpawnRequest, IRobotAgent>)probeType.GetMethod("Decorate")!
                    .CreateDelegate(typeof(Func<IRobotAgent, RobotAgentSpawnRequest, IRobotAgent>), probe);
                var robots = new FirstPartyRobots(rig.Host, decorate);
                _ = new FirstPartyRobots(rig.Players[0]);
                var owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
                using var lifetime = owner.Lifetime;
                var run = (Action<string, IModLifetime, IRuntimeReplication,
                    Func<IRuntimeReplication, RobotAgentSpawnRequest, OperationResult<IRobotAgent>>,
                    Func<IDisposable>, Action>)probeType.GetMethod("Run")!.CreateDelegate(typeof(Action<string,
                        IModLifetime, IRuntimeReplication,
                        Func<IRuntimeReplication, RobotAgentSpawnRequest, OperationResult<IRobotAgent>>,
                        Func<IDisposable>, Action>), probe);
                run(scenario, lifetime, owner.Seam, robots.Provider.Spawn,
                    () => rig.Host.Runtime.Intents.EnterForwarded(WorldPeer.ModId,
                        IntentSource.UiCallback, rig.Players[0].Control.Id.Value),
                    () => owner.Lifetime.BeginStop());
                Console.WriteLine("Robot interaction authority: " + scenario + " passed.");
            }
        }
    }
}
