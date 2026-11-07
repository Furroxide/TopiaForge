using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotInteractionAuthorityTests
    {
        private delegate void RunProbe(string scenario, IModLifetime lifetime, IRuntimeReplication replication,
            Func<IRuntimeReplication, RobotAgentSpawnRequest, Func<bool>, OperationResult<IRobotAgent>> spawn,
            Func<IDisposable> forwarded, Action stopOwner, Action<string> forgetId,
            Action<int> awaitAdoption, Func<long> sentRequests, Action<string, string?> settleClientCleanup);

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
            foreach (var scenario in new[]
            {
                "retained", "guest-custom", "replacement", "owner-stop", "world-retired", "policies", "worker",
                "client-live", "client-owner-stop", "client-retired-despawn", "client-retired-dispose"
            })
            {
                var rig = AuthorityRig.Create(players: 1);
                rig.Host.World.Proxies.IsSharedMod = mod => mod == WorldPeer.ModId || mod == "io.example.robot-provider";
                var probe = Activator.CreateInstance(probeType)!;
                var decorate = (Func<IRobotAgent, RobotAgentSpawnRequest, IRobotAgent>)probeType.GetMethod("Decorate")!
                    .CreateDelegate(typeof(Func<IRobotAgent, RobotAgentSpawnRequest, IRobotAgent>), probe);
                var player = rig.Players[0];
                var clientScenario = scenario.StartsWith("client-", StringComparison.Ordinal);
                var hostRobots = new FirstPartyRobots(rig.Host, clientScenario ? null : decorate);
                var localBodies = new Dictionary<IEntity, FakeWorldObject>();
                var clientRobots = new FirstPartyRobots(player, clientScenario ? (inner, request) =>
                {
                    var body = decorate(inner, request);
                    localBodies.Add(body, ((FirstPartyRobot)inner).Body);
                    return body;
                } : null);
                // The same-ID scenario needs the fake engine to keep each body distinct while the
                // production command table continues to resolve both handles by their shared ID.
                if (clientScenario)
                    player.Scene.ProviderObject = entity => localBodies.TryGetValue(entity, out var body) ? body : null;

                var owner = clientScenario
                    ? new AuthorityMod(player, ModExecutionContext.Participant)
                    : new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
                using var lifetime = owner.Lifetime;
                var run = (RunProbe)probeType.GetMethod("Run")!.CreateDelegate(typeof(RunProbe), probe);
                run(scenario, lifetime, owner.Seam, (clientScenario ? clientRobots : hostRobots).Provider.Spawn,
                    () => rig.Host.Runtime.Intents.EnterForwarded(WorldPeer.ModId, IntentSource.UiCallback, player.Control.Id.Value),
                    () => lifetime.BeginStop(), ForgetId, AwaitAdoption, () => player.Counters().AuthorityRequestsSent, SettleClientCleanup);

                void ForgetId(string id)
                {
                    if (clientScenario) clientRobots.Provider.Forget(id);
                    else rig.Host.Runtime.Spawns.Remove(rig.Host.Runtime.Spawns.Live().Single(record => record.EntityKey == id));
                }

                void AwaitAdoption(int count)
                {
                    if (!rig.Run(() => player.Client.OwnedObjects == count))
                        throw new InvalidOperationException("Robot cleanup test could not adopt " + count + " client robots.");
                }

                void SettleClientCleanup(string original, string? successor)
                {
                    if (!rig.Run(() => hostRobots.Agents.Values.Any(robot => robot.Name == original && !robot.IsAlive)))
                        throw new InvalidOperationException("The original prediction did not retire on the host.");
                    rig.Frames(4);
                    if (successor != null && !hostRobots.Agents.Values.Any(robot => robot.Name == successor && robot.IsAlive))
                        throw new InvalidOperationException("Retired cleanup reached the same-ID replacement on the host.");
                }
                Console.WriteLine("Robot interaction authority: " + scenario + " passed.");
            }
        }
    }
}
