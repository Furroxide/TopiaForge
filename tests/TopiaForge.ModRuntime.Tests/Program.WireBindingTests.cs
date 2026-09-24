using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestProductionWire(Fixture fixture, TopiaForge.ModManager.ModRuntime runtime,
            EffectiveProfile profile, IReadOnlyList<ModPackage> selected, string name)
        {
            var plan = LaunchResolver.Resolve(profile, new LaunchRequest(fixture.Manifest.Id + ".target")).Plan!.Descriptor;
            var wire = new ProfileLaunchConfigurationV4(profile.ProfileId, profile.Revision + (name == "wire-drift" ? 1 : 0), "Wire-Request", name == "wire-menu" ? "main-menu" : "launch-target",
                profile.Packages.Select(package => package.Identity).ToArray(), PackageSetDigest.Of(profile.Packages), false, false,
                profile.Packages.Select(package => package.Id).ToArray(), new Dictionary<string, string>(), name == "wire-menu" ? null : plan);
            var store = new LaunchStagingStore(fixture.Paths); fixture.Paths.EnsureCreated();
            File.WriteAllText(store.RequestPath(wire.RequestId), LaunchTransportJson.WriteProfile(wire));
            wire = store.ConsumeRequest(store.RequestPath(wire.RequestId));
            var menuCalls = 0;
            runtime.ActivateSessionRuntime(profile, (_, _) => { menuCalls++; return Task.FromResult(OperationResult<bool>.Success(true)); });
            if (name == "wire-provider-failed") selected = new[] { new ModPackage(fixture.Package.PackagePath, fixture.Manifest,
                fixture.Package.State, new[] { "The selected provider failed verification." }) };
            var errors = new List<Exception>();
            using var publisher = new RuntimeLaunchPublisher(runtime.Sessions, store, wire.RequestId, errors.Add);
            runtime.Load(selected);
            var snapshot = runtime.SessionBindings!.Capture();
            var probe = Assembly.LoadFrom(Path.Combine(fixture.Package.PackagePath, BindingAssembly)).GetType("TopiaForge.BindingTestMod.BindingProbe")!;
            var events = (List<string>)probe.GetField("Events")!.GetValue(null)!;
            probe.GetField("ActiveScopes")!.SetValue(null, (Func<int>)(() => runtime.SessionBindings.Capture().Contexts.Values.Sum(context => context.ActiveChildScopeCount)));
            fixture.GameplayHost.ScopeCreated = lifetime => probe.GetField("ConstructorLifetime")!.SetValue(null, lifetime);
            if (name == "wire-observation")
            {
                SetDiscoveryHandler(probe, (context, _) => Task.FromResult(Discovered(context.FamilyId, "one")));
                PumpBinding(runtime.NativeDispatcher, runtime.DiscoverWorldsAsync());
                Assert(runtime.SessionBindings.Capture().Observation.DiscoveredWorlds.Count == 1, "Production discovery must create an observed declared-family instance.");
            }
            publisher.PublishObservations(runtime.SessionBindings.CaptureObservations());
            var observed = runtime.SessionBindings.CaptureObservations().Single();
            Assert(File.Exists(store.ObservationPath(observed)), "Every exact selected owner publishes a provenance-bound observation, including failed owners.");
            var launched = runtime.Sessions.ExecuteCommandAsync(wire); PumpBinding(runtime.NativeDispatcher, launched);
            var outcome = LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath(wire.RequestId)));
            var success = name != "wire-drift" && name != "wire-provider-failed";
            Assert(launched.Result.Succeeded == success && outcome.RequestId == wire.RequestId && outcome.Command == wire.Command,
                "Consumed V4 produces the original correlated success/failure through real binding and runtime authority.");
            if (!success || name == "wire-menu") Assert(!events.Contains("provider:constructor") && !events.Contains("factory:constructor"), "Preflight failure and explicit menu cannot construct gameplay.");
            if (name == "wire-menu") Assert(menuCalls == 1 && outcome.Phase == "idle", "Explicit V4 menu invokes one transition and acknowledges Idle.");
            if (success && name != "wire-menu") Assert(runtime.Sessions.Current.Phase == SessionPhase.Running && outcome.Phase == "running", "Target success is acknowledged only at Running.");
            PumpBinding(runtime.NativeDispatcher, runtime.UnloadAllAsync());
            publisher.PublishObservations(runtime.SessionBindings.CaptureObservations());
            var removed = LaunchTransportJson.ReadObservation(File.ReadAllText(store.ObservationPath(observed)));
            Assert(removed.DiscoveredWorlds.Count == 0 && removed.Availability.Count != 0 && removed.ObservationRevision > observed.ObservationRevision,
                "Production owner unload atomically clears discovered content and publishes structured unavailable declarations.");
            if (success && name != "wire-menu") Assert(File.Exists(store.OutcomePath(wire.RequestId, true)), "Shutdown publishes a separate terminal outcome for the launched session.");
            Assert(errors.Count == 0, "No publication error may be hidden in production binding verification.");
            Console.WriteLine("Production V4 case passed: " + name);
        }
    }
}
