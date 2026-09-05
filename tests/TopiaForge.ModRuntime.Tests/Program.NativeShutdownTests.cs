using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestShutdownAlwaysWaitsForNativeDrain(string root)
        {
            foreach (var sessionHook in new[] { "none", "throw", "fault", "failure" })
            {
                var fixture = NewFixture(root, "native-shutdown-" + sessionHook,
                    "TopiaForge.ValidTestMod.RuntimeShutdownOrderingMod");
                var runtime = fixture.CreateRuntimeInstance();
                using var sessionDispatcher = new HostDispatcher();
                if (sessionHook != "none")
                    runtime.AttachSessionLifecycle(new FailedNativeDrainSessionHook(sessionHook), sessionDispatcher);
                runtime.Load(new[] { fixture.Package });
                var native = new ShutdownNativeDispatch();
                var admitted = new OwnerSceneTransitionService(fixture.Manifest.Id, runtime.NativeTransitions).TryDispatch(
                    new NativeSceneRequest("PendingWorld", false, "runtime-shutdown-regression"), native);
                Assert(admitted.Succeeded && runtime.NativeTransitions.IsSceneBusy,
                    "The native operation must be accepted before runtime shutdown.");
                native.Completion!.FailCaller(ModErrorCode.Cancelled, "The caller stopped before native completion.");
                Assert(admitted.Value!.Completion.IsCompleted && !admitted.Value.NativeDrained.IsCompleted,
                    "Caller cancellation must leave the underlying native operation pending.");
                var shutdown = runtime.UnloadAllAsync();
                Assert(!shutdown.IsCompleted && !fixture.GameplayHost.Disposed && runtime.IsLoaded(fixture.Manifest.Id),
                    "V5 shutdown must retain package and gameplay ownership until native drain, even without a session hook or after hook failure.");
                AssertTrace(fixture.TracePath, "load", "stopping");
                Assert(ReferenceEquals(shutdown, runtime.UnloadAllAsync()), "Repeated shutdown joins the same native drain barrier.");
                var dispatcher = runtime.NativeDispatcher as HostDispatcher;
                Assert(dispatcher != null, "Injected runtimes must retain a host dispatcher for native completion cleanup.");
                Task.Run(() => native.Completion.NativeCompleted(OperationResult<SceneSnapshot>.Success(
                    new SceneSnapshot("PendingWorld", true, true)))).GetAwaiter().GetResult();
                Assert(!shutdown.IsCompleted && !fixture.GameplayHost.Disposed,
                    "Worker native completion must marshal package teardown instead of disposing on the worker.");
                var deadline = Stopwatch.StartNew();
                while (!shutdown.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
                {
                    dispatcher!.Drain();
                    sessionDispatcher.Drain();
                    Thread.Yield();
                }
                Assert(shutdown.IsCompleted, "Native drain must eventually complete runtime cleanup on the host.");
                Assert(shutdown.Result.Succeeded == (sessionHook == "none"),
                    "Session hook failure remains visible after the mandatory native drain.");
                AssertTrace(fixture.TracePath, "load", "stopping", "unload:stopping", "cleanup");
                Assert(fixture.GameplayHost.Disposed && !runtime.NativeTransitions.IsSceneBusy
                    && !runtime.IsLoaded(fixture.Manifest.Id), "Native drain releases all runtime ownership exactly once.");
                dispatcher!.Drain();
                dispatcher.Dispose();
            }
        }

        private sealed class ShutdownNativeDispatch : IInternalNativeSceneDispatch
        {
            internal IInternalNativeSceneCompletion? Completion;
            public NativeSceneDispatchStatus Begin(IInternalNativeSceneCompletion completion)
            {
                Completion = completion;
                return NativeSceneDispatchStatus.Dispatched;
            }
        }

        private sealed class FailedNativeDrainSessionHook : IRuntimeSessionShutdown
        {
            private readonly string behavior;
            internal FailedNativeDrainSessionHook(string behavior) { this.behavior = behavior; }
            public Task<OperationResult<bool>> StopOwnerAsync(string packageId) => ShutdownAsync();
            public Task<OperationResult<bool>> ShutdownAsync()
            {
                if (behavior == "throw") throw new InvalidOperationException("Session hook failed before returning its task.");
                if (behavior == "fault") return Task.FromException<OperationResult<bool>>(
                    new InvalidOperationException("Session hook task faulted before native drain."));
                return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.External, "Session hook returned a failed outcome."));
            }
        }
    }
}
