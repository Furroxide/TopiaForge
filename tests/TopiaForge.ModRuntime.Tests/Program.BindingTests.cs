using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private const string BindingAssembly = "TopiaForge.BindingTestMod.dll";
        private static readonly string[] BindingCases = { "success", "shapes", "hash", "hash-missing", "hash-valid", "location", "forwarder", "receipt", "entry-race", "selection-drift", "snapshot", "ownership", "registry", "discovery-drain", "discover-valid", "discover-failed", "discover-bounded", "discover-constructor", "discover-stale", "discover-cancel", "discover-cancel-cleanup", "discover-worker-cancel", "discover-worker-cancel-callback", "discover-scope-construction", "discover-scope-construction-cleanup", "discover-malformed", "discover-duplicate", "discover-null", "discover-null-result", "discover-null-item", "discover-cleanup", "discover-limits", "discover-timeout", "startup", "constructor", "failed-owner", "native-owner-drain", "native-failed-owner", "native-failed-scene", "native-gate" };

        private static void TestProductionBindingsInFreshProcesses()
        {
            foreach (var name in BindingCases)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(Program).Assembly.Location);
                start.ArgumentList.Add("--binding-case");
                start.ArgumentList.Add(name);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated binding harness.");
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(120000)) { child.Kill(true); throw new InvalidOperationException("Binding child timed out: " + name); }
                Assert(child.ExitCode == 0, "binding case " + name + ": " + output.Result + error.Result);
            }
        }

        private static int RunBindingCase(string name)
        {
            if (!BindingCases.Contains(name, StringComparer.Ordinal)) return 2;
            var root = Directory.CreateTempSubdirectory("TopiaForgeBindingTests-").FullName;
            try
            {
                var fixture = NewFixture(root, "binding", "TopiaForge.BindingTestMod.BindingMod", BindingAssembly);
                var manifest = fixture.Manifest;
                manifest.SchemaVersion = 6;
                manifest.Contributions = BindingContributions(manifest.Id);
                if (name.StartsWith("discover-", StringComparison.Ordinal))
                {
                    var secondFamily = JsonUtil.Clone(manifest.Contributions.Worlds[1]);
                    secondFamily.Id += "two";
                    manifest.Contributions.Worlds.Add(secondFamily);
                }
                if (name == "shapes")
                    foreach (var type in new[] { "WrongKind", "AbstractFactory", "OpenFactory`1", "HiddenFactory", "PrivateConstructorFactory", "Missing" })
                        manifest.Contributions.Gamemodes.Add(new ModGamemodeDeclaration
                        {
                            Id = manifest.Id + ".bad" + manifest.Contributions.Gamemodes.Count,
                            Name = type,
                            Implementation = new ModImplementationBinding { Type = "TopiaForge.BindingTestMod." + type }
                        });
                if (name == "hash" || name == "hash-missing" || name == "hash-valid")
                {
                    manifest.Contributions.Gamemodes[0].Implementation!.Assembly = BindingAssembly;
                    if (name == "hash") manifest.Hashes[BindingAssembly] = new string('0', 64);
                    if (name == "hash-valid") manifest.Hashes[BindingAssembly] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Package.PackagePath, BindingAssembly))));
                }
                if (name == "forwarder")
                {
                    const string forwarder = "TopiaForge.BindingForwarderTestMod.dll";
                    File.Copy(Path.Combine(AppContext.BaseDirectory, forwarder), Path.Combine(fixture.Package.PackagePath, forwarder));
                    manifest.Contributions.Gamemodes[0].Implementation!.Assembly = forwarder;
                    manifest.Hashes[forwarder] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Package.PackagePath, forwarder))));
                }
                WriteBindingReceipt(fixture);
                if (name == "entry-race") { TestEntryVerificationLeaseRace(fixture); return 0; }
                if (name == "receipt") File.WriteAllText(Path.Combine(fixture.Package.PackagePath, "unexpected.txt"), "changed after scan");
                if (name == "location")
                {
                    // Preload identical CLR identity from a different physical owner. Binding must check
                    // the assembly actually returned by the CLR, not only the selected file's hash.
                    Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, BindingAssembly));
                }
                var selectedPackages = new List<ModPackage> { fixture.Package };
                if (name == "ownership")
                {
                    var longer = NewFixture(root, "binding-child", "TopiaForge.BindingTestMod.BindingMod", BindingAssembly);
                    longer.Manifest.SchemaVersion = 6;
                    longer.Manifest.Id = manifest.Id + ".extension";
                    longer.Package.State!.Id = longer.Manifest.Id;
                    var shadowed = new ModWorldDeclaration { Id = longer.Manifest.Id + ".world", Name = "Owned by longer package", Content = new ModWorldContent { Kind = ModWorldContent.GameSceneKind, SceneName = "DeclaredScene" }, Transitions = new List<string> { ModTransitions.SceneReplacement }, Spawn = new ModSpawnPolicy { Kind = ModSpawnPolicy.ProviderDefaultKind } };
                    longer.Manifest.Contributions = new ModContributions { Worlds = new List<ModWorldDeclaration> { shadowed } };
                    manifest.Contributions.Worlds.Add(JsonUtil.Clone(shadowed));
                    selectedPackages.Add(new ModPackage(longer.Package.PackagePath, longer.Manifest, longer.Package.State, new[] { "Synthetic failed selected package" }));
                }
                var profile = new EffectiveProfile("binding-profile", 7, selectedPackages.Select(package => new ResolvedPackage(package.Manifest!.Id, package.Manifest.Version, package.Manifest)).ToArray());
                var runtime = fixture.CreateRuntimeInstance();
                runtime.ConfigureSessionSelection(profile);
                if (name == "selection-drift")
                {
                    manifest.Contributions.LaunchTargets[0].Title = "Changed after selection";
                    var rejected = false;
                    try { runtime.Load(selectedPackages); } catch (InvalidOperationException) { rejected = true; }
                    Assert(rejected && runtime.LoadedModIds.Count == 0, "changed selected manifest is rejected before package execution");
                    PumpBinding(runtime.NativeDispatcher, runtime.UnloadAllAsync());
                    return 0;
                }
                if (name.StartsWith("native-", StringComparison.Ordinal)) { TestNativeBindingLifecycle(fixture, runtime, profile, name); return 0; }
                if (name == "failed-owner")
                {
                    var assembly = Assembly.LoadFrom(Path.Combine(fixture.Package.PackagePath, BindingAssembly));
                    assembly.GetType("TopiaForge.BindingTestMod.BindingProbe")!.GetField("ThrowOnLoad")!.SetValue(null, true);
                }
                runtime.Load(selectedPackages);
                var registry = runtime.SessionBindings!;
                var snapshot = registry.Capture();
                Assert(snapshot.Profile.Packages.Count == selectedPackages.Count && snapshot.Profile.Packages[0].Identity.Equals(profile.Packages[0].Identity), "selected package identities survive load failure");
                if (name == "hash" || name == "hash-missing" || name == "location" || name == "forwarder" || name == "receipt" || name == "failed-owner")
                {
                    Assert(snapshot.Gamemodes.Count == 0, "invalid assembly or failed owner must not bind");
                    Assert(snapshot.Bindings.Availability.Any(value => value.Kind == "gamemode"), "failed declaration remains visible");
                    Assert(snapshot.BindingFailures.Count > 0, "binding failure retains structured provenance");
                    if (name == "hash") Assert(snapshot.Worlds.Count == 0, "a supplied entry hash is enforced for omitted assembly fields too");
                    if (name == "hash-missing" || name == "forwarder") Assert(snapshot.Worlds.Count == 2, "omitted entry assembly retains optional manifest hash semantics");
                }
                else
                {
                    Assert(snapshot.Gamemodes.Count == 1 && snapshot.Worlds.Count == (name.StartsWith("discover-", StringComparison.Ordinal) ? 3 : 2) && snapshot.DiscoverySources.Count == (name.StartsWith("discover-", StringComparison.Ordinal) ? 2 : 1), "normal package binds factory, provider and discovery family atomically: " + string.Join("; ", snapshot.BindingFailures.Select(failure => failure.Code + ": " + failure.Message)));
                    if (name == "shapes") Assert(snapshot.BindingFailures.Count == 6, "all malformed declaration shapes are visible independently");
                    var probe = Assembly.LoadFrom(Path.Combine(fixture.Package.PackagePath, BindingAssembly)).GetType("TopiaForge.BindingTestMod.BindingProbe")!;
                    var events = (List<string>)probe.GetField("Events")!.GetValue(null)!;
                    Assert(!events.Any(value => value.EndsWith(":constructor", StringComparison.Ordinal)), "binding must not construct factories, providers or discovery sources");
                    probe.GetField("ActiveScopes")!.SetValue(null, (Func<int>)(() => snapshot.Contexts[manifest.Id].ActiveChildScopeCount));
                    fixture.GameplayHost.ScopeCreated = lifetime => probe.GetField("ConstructorLifetime")!.SetValue(null, lifetime);
                    events.Clear();
                    if (name == "snapshot")
                    {
                        manifest.Contributions.Gamemodes.Clear();
                        manifest.Contributions.Worlds.Clear();
                        var exposed = snapshot.Profile.Packages[0].Manifest;
                        exposed.Contributions!.LaunchTargets.Clear();
                        Assert(snapshot.Profile.Packages[0].Manifest.Contributions!.Gamemodes.Count == 1
                            && snapshot.Profile.Packages[0].Manifest.Contributions!.LaunchTargets.Count == 1, "captured manifests are independent of mutable inputs and exposed copies");
                    }
                    if (name.StartsWith("discover-scope-construction", StringComparison.Ordinal))
                        TestDiscoveryScopeConstruction(runtime, registry, fixture, profile.Packages[0].Identity, name.EndsWith("cleanup", StringComparison.Ordinal));
                    else if (name.StartsWith("discover-", StringComparison.Ordinal)) TestProductionDiscovery(runtime, registry, probe, events, profile.Packages[0].Identity, name);
                    else if (name == "discovery-drain") TestDiscoveryShutdownDrain(runtime, registry, probe, events, profile.Packages[0].Identity);
                    else if (name == "registry") TestBindingDiscoveryTokens(registry, profile.Packages[0].Identity);
                    else if (name != "shapes") TestBoundLaunch(runtime, profile, probe, events, name);
                }
                var shutdown = runtime.UnloadAllAsync();
                PumpBinding(runtime.NativeDispatcher, shutdown);
                Assert(registry.Capture().Contexts.Count == 0 && registry.Capture().Worlds.Count == 0, "owner removal atomically clears live bindings and contexts");
                Assert(registry.Capture().Profile.Packages.Count == selectedPackages.Count, "owner removal preserves selected ownership diagnostics");
                Console.WriteLine("Binding case passed: " + name);
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
            finally { Environment.SetEnvironmentVariable("TOPIAFORGE_RUNTIME_TEST_TRACE", null); TryDelete(root); }
        }

        private static void TestEntryVerificationLeaseRace(Fixture fixture)
        {
            var changed = false;
            var loader = new VerifiedPackageAssemblyLoader(new ModAssemblyResolutionCatalog(new[] { fixture.Package }, string.Empty),
                (_, _) => { }, path =>
                {
                    using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                    output.WriteByte(0x42);
                    changed = true;
                });
            try { loader.LoadType(fixture.Package, new ModImplementationBinding { Type = fixture.Manifest.EntryType }); }
            catch (BindingVerificationException error) when (error.Code == RuntimeBindingFailureCode.ReceiptInvalid)
            { Assert(changed, "race hook must change the selected bytes"); return; }
            throw new InvalidOperationException("The loader accepted bytes changed between receipt verification and opening its sharing lease.");
        }

        private static void TestBoundLaunch(TopiaForge.ModManager.ModRuntime runtime, EffectiveProfile profile, Type probe, List<string> events, string name)
        {
            if (name == "startup") probe.GetField("ThrowOnStart")!.SetValue(null, true);
            if (name == "constructor") probe.GetField("ThrowOnFactoryConstruction")!.SetValue(null, true);
            var environment = new BoundEnvironment(runtime.SessionBindings!);
            var orchestrator = new GamemodeSessionOrchestrator(runtime.NativeDispatcher, runtime.NativeTransitions, environment, runtime.RuntimeOwnershipId);
            runtime.AttachSessionLifecycle(orchestrator, runtime.NativeDispatcher);
            var manifest = profile.Packages[0].Manifest;
            var plan = LaunchResolver.Resolve(profile, new LaunchRequest(manifest.Contributions!.LaunchTargets[0].Id)).Plan!;
            var launch = orchestrator.StartAsync(plan.Descriptor, "binding-request");
            PumpBinding(runtime.NativeDispatcher, launch);
            Assert(launch.Result.Succeeded == (name == "success" || name == "hash-valid" || name == "ownership" || name == "snapshot"), "production-bound launch outcome must reflect callback failure");
            if (name == "success" || name == "hash-valid" || name == "ownership" || name == "snapshot") Assert(orchestrator.Current.Phase == SessionPhase.Running, "launch succeeds only after Running");
            var stopped = orchestrator.ShutdownAsync();
            PumpBinding(runtime.NativeDispatcher, stopped);
            Assert(events.Contains("provider:resource:dispose"), "allocated provider resource is always released");
            Assert(events.Contains("factory:constructor-resource:dispose"), "resource allocated through the actual child lifetime before factory constructor failure is released");
            if (name != "constructor") Assert(events.Contains("factory:resource:dispose"), "allocated startup resource is always released");
            Assert(runtime.SessionBindings!.Capture().Contexts.Values.All(context => context.ActiveChildScopeCount == 0), "failed startup drains every child scope");
        }

        private static void TestDiscoveryShutdownDrain(TopiaForge.ModManager.ModRuntime runtime, RuntimeBindingRegistry registry, Type probe, List<string> events, PackageIdentity package)
        {
            var attempt = registry.BeginDiscovery(package, CancellationToken.None)!;
            var work = registry.RegisterDiscoveryWork(attempt)!;
            var parent = attempt.OwnerContext;
            var ownerDrain = registry.WaitForDiscoveryIdleAsync(package);
            var shutdown = runtime.UnloadAllAsync();
            runtime.NativeDispatcher.Drain();
            Assert(parent.Lifetime.IsStopping, "discovery owner is cancelled before the drain barrier");
            Assert(!shutdown.IsCompleted && !events.Contains("package:unload"), "package unload must await active discovery work and child cleanup");
            Assert(!ownerDrain.IsCompleted && registry.RegisterDiscoveryWork(attempt) == null, "stopping refuses new discovery work while retaining old work");
            work.Dispose(); work.Dispose();
            PumpBinding(runtime.NativeDispatcher, shutdown);
            Assert(ownerDrain.IsCompletedSuccessfully && events.Contains("package:unload"), "last discovery cleanup releases package unload exactly once");
        }

        private static void TestBindingDiscoveryTokens(RuntimeBindingRegistry registry, PackageIdentity package)
        {
            var first = registry.BeginDiscovery(package, CancellationToken.None)!;
            var newer = registry.BeginDiscovery(package, CancellationToken.None)!;
            var family = newer.Families[0].DeclarationId;
            var found = new[] { new DiscoveredWorldObservation(family + ".one", family, "One") };
            Assert(registry.PublishDiscovery(newer, found, Array.Empty<DeclarationAvailability>()), "current discovery publishes");
            var captured = registry.Capture();
            Assert(!registry.PublishDiscovery(first, Array.Empty<DiscoveredWorldObservation>(), Array.Empty<DeclarationAvailability>()), "stale discovery cannot replace newer results");
            using var cancellation = new CancellationTokenSource();
            var cancelled = registry.BeginDiscovery(package, cancellation.Token)!;
            cancellation.Cancel();
            Assert(!registry.PublishDiscovery(cancelled, Array.Empty<DiscoveredWorldObservation>(), Array.Empty<DeclarationAvailability>()), "cancelled discovery cannot clear observations");
            Assert(captured.Observation.DiscoveredWorlds.Count == 1 && registry.Capture().Observation.DiscoveredWorlds.Count == 1, "captures and published observations remain immutable");
            var removal = registry.BeginDiscovery(package, CancellationToken.None)!;
            var loading1 = registry.BeginPackageLoad(package);
            var loading2 = registry.BeginPackageLoad(package);
            Assert(!registry.CommitFailed(loading1, "stale failure"), "stale package attempt cannot replace current load metadata");
            Assert(registry.CommitFailed(loading2, "current failure"), "current package failure is published");
            Assert(registry.Capture().Profile.Packages.Count == 1, "runtime load failure retains namespace ownership");
            registry.BeginOwnerStop(package);
            Assert(!registry.PublishDiscovery(removal, found, Array.Empty<DeclarationAvailability>()), "removed owner cannot publish late observations");
            Assert(registry.Capture().Observation.DiscoveredWorlds.Count == 0 && registry.Capture().Worlds.Count == 0, "removal clears observations and all package bindings in one snapshot");
            Assert(captured.Worlds.Count == 2, "earlier immutable capture is not modified by removal");
        }

        private static ModContributions BindingContributions(string id) => new ModContributions
        {
            Gamemodes = new List<ModGamemodeDeclaration> { new ModGamemodeDeclaration { Id = id + ".mode", Name = "Mode", Implementation = new ModImplementationBinding { Type = "TopiaForge.BindingTestMod.GoodFactory" } } },
            Worlds = new List<ModWorldDeclaration>
            {
                new ModWorldDeclaration { Id = id + ".world", Name = "World", Content = new ModWorldContent { Kind = ModWorldContent.ProviderKind, Implementation = new ModImplementationBinding { Type = "TopiaForge.BindingTestMod.GoodProvider" } }, Transitions = new List<string> { ModTransitions.SceneReplacement }, Spawn = new ModSpawnPolicy { Kind = ModSpawnPolicy.ProviderDefaultKind } },
                new ModWorldDeclaration { Id = id + ".family", Name = "Family", Content = new ModWorldContent { Kind = ModWorldContent.DiscoveredKind, Implementation = new ModImplementationBinding { Type = "TopiaForge.BindingTestMod.GoodDiscovery" } }, Transitions = new List<string> { ModTransitions.SceneReplacement }, Spawn = new ModSpawnPolicy { Kind = ModSpawnPolicy.ProviderDefaultKind } }
            },
            LaunchTargets = new List<ModLaunchTargetDeclaration> { new ModLaunchTargetDeclaration { Id = id + ".target", Title = "Target", Gamemode = id + ".mode", World = new ModWorldPolicy { Policy = ModWorldPolicy.FixedPolicy, Default = id + ".world" }, Transition = ModLaunchTargetDeclaration.AutoTransition } }
        };

        private static void WriteBindingReceipt(Fixture fixture) => File.WriteAllText(Path.Combine(fixture.Package.PackagePath, PackageInstallReceipt.FileName),
            JsonUtil.Serialize(PackageInstallReceipt.Create(Path.Combine(fixture.Package.PackagePath, BindingAssembly), fixture.Package.PackagePath, fixture.Manifest)));
        private static void PumpBinding(HostDispatcher host, Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { host.Drain(); Thread.Sleep(1); }
            Assert(task.IsCompleted, "host work timed out");
            host.Drain();
            task.GetAwaiter().GetResult();
        }
        private sealed class BoundEnvironment : IRuntimeSessionEnvironment
        {
            private readonly RuntimeBindingRegistry registry;
            internal BoundEnvironment(RuntimeBindingRegistry registry) { this.registry = registry; }
            public RuntimeSessionSnapshot Capture() => registry.Capture();
            public Task<OperationResult<bool>> LoadMainMenuAsync(IInternalSceneTransitionService transitions, CancellationToken cancellationToken) => Task.FromResult(OperationResult<bool>.Success(true));
        }
    }
}
