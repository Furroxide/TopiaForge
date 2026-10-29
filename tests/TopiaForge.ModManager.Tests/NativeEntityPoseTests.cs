using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Mods.Internal;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class NativeEntityPoseTests
    {
        internal static void Run(string? baselineDirectory = null)
        {
            if (baselineDirectory == null) SessionScopes();
            var registry = baselineDirectory == null ? Read("src/TopiaForge.ModManager/UnityEntityRegistry.cs")
                : File.ReadAllText(Path.Combine(baselineDirectory, "UnityEntityRegistry.cs"));
            var ownerService = baselineDirectory == null ? Read("src/TopiaForge.ModManager/UnityWorldServices.cs")
                : File.ReadAllText(Path.Combine(baselineDirectory, "UnityWorldServices.cs"));
            var world = Read("src/TopiaForge.ModManager/ModContext.WorldView.cs");
            var owner = Read("mods/TopiaForge.RobotKit/RobotAgentService.OwnerFacade.cs");
            var scoped = Read("src/TopiaForge.ModManager/ModContext.ScopedGameplay.cs");
            var spawn = Method(owner, "Spawn");
            var capture = spawn.IndexOf("CaptureNativeEntityReadScope", StringComparison.Ordinal);
            if (capture < 0 || capture > spawn.IndexOf("service.Spawn", StringComparison.Ordinal))
                throw new InvalidOperationException("Read ownership must be captured before native spawn.");
            var sources = new[]
            {
                Read("src/TopiaForge.ModManager/UnityMainThreadGuard.cs"),
                Engine.Replace("/* registry */", Methods(registry, "TryGetTransform", "IsNativeReadSceneLoaded",
                    "TryResolve", "FromUnity", "ToUnity", "SetTransform", "AcquireMotion"))
                    .Replace("/* owner service */", Class(ownerService, "OwnerEntityService"))
                    .Replace("/* context scope */", Method(world, "CaptureNativeEntityReadScope") + Class(world, "NativeEntityReadScope"))
                    .Replace("/* lifetime read */", Method(Class(scoped, "LifetimeEntityService"), "TryGetTransform"))
                    .Replace("/* robot wrapper */", Class(owner, "OwnerRobotAgent")),
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
            var result = compilation.Emit(bytes);
            if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("PoseScenarios", true)!;
            // The existing transport fixture is an in-memory channel; no sockets or backend are involved.
            var rig = new WorldRig();
            rig.OpenToMultiplayer();
            var guest = rig.AddPlayer("guest");
            rig.JoinAndSynchronize(guest);
            var hostOwner = new AuthorityMod(rig.Host);
            var guestOwner = new AuthorityMod(guest);
            using var hostLifetime = hostOwner.Lifetime;
            using var guestLifetime = guestOwner.Lifetime;
            if (guestOwner.Seam.Realm != InternalNetRealm.Client) throw new InvalidOperationException("Replica route must be joined.");
            Func<IRobotAgent, bool, IRobotAgent> routed = (agent, replica) => new RoutedRobotAgent(agent, replica ? guestOwner.Seam : hostOwner.Seam);
            ((Action<Func<IRobotAgent, bool, IRobotAgent>>)probe.GetMethod("Run")!.CreateDelegate(typeof(Action<Func<IRobotAgent, bool, IRobotAgent>>)))(routed);
            Console.WriteLine("Native entity pose: production read chain, owner/routed wrappers, world and native boundary checks passed.");
        }

        private static void SessionScopes()
        {
            UnityMainThreadGuard.CaptureCurrentThread();
            using var fixture = new SessionFixture(Path.Combine(Path.GetTempPath(), "TopiaForgePose-" + Guid.NewGuid().ToString("N")));
            fixture.Parent.ConfigureSessions(fixture.Hosted);
            void Check(bool value, string reason)
            { if (!value) throw new InvalidOperationException("Native read scope: " + reason); }
            Check(fixture.Parent.CaptureNativeEntityReadScope() == null, "no scope before world readiness");
            fixture.Launch();
            var session = SessionFixture.ModeSession!;
            var child = (ModContext)session.Context;
            var parentScope = fixture.Parent.CaptureNativeEntityReadScope();
            var childScope = child.CaptureNativeEntityReadScope();
            Check(parentScope != null && parentScope.IsCurrent(fixture.Parent.Lifetime), "actual parent belongs to current world");
            Check(childScope != null && childScope.IsCurrent(child.Lifetime), "actual child belongs to current world");
            Check(!parentScope!.IsCurrent(child.Lifetime) && !childScope!.IsCurrent(fixture.Parent.Lifetime), "exact context lifetimes cannot be exchanged");
            Check(childScope!.SceneInstanceId == session.World.Scene.InstanceId && childScope.SceneName == session.World.Scene.Name,
                "native scene identity comes from captured readiness");
            using var outsider = new SessionFixture(Path.Combine(Path.GetTempPath(), "TopiaForgePoseOther-" + Guid.NewGuid().ToString("N")));
            outsider.Parent.ConfigureSessions(fixture.Hosted);
            Check(outsider.Parent.CaptureNativeEntityReadScope() == null, "same package from another runtime root is refused");
            var restart = session.RestartAsync(); fixture.Wait(restart);
            Check(restart.Result.Succeeded && !parentScope.IsCurrent(fixture.Parent.Lifetime) && !childScope.IsCurrent(child.Lifetime),
                "same-scene restart retires parent and child read scopes");
            var successor = (ModContext)SessionFixture.ModeSession!.Context;
            var successorScope = successor.CaptureNativeEntityReadScope();
            Check(successorScope != null && successorScope.IsCurrent(successor.Lifetime), "successor has its own read scope");
            fixture.Parent.BeginStopping();
            Check(!successorScope!.IsCurrent(successor.Lifetime), "parent shutdown stops descendant read scopes immediately");
            Console.WriteLine("Native pose scope: real parent/child/orchestrator admission and restart checks passed.");
        }

        private static string Read(string path) => File.ReadAllText(Path.Combine(Program.FindRepoRoot(), path));
        private static string Class(string source, string name) => CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>().Single(node => node.Identifier.Text == name).ToFullString();
        private static string Method(string source, string name) => CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(node => node.Identifier.Text == name).ToFullString();
        private static string Methods(string source, params string[] names) => string.Join("\n", CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Where(node => names.Contains(node.Identifier.Text)).Select(node => node.ToFullString()));
    }
}
