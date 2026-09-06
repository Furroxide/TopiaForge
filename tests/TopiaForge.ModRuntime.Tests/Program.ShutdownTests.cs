using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestShutdownWaitsForSessionDrain(string root)
        {
            foreach (var fail in new[] { false, true })
            {
                var fixture = NewFixture(root, "shutdown-drain-" + fail, "TopiaForge.ValidTestMod.RuntimeShutdownOrderingMod");
                var runtime = fixture.CreateRuntimeInstance();
                using var dispatcher = new HostDispatcher();
                var sessions = new ControlledSessionShutdown();
                runtime.AttachSessionLifecycle(sessions, dispatcher);
                runtime.Load(new[] { fixture.Package });
                var shutdown = runtime.UnloadAllAsync();
                Assert(!shutdown.IsCompleted && !fixture.GameplayHost.Disposed && runtime.IsLoaded(fixture.Manifest.Id),
                    "Package contexts and gameplay services remain alive while session work drains.");
                AssertTrace(fixture.TracePath, "load", "stopping");
                Assert(ReferenceEquals(shutdown, runtime.UnloadAllAsync()) && sessions.Calls == 1,
                    "Repeated shutdown joins the same barrier instead of invoking cleanup twice.");
                try
                {
                    runtime.Load(new[] { fixture.Package });
                    throw new InvalidOperationException("Runtime accepted loading while shutdown was pending.");
                }
                catch (ObjectDisposedException) { }
                Task.Run(() => sessions.Complete(fail)).GetAwaiter().GetResult();
                var deadline = Stopwatch.StartNew();
                while (!shutdown.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
                {
                    dispatcher.Drain();
                    Thread.Yield();
                }
                Assert(shutdown.IsCompleted, "Worker completion must marshal cleanup back onto the host dispatcher.");
                Assert(shutdown.GetAwaiter().GetResult().Succeeded == !fail,
                    "Session cleanup failure is retained in the final runtime outcome.");
                AssertTrace(fixture.TracePath, "load", "stopping", "unload:stopping", "cleanup");
                Assert(fixture.GameplayHost.Disposed && !runtime.IsLoaded(fixture.Manifest.Id),
                    "All package and host cleanup runs after either successful or failed session drain.");
                Assert(ReferenceEquals(shutdown, runtime.UnloadAllAsync()), "Terminal unload remains idempotent.");
            }
        }

        private static void TestShutdownAttemptsCleanupAfterLoggingAndHostFailures(string root)
        {
            var fixture = NewFixture(root, "shutdown-cleanup-failures", "TopiaForge.ValidTestMod.RuntimeFailingUnloadMod");
            var runtime = fixture.CreateRuntimeInstance();
            runtime.Load(new[] { fixture.Package });
            fixture.Logger.ThrowOnError = true;
            fixture.GameplayHost.ThrowOnDispose = true;
            var shutdown = runtime.UnloadAllAsync();
            Assert(shutdown.IsCompleted && !shutdown.GetAwaiter().GetResult().Succeeded,
                "Failed cleanup completes with an unsuccessful outcome even if logging also throws.");
            Assert(runtime.ShutdownFailures.Count == 2 && fixture.GameplayHost.Disposed,
                "Both package unload and host cleanup failures are retained after attempting all disposal.");
            AssertTrace(fixture.TracePath, "load", "unload", "cleanup");
            Assert(runtime.LoadedModIds.Count == 0, "Failed shutdown releases loaded package ownership.");
        }

        private sealed class ControlledSessionShutdown : IRuntimeSessionShutdown
        {
            private readonly TaskCompletionSource<OperationResult<bool>> completion =
                new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls { get; private set; }
            public Task<OperationResult<bool>> StopOwnerAsync(string packageId) => ShutdownAsync();
            public Task<OperationResult<bool>> ShutdownAsync()
            {
                Calls++;
                return completion.Task;
            }
            public void Complete(bool fail) => completion.SetResult(fail
                ? OperationResult<bool>.Failure(ModErrorCode.External, "Synthetic session disposer failed after drain.")
                : OperationResult<bool>.Success(true));
        }

        private static void TestPartialLoadLoggingFailureDoesNotBlockNextPackage(string root)
        {
            var failing = NewFixture(root, "partial-log-failure", "TopiaForge.ValidTestMod.RuntimeFailingLoadOrderingMod");
            var healthy = NewFixture(root, "partial-log-next", "TopiaForge.SecondTestMod.SceneObserverMod", "TopiaForge.SecondTestMod.dll");
            using var runtime = failing.CreateRuntime();
            failing.Logger.ThrowOnError = true;
            runtime.Load(new[] { failing.Package, healthy.Package });
            Assert(runtime.IsLoaded(healthy.Manifest.Id) && !runtime.IsLoaded(failing.Manifest.Id),
                "A failing diagnostic sink cannot prevent cleanup or loading an independent package.");
        }

        private static void TestPartialLoadCancelsBeforeCleanup(string root)
        {
            var fixture = NewFixture(root, "partial-shutdown-order", "TopiaForge.ValidTestMod.RuntimeFailingLoadOrderingMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });
            AssertTrace(fixture.TracePath, "load", "stopping", "unload:stopping", "cleanup");
            Assert(!runtime.IsLoaded(fixture.Manifest.Id), "Partial startup retains failure, never a loaded package.");
        }

        private static void TestShutdownCancelsBeforeCallbacks(string root)
        {
            var fixture = NewFixture(root, "shutdown-order", "TopiaForge.ValidTestMod.RuntimeShutdownOrderingMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });
            runtime.UnloadAll();
            AssertTrace(fixture.TracePath, "load", "stopping", "unload:stopping", "cleanup");
        }
    }
}
