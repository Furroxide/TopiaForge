using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestProductionDiscovery(TopiaForge.ModManager.ModRuntime runtime, RuntimeBindingRegistry registry,
            Type probe, List<string> events, PackageIdentity package, string name)
        {
            var discovery = new RuntimeWorldDiscovery(registry, runtime.NativeDispatcher);
            var parent = registry.Capture().Contexts[package.Id];
            if (name == "discover-limits") { TestDiscoveryLimits(runtime, registry, package, discovery, events); return; }
            if (name == "discover-timeout") { TestDiscoveryTimeout(runtime, registry, probe, package); return; }
            if (name == "discover-stale") { TestReplacedDiscovery(runtime, registry, probe, package, discovery); return; }
            if (name == "discover-cancel" || name == "discover-cancel-cleanup") { TestIgnoringDiscoveryCancellation(runtime, registry, probe, events, package, discovery, name == "discover-cancel-cleanup"); return; }
            if (name == "discover-worker-cancel" || name == "discover-worker-cancel-callback")
            { TestWorkerDiscoveryCancellation(runtime, registry, probe, package, discovery, name.EndsWith("callback", StringComparison.Ordinal)); return; }
            if (name == "discover-cleanup") probe.GetField("ThrowOnFirstDiscoveryDispose")!.SetValue(null, true);
            if (name == "discover-constructor") probe.GetField("ThrowOnFirstDiscoveryConstruction")!.SetValue(null, true);
            SetDiscoveryHandler(probe, (context, cancellationToken) =>
            {
                Assert(context.MaximumResults == (name == "discover-duplicate" ? 2 : 1), "the bounded discovery limit reaches the real implementation");
                Assert(parent.ActiveChildScopeCount == 1, "each family owns one isolated scope");
                if (context.FamilyId.EndsWith(".family", StringComparison.Ordinal) && name == "discover-failed")
                    throw new InvalidOperationException("synthetic family failure");
                if (context.FamilyId.EndsWith(".family", StringComparison.Ordinal) && name == "discover-bounded")
                    return Task.FromResult(Discovered(context.FamilyId, "one", "two"));
                if (context.FamilyId.EndsWith(".family", StringComparison.Ordinal))
                {
                    if (name == "discover-malformed") return Task.FromResult(Discovered("tests.foreign.family", "one"));
                    if (name == "discover-duplicate") return Task.FromResult(Discovered(context.FamilyId, "same", "same"));
                    if (name == "discover-null-result") return Task.FromResult<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>>(null!);
                    if (name == "discover-null-item") return Task.FromResult(OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>.Success(new DiscoveredWorldDescriptor[] { null! }));
                    if (name == "discover-null")
                    {
                        // A hostile/reflection-created result can violate the SDK factory's null guard.
                        var constructor = typeof(OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                        return Task.FromResult((OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>)constructor.Invoke(new object?[] { true, null, ModErrorCode.None, string.Empty }));
                    }
                }
                return Task.FromResult(Discovered(context.FamilyId, "one"));
            });
            var task = discovery.DiscoverAsync(package, maximumResultsPerFamily: name == "discover-duplicate" ? 2 : 1);
            PumpBinding(runtime.NativeDispatcher, task);
            Assert(task.Result.TryGetValue(out var published) && published, "production discovery publishes the completed package observation");
            var snapshot = registry.Capture();
            var failed = name != "discover-valid";
            Assert(snapshot.Observation.DiscoveredWorlds.Count == (failed ? 1 : 2), "independent valid families survive a failed or malformed family");
            Assert(snapshot.Observation.Availability.Count == (failed ? 1 : 0), "failed family remains visible in availability");
            Assert(snapshot.Observation.Availability.All(value => value.Blocks.All(block => block.Code == LaunchBlockCode.WorldUnavailable)), "family failures use structured WorldUnavailable");
            Assert(parent.ActiveChildScopeCount == 0, "every family scope is disposed before observation publication completes");
            Assert(events.Count(value => value == "discovery:constructor-resource:dispose") == 2, "actual child lifetime reclaims allocations even when a discovery constructor or disposer throws");
            Assert(events.Count(value => value == "discovery:dispose") == (name == "discover-constructor" ? 1 : 2), "every successfully constructed source is disposed");
            Assert(events.Count(value => value.StartsWith("discovery:", StringComparison.Ordinal) && value.EndsWith(":dispose", StringComparison.Ordinal) && value != "discovery:dispose" && value != "discovery:constructor-resource:dispose") == (name == "discover-constructor" ? 1 : 2), "each allocated family resource is reclaimed");
        }

        private static void TestDiscoveryLimits(TopiaForge.ModManager.ModRuntime runtime, RuntimeBindingRegistry registry,
            PackageIdentity package, RuntimeWorldDiscovery discovery, List<string> events)
        {
            var snapshot = registry.Capture();
            foreach (var limit in new[] { 0, 4097 })
            {
                var task = discovery.DiscoverAsync(package, limit);
                PumpBinding(runtime.NativeDispatcher, task);
                Assert(!task.Result.Succeeded && task.Result.ErrorCode == ModErrorCode.InvalidArgument, "invalid budgets fail before discovery enrollment");
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = discovery.DiscoverAsync(package, cancellationToken: cancellation.Token);
            PumpBinding(runtime.NativeDispatcher, cancelled);
            Assert(cancelled.Result.ErrorCode == ModErrorCode.Cancelled, "pre-cancelled discovery does not enroll work");
            Assert(ReferenceEquals(snapshot, registry.Capture()) && events.Count == 0, "invalid limits and pre-cancellation cannot construct sources or replace observations");
        }

        private static void TestDiscoveryTimeout(TopiaForge.ModManager.ModRuntime runtime, RuntimeBindingRegistry registry, Type probe, PackageIdentity package)
        {
            var discovery = new RuntimeWorldDiscovery(registry, runtime.NativeDispatcher, TimeSpan.FromMilliseconds(500));
            var pending = new TaskCompletionSource<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken token = default;
            string? family = null;
            SetDiscoveryHandler(probe, (context, stopping) => { family = context.FamilyId; token = stopping; return pending.Task; });
            var parent = registry.Capture().Contexts[package.Id];
            var task = discovery.DiscoverAsync(package);
            PumpBindingUntil(runtime.NativeDispatcher, () => family != null);
            PumpBindingUntil(runtime.NativeDispatcher, () => token.IsCancellationRequested, "the discovery deadline did not request cancellation");
            Assert(!task.IsCompleted && parent.ActiveChildScopeCount > 0, "timeout requests cancellation but retains an ignoring callback's scope");
            pending.SetResult(Discovered(family!, "late"));
            PumpBinding(runtime.NativeDispatcher, task);
            Assert(!task.Result.Succeeded && task.Result.ErrorCode == ModErrorCode.TimedOut, "timed out callback reports only after returning and draining cleanup");
            Assert(parent.ActiveChildScopeCount == 0 && registry.Capture().Observation.DiscoveredWorlds.Count == 0, "timed out observations never publish");
        }

        private static void TestReplacedDiscovery(TopiaForge.ModManager.ModRuntime runtime, RuntimeBindingRegistry registry,
            Type probe, PackageIdentity package, RuntimeWorldDiscovery discovery)
        {
            var pending = new TaskCompletionSource<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? firstFamily = null;
            SetDiscoveryHandler(probe, (context, cancellationToken) =>
            {
                if (firstFamily == null) { firstFamily = context.FamilyId; return pending.Task; }
                return Task.FromResult(Discovered(context.FamilyId, "new"));
            });
            var old = discovery.DiscoverAsync(package);
            PumpBindingUntil(runtime.NativeDispatcher, () => firstFamily != null);
            var current = discovery.DiscoverAsync(package);
            PumpBinding(runtime.NativeDispatcher, current);
            Assert(current.Result.TryGetValue(out var published) && published, "new discovery attempt publishes");
            pending.SetResult(Discovered(firstFamily!, "old"));
            PumpBinding(runtime.NativeDispatcher, old);
            Assert(old.Result.TryGetValue(out var stale) && !stale, "old completed work is rejected without replacing newer results");
            Assert(registry.Capture().Observation.DiscoveredWorlds.All(value => value.Id.EndsWith(".new", StringComparison.Ordinal)), "newer observation survives stale completion");
            Assert(registry.Capture().Contexts[package.Id].ActiveChildScopeCount == 0, "stale callbacks still clean every scope");
        }

        private static void TestIgnoringDiscoveryCancellation(TopiaForge.ModManager.ModRuntime runtime, RuntimeBindingRegistry registry,
            Type probe, List<string> events, PackageIdentity package, RuntimeWorldDiscovery discovery, bool cleanupFailure = false)
        {
            if (cleanupFailure) probe.GetField("ThrowOnFirstDiscoveryDispose")!.SetValue(null, true);
            using var cancellation = new CancellationTokenSource();
            var pending = new TaskCompletionSource<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? family = null;
            SetDiscoveryHandler(probe, (context, token) => { family = context.FamilyId; return pending.Task; });
            var parent = registry.Capture().Contexts[package.Id];
            var task = discovery.DiscoverAsync(package, cancellationToken: cancellation.Token);
            PumpBindingUntil(runtime.NativeDispatcher, () => family != null);
            cancellation.Cancel();
            var shutdown = runtime.UnloadAllAsync();
            runtime.NativeDispatcher.Drain();
            Assert(!task.IsCompleted && !shutdown.IsCompleted && parent.ActiveChildScopeCount > 0, "ignored cancellation retains child and runtime ownership until callback completion");
            Assert(!events.Contains("package:unload"), "package teardown cannot race an active discovery callback");
            pending.SetResult(Discovered(family!, "late"));
            PumpBinding(runtime.NativeDispatcher, task);
            PumpBinding(runtime.NativeDispatcher, shutdown);
            Assert(!task.Result.Succeeded && task.Result.ErrorCode == (cleanupFailure ? ModErrorCode.External : ModErrorCode.Cancelled), "cancellation reports throwing cleanup distinctly after the ignored callback returns");
            if (cleanupFailure) Assert(task.Result.ErrorMessage.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0 && task.Result.ErrorMessage.IndexOf("cleanup", StringComparison.OrdinalIgnoreCase) >= 0, "failed cancellation cleanup retains both diagnostics");
            Assert(shutdown.Result.Succeeded != cleanupFailure, "Runtime shutdown must report actual discovery cleanup failure while clean cancellation remains successful.");
            if (cleanupFailure) Assert(runtime.ShutdownFailures.Count != 0,
                "Discovery cleanup failure must reach the runtime terminal failure collection.");
            Assert(parent.ActiveChildScopeCount == 0 && registry.Capture().Observation.DiscoveredWorlds.Count == 0, "late result is discarded and scope drained");
            Assert(events.IndexOf("discovery:dispose") < events.IndexOf("package:unload"), "source cleanup precedes package teardown");
        }

        private static void TestWorkerDiscoveryCancellation(TopiaForge.ModManager.ModRuntime runtime, RuntimeBindingRegistry registry,
            Type probe, PackageIdentity package, RuntimeWorldDiscovery discovery, bool throwingCallback)
        {
            using var cancellation = new CancellationTokenSource();
            var pending = new TaskCompletionSource<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbacks = 0; var callbackOnHost = false; string? family = null;
            SetDiscoveryHandler(probe, (context, token) =>
            {
                family = context.FamilyId;
                context.Context.Lifetime.Track(token.Register(() =>
                {
                    callbackOnHost = runtime.NativeDispatcher.IsCurrent; callbacks++;
                    if (throwingCallback) throw new InvalidOperationException("worker discovery cancellation failure");
                }));
                return pending.Task;
            });
            var task = discovery.DiscoverAsync(package, cancellationToken: cancellation.Token);
            PumpBindingUntil(runtime.NativeDispatcher, () => family != null);
            var cancel = Task.Run<Exception?>(() => { try { cancellation.Cancel(); return null; } catch (Exception error) { return error; } });
            PumpBinding(runtime.NativeDispatcher, cancel);
            PumpBindingUntil(runtime.NativeDispatcher, () => callbacks != 0);
            var callerFailure = cancel.Result;
            pending.SetResult(Discovered(family!, "late"));
            PumpBinding(runtime.NativeDispatcher, task);
            var shutdown = runtime.UnloadAllAsync(); PumpBinding(runtime.NativeDispatcher, shutdown);
            Assert(callbackOnHost && callerFailure == null,
                "Worker cancellation must dispatch provider callbacks to the host and retain their failures rather than throw at the caller.");
            Assert(task.Result.ErrorCode == (throwingCallback ? ModErrorCode.External : ModErrorCode.Cancelled)
                && shutdown.Result.Succeeded != throwingCallback,
                "Throwing provider cancellation callbacks must reach both discovery and runtime cleanup outcomes.");
            Assert(registry.Capture().Observation.DiscoveredWorlds.Count == 0, "Cancelled discovery cannot publish a late result.");
        }

        private static void TestDiscoveryScopeConstruction(TopiaForge.ModManager.ModRuntime runtime, RuntimeBindingRegistry registry,
            Fixture fixture, PackageIdentity package, bool throwingCleanup)
        {
            var cleaned = 0; var parent = registry.Capture().Contexts[package.Id];
            fixture.GameplayHost.ScopeCreated = lifetime =>
            {
                lifetime.Defer(() =>
                {
                    cleaned++;
                    if (throwingCleanup) throw new InvalidOperationException("discovery scope cleanup failure");
                });
                throw new InvalidOperationException("handled discovery scope constructor failure");
            };
            var discovery = new RuntimeWorldDiscovery(registry, runtime.NativeDispatcher).DiscoverAsync(package);
            PumpBinding(runtime.NativeDispatcher, discovery);
            Assert(discovery.Result.Succeeded && registry.Capture().Observation.Availability.Count == 2
                && parent.ActiveChildScopeCount == 0 && cleaned == 2,
                "A failed context construction must clean its otherwise-unreturned scope and make each family unavailable.");
            var shutdown = runtime.UnloadAllAsync(); PumpBinding(runtime.NativeDispatcher, shutdown);
            Assert(shutdown.Result.Succeeded != throwingCleanup,
                "Cleanup failures during unreturned discovery scope construction must reach runtime shutdown.");
            var errors = string.Join(";", runtime.ShutdownFailures.Select(error => error.ToString()));
            if (throwingCleanup) Assert(errors.Contains("discovery scope cleanup failure", StringComparison.Ordinal)
                && !errors.Contains("handled discovery scope constructor failure", StringComparison.Ordinal),
                "The cleanup barrier must retain the cleanup fault while the ordinary constructor failure stays handled as unavailable.");
        }

        private static OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>> Discovered(string family, params string[] suffixes) =>
            OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>.Success(suffixes.Select(suffix => new DiscoveredWorldDescriptor(family + "." + suffix, family, suffix)).ToArray());
        private static void SetDiscoveryHandler(Type probe, Func<IWorldDiscoveryContext, CancellationToken, Task<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>>> callback) =>
            probe.GetField("DiscoverHandler")!.SetValue(null, callback);
        private static void PumpBindingUntil(HostDispatcher host, Func<bool> predicate, string message = "discovery callback did not start")
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!predicate() && DateTime.UtcNow < deadline) { host.Drain(); Thread.Sleep(1); }
            Assert(predicate(), message);
            host.Drain();
        }
    }
}
