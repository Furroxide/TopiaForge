using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using TopiaForge.Mods.Internal;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestNativeBindingLifecycle(Fixture fixture, TopiaForge.ModManager.ModRuntime runtime,
            EffectiveProfile profile, string name)
        {
            if (name == "native-gate")
            {
                TestNativeAdmissionComposition(runtime.NativeTransitions);
                PumpBinding(runtime.NativeDispatcher, runtime.UnloadAllAsync());
                return;
            }
            var probe = Assembly.LoadFrom(Path.Combine(fixture.Package.PackagePath, BindingAssembly)).GetType("TopiaForge.BindingTestMod.BindingProbe")!;
            var events = (List<string>)probe.GetField("Events")!.GetValue(null)!;
            AssetNativeWorkTicket? ticket = null;
            IModContext? context = null;
            var failedEntry = name == "native-failed-owner" || name == "native-failed-scene";
            var nativeScene = name == "native-failed-scene" ? new ShutdownNativeDispatch() : null;
            var reentrantBlocked = false;
            probe.GetField("OnLoadHook")!.SetValue(null, (Action<IModContext>)(loaded =>
            {
                context = loaded;
                ticket = ((IInternalNativeWorkLifetime)loaded.Lifetime).RegisterNativeWork("synthetic pending bundle");
                loaded.Lifetime.Defer(() => events.Add("package:resource:dispose"));
                if (nativeScene != null)
                {
                    var dispatched = new OwnerSceneTransitionService(fixture.Manifest.Id, runtime.NativeTransitions).TryDispatch(
                        new NativeSceneRequest("PendingFailedWorld", false, "failed-startup-drain"), nativeScene);
                    Assert(dispatched.Succeeded, "failed package starts a real executor-owned native scene operation");
                }
                if (failedEntry)
                {
                    loaded.Lifetime.StoppingToken.Register(() =>
                    {
                        var lifecycle = runtime.NativeTransitions.TryReserve(new NativeTransitionOwner("tests.independent", "independent"), "reentrant");
                        reentrantBlocked = runtime.SessionBindings!.Capture().PackageCleanupPending && lifecycle.ErrorCode == ModErrorCode.Conflict;
                        if (lifecycle.TryGetValue(out var reservation)) reservation.Dispose();
                    });
                    throw new InvalidOperationException("synthetic pending-native startup failure");
                }
            }));
            runtime.Load(new[] { fixture.Package });
            Assert(ticket != null && context != null, "the real package OnLoad starts tracked native work");
            if (failedEntry)
            {
                Assert(runtime.GetLoadFailure(fixture.Manifest.Id) != null && context!.Lifetime.IsStopping, "failed entry reports failure and requests cancellation");
                Assert(!events.Contains("package:unload") && !events.Contains("package:resource:dispose"), "failed OnLoad must retain package callbacks and resources until native work drains");
                Assert(reentrantBlocked, "failed cleanup closes atomic and native admission before cancellation callbacks run");
                var pending = runtime.SessionBindings!.Capture();
                Assert(pending.PackageCleanupPending && pending.Profile.Packages.Count == 1 && pending.Contexts.Count == 0, "failed selected owner remains visible while its context drains privately");
                var orchestrator = new GamemodeSessionOrchestrator(runtime.NativeDispatcher, runtime.NativeTransitions,
                    new BoundEnvironment(runtime.SessionBindings), runtime.RuntimeOwnershipId);
                runtime.AttachSessionLifecycle(orchestrator, runtime.NativeDispatcher);
                var plan = LaunchResolver.Resolve(profile, new LaunchRequest(fixture.Manifest.Contributions!.LaunchTargets[0].Id)).Plan!;
                var launch = orchestrator.StartAsync(plan.Descriptor, "failed-drain-launch");
                PumpBinding(runtime.NativeDispatcher, launch);
                Assert(launch.Result.ErrorCode == ModErrorCode.Conflict, "launch reports Busy ahead of unavailable target errors while native cleanup drains");
                var direct = runtime.NativeTransitions.RequestTransition(new SceneTransitionRequest("tests.independent", "Other", SceneTransitionPriority.UserInitiated));
                Assert(!direct.Approved && direct.ErrorCode == ModErrorCode.Conflict, "installing the session gate cannot reopen direct scene admission during failed cleanup");
            }
            var shutdown = runtime.UnloadAllAsync();
            runtime.NativeDispatcher.Drain();
            Assert(!shutdown.IsCompleted && !events.Contains("package:unload") && !events.Contains("package:resource:dispose"), "package unload and owned services wait for outstanding native work");
            ticket!.Complete(new InvalidOperationException("synthetic late native completion failure"));
            if (nativeScene != null)
            {
                var observationEnds = DateTime.UtcNow.AddMilliseconds(100);
                while (DateTime.UtcNow < observationEnds) { runtime.NativeDispatcher.Drain(); Thread.Sleep(1); }
                Assert(!events.Contains("package:unload") && runtime.SessionBindings!.Capture().PackageCleanupPending, "failed owner cleanup also retains its own still-running native scene after asset completion");
                nativeScene.Completion!.NativeCompleted(OperationResult<SceneSnapshot>.Failure(ModErrorCode.External, "synthetic late native scene failure"));
            }
            PumpBinding(runtime.NativeDispatcher, shutdown);
            Assert(events.Count(value => value == "package:unload") == 1 && events.Count(value => value == "package:resource:dispose") == 1, "native failure still attempts every package cleanup exactly once");
            Assert(runtime.ShutdownFailures.Any(error => error.ToString().Contains("synthetic late native completion failure", StringComparison.Ordinal)), "late native failure survives into runtime shutdown diagnostics");
            if (nativeScene != null) Assert(runtime.ShutdownFailures.Any(error => error.ToString().Contains("synthetic late native scene failure", StringComparison.Ordinal)), "failed owner preserves late scene failure independently from late asset failure");
            Assert(!runtime.SessionBindings!.Capture().PackageCleanupPending, "drained failed contexts release the atomic cleanup gate");
        }

        private static void TestNativeAdmissionComposition(SceneCoordinator coordinator)
        {
            var runtimeBusy = true;
            coordinator.SetRuntimeCleanupAdmissionGate(() => runtimeBusy);
            coordinator.SetSessionAdmissionGate(() => false);
            var direct = coordinator.RequestTransition(new SceneTransitionRequest("tests.owner", "Scene", SceneTransitionPriority.UserInitiated));
            if (direct.Approved) direct.Claim!.Dispose();
            var lifecycle = coordinator.TryReserve(new NativeTransitionOwner("tests.owner", "owner"), "launch");
            if (lifecycle.TryGetValue(out var reservation)) reservation.Dispose();
            Assert(!direct.Approved && direct.ErrorCode == ModErrorCode.Conflict && lifecycle.ErrorCode == ModErrorCode.Conflict,
                "runtime cleanup independently blocks both direct and lifecycle native reservations");
            runtimeBusy = false;
            coordinator.SetSessionAdmissionGate(() => true);
            direct = coordinator.RequestTransition(new SceneTransitionRequest("tests.owner", "Scene", SceneTransitionPriority.UserInitiated));
            lifecycle = coordinator.TryReserve(new NativeTransitionOwner("tests.owner", "owner"), "launch");
            Assert(!direct.Approved && lifecycle.TryGetValue(out reservation), "clearing runtime cleanup retains the existing session admission semantics");
            reservation!.Dispose();
            coordinator.SetSessionAdmissionGate(() => false);
        }
    }
}
