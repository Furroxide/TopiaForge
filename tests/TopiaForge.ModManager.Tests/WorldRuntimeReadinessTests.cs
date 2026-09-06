using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldRuntimeReadinessTests
    {
        internal static void Run()
        {
            using var test = new Fixture();
            var preparing = test.Service.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.SceneReplacement), default);
            Assert(!preparing.IsCompleted, "Preparing waits for native completion.");
            test.Load.Complete(); test.Pump();
            Assert(!preparing.IsCompleted, "A loaded scene without a real default spawn must remain unready.");
            test.Load.DefaultSpawn = Spawn; test.Clock.Advance(); test.Wait(preparing);
            var prepared = preparing.Result.Value!;
            Assert(prepared.Scene.InstanceId == -17 && prepared.NativeDefaultSpawn == Spawn, "Actual scene handle and resolved default survive preparation.");
            test.Load.Markers = Array.Empty<TransformState>();
            var missing = prepared.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.AuthoredMarker, "Spawn"), null, null, default);
            test.Wait(missing);
            Assert(missing.Result.ErrorCode == ModErrorCode.NotFound && test.Load.Applied == 0, "Missing markers never fall back to a default spawn.");
            prepared.Dispose();
            Assert(test.Load.Disposals == 1, "Failed readiness still owns and releases preparation.");
            TestMarkersAndPlacement();
            TestCancellationDrainsNative();
            TestTimeoutAndOwnerStop();
            TestCleanupFailure();
            TestForeignContentRoot();
            TestAdditiveAdmission();
            TestOwnerStopsDuringAllocation();
            TestDrainFailureAttemptsCleanup();
            TestLateNativeFailures();
            TestNativeFailureAfterArrival();
            TestProviderRetainsLateFailure();
            TestAdapterThrowRemainsTerminal();
            Console.WriteLine("WorldRuntimeReadinessTests passed.");
        }

        private static void TestMarkersAndPlacement()
        {
            using var test = new Fixture();
            test.Load.RequiresDispatch = false; test.Load.DefaultSpawn = Spawn;
            var task = test.Service.PrepareAsync(NativeWorldLoadRequest.Scene("Scene", WorldLoadTransition.AdditiveArena), default);
            test.Wait(task); var prepared = task.Result.Value!;
            test.Load.Markers = new[] { Spawn, Spawn };
            var duplicate = prepared.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.AuthoredMarker, "Spawn"), null, null, default);
            test.Wait(duplicate);
            Assert(duplicate.Result.ErrorCode == ModErrorCode.Conflict && test.Load.Applied == 0, "Duplicate markers fail instead of choosing the first.");
            prepared.Dispose();
            using var valid = new Fixture();
            valid.Load.RequiresDispatch = false; valid.Load.DefaultSpawn = Spawn;
            var start = valid.Service.PrepareAsync(NativeWorldLoadRequest.Scene("Scene", WorldLoadTransition.AdditiveArena), default);
            valid.Wait(start); valid.Load.PlayerReady = false;
            var finish = start.Result.Value!.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
            Assert(!finish.IsCompleted, "Scene/default spawn alone cannot establish player readiness.");
            valid.Load.PlayerReady = true; valid.Clock.Advance(); valid.Wait(finish);
            Assert(finish.Result.Succeeded && finish.Result.Value!.Spawn == Spawn && valid.Load.Applied == 1,
                "Readiness records the actual applied position and rotation after player readiness.");
            start.Result.Value.Dispose();
        }

        private static void TestCancellationDrainsNative()
        {
            using var test = new Fixture(); using var cancel = new CancellationTokenSource();
            var task = test.Service.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.SceneReplacement), cancel.Token);
            cancel.Cancel(); test.Pump();
            Assert(test.Coordinator.IsSceneBusy && test.Load.Disposals == 0 && !task.IsCompleted,
                "Caller cancellation must retain the native operation and arrival observer through drain.");
            test.Load.DefaultSpawn = Spawn; test.Load.Complete(); test.Wait(task);
            Assert(task.Result.ErrorCode == ModErrorCode.Cancelled && test.Load.Disposals == 1 && !test.Coordinator.IsSceneBusy,
                "Late native completion releases ownership without returning cancelled content.");
        }

        private static void TestTimeoutAndOwnerStop()
        {
            using var test = new Fixture();
            test.Load.RequiresDispatch = false;
            var task = test.Service.PrepareAsync(NativeWorldLoadRequest.Scene("Scene", WorldLoadTransition.AdditiveArena), default);
            test.Clock.Advance(TimeSpan.FromSeconds(31)); test.Wait(task);
            Assert(task.Result.ErrorCode == ModErrorCode.TimedOut && test.Load.Disposals == 1,
                "A readiness deadline is a failure, never a replacement for real spawn evidence.");
            test.Lifetime.BeginStop();
            var stopped = test.Service.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.SceneReplacement), default);
            test.Wait(stopped);
            Assert(stopped.Result.ErrorCode == ModErrorCode.Cancelled && test.Backend.Created == 1, "Stopped facades cannot allocate another preparation.");
        }

        private static void TestCleanupFailure()
        {
            var test = new Fixture(); test.Load.RequiresDispatch = false; test.Load.DefaultSpawn = Spawn;
            var task = test.Service.PrepareAsync(NativeWorldLoadRequest.Scene("Scene", WorldLoadTransition.AdditiveArena), default);
            test.Wait(task); test.Load.ThrowOnDispose = true;
            var surfaced = false;
            try { test.Lifetime.Dispose(); } catch (Exception) { surfaced = true; }
            Assert(surfaced && test.Load.Disposals == 1, "Owner cleanup must aggregate preparation disposer failures instead of losing a faulted cleanup task.");
            test.Dispatcher.Drain(); test.Dispatcher.Dispose();
        }

        private static void TestForeignContentRoot()
        {
            using var test = new Fixture(); test.Load.RequiresDispatch = false; test.Load.DefaultSpawn = Spawn;
            var task = test.Service.PrepareAsync(NativeWorldLoadRequest.Scene("Scene", WorldLoadTransition.AdditiveArena), default);
            test.Wait(task); test.Load.ContentValid = false;
            var finish = task.Result.Value!.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
            test.Wait(finish);
            Assert(finish.Result.ErrorCode == ModErrorCode.InvalidArgument && test.Load.Applied == 0,
                "Provider-default readiness must validate content ownership before applying player spawn.");
        }

        private static void TestAdditiveAdmission()
        {
            using var busy = new Fixture(); busy.Load.RequiresDispatch = false; busy.Load.DefaultSpawn = Spawn;
            var reserved = busy.Coordinator.TryReserve(new NativeTransitionOwner("other", "other"), "other-load");
            var rejected = busy.Service.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.AdditiveArena), default);
            busy.Wait(rejected);
            Assert(rejected.Result.ErrorCode == ModErrorCode.Conflict, "Additive world preparation cannot bypass an occupied native executor.");
            reserved.Value!.Dispose(); busy.Pump();
            using var denied = new Fixture(new Deny()); denied.Load.RequiresDispatch = false; denied.Load.DefaultSpawn = Spawn;
            var unauthorized = denied.Service.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.AdditiveArena), default);
            denied.Wait(unauthorized);
            Assert(unauthorized.Result.ErrorCode == ModErrorCode.NotAuthoritative, "Additive content must obey the same multiplayer authority policy.");
            using var ready = new Fixture(); ready.Load.RequiresDispatch = false; ready.Load.DefaultSpawn = Spawn;
            var preparation = ready.Service.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.AdditiveArena), default);
            ready.Wait(preparation);
            var contender = new OwnerSceneTransitionService("other", ready.Coordinator);
            Assert(contender.Acquire("Scene", false, "competing request").ErrorCode == ModErrorCode.Conflict,
                "Native admission remains owned while content and player readiness are unfinished.");
            var finish = preparation.Result.Value!.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
            ready.Wait(finish);
            var after = contender.Acquire("Scene", false, "after readiness");
            Assert(after.Succeeded, "Readiness releases its child admission without retaining ownership while running.");
            after.Value!.Dispose(); ready.Pump();
        }
        private sealed class Deny : ISceneTransitionAuthorityPolicy
        {
            public SceneTransitionAuthorityDecision Evaluate(SceneTransitionRequest request) => SceneTransitionAuthorityDecision.Deny("Synthetic client has no scene authority.");
        }

        private static void TestOwnerStopsDuringAllocation()
        {
            using var test = new Fixture();
            test.Backend.OnCreate = test.Lifetime.BeginStop;
            var task = test.Service.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.SceneReplacement), default);
            test.Wait(task);
            Assert(task.Result.ErrorCode == ModErrorCode.Cancelled && test.Load.Disposals == 1 && !test.Coordinator.IsSceneBusy,
                "Owner cancellation during allocation must dispose the unpublished preparation and report cancellation without dispatch.");
        }

        private static void TestDrainFailureAttemptsCleanup()
        {
            using var dispatcher = new HostDispatcher();
            var load = new Load { ThrowOnDispose = true };
            var preparation = new WorldPreparation(load, default, default, new Clock(), dispatcher);
            preparation.Attach(new FaultedDrain());
            var task = preparation.CloseAsync();
            Exception? failure = null;
            try { HostDispatcherTests.Pump(dispatcher, task); } catch (Exception error) { failure = error; }
            Assert(load.Disposals == 1 && failure != null && failure.ToString().Contains("Synthetic drain failure", StringComparison.Ordinal)
                && failure.ToString().Contains("Synthetic preparation cleanup failure", StringComparison.Ordinal),
                "A failing drain must not skip independent cleanup or hide the disposer failure.");
        }
        private sealed class FaultedDrain : IInternalNativeSceneOperation
        {
            public Task<OperationResult<SceneSnapshot>> Completion => Task.FromResult(OperationResult<SceneSnapshot>.Success(new SceneSnapshot("Scene", true, true)));
            public Task<OperationResult<SceneSnapshot>> NativeCompletion => Completion;
            public Task NativeDrained { get; } = Task.FromException(new InvalidOperationException("Synthetic drain failure."));
            public NativeSceneDispatchStatus DispatchStatus => NativeSceneDispatchStatus.Dispatched;
        }

        private static void TestLateNativeFailures()
        {
            foreach (var failures in new[] { (Native: false, Managed: true), (Native: true, Managed: false), (Native: true, Managed: true) })
            {
                var coordinator = new SceneCoordinator(); using var cancellation = new CancellationTokenSource();
                IInternalNativeSceneCompletion? completion = null;
                var dispatched = new OwnerSceneTransitionService("world", coordinator).TryDispatch(
                    new NativeSceneRequest("Scene", false, "late failure"), new DelegateNativeSceneDispatch(sink =>
                    { completion = sink; sink.RequireManagedCompletion(); return NativeSceneDispatchStatus.Dispatched; }), cancellation.Token);
                var operation = dispatched.Value!; cancellation.Cancel();
                Assert(operation.Completion.Result.ErrorCode == ModErrorCode.Cancelled && !operation.NativeCompletion.IsCompleted,
                    "Caller cancellation must not masquerade as a terminal native result.");
                completion!.ManagedCompleted(failures.Managed ? OperationResult<bool>.Failure(ModErrorCode.External, "late managed failure") : OperationResult<bool>.Success(true));
                completion.NativeCompleted(failures.Native ? OperationResult<SceneSnapshot>.Failure(ModErrorCode.External, "late native failure")
                    : OperationResult<SceneSnapshot>.Success(new SceneSnapshot("Scene", true, true)));
                Assert(operation.NativeDrained.IsCompleted && operation.NativeCompletion.Result.ErrorCode == ModErrorCode.External
                    && (!failures.Native || operation.NativeCompletion.Result.ErrorMessage.Contains("late native failure", StringComparison.Ordinal))
                    && (!failures.Managed || operation.NativeCompletion.Result.ErrorMessage.Contains("late managed failure", StringComparison.Ordinal))
                    && operation.Completion.Result.ErrorCode == ModErrorCode.Cancelled,
                    "Terminal native and managed failures must survive drain without rewriting the earlier caller acknowledgement.");
            }
        }

        private static void TestNativeFailureAfterArrival()
        {
            var coordinator = new SceneCoordinator(); IInternalNativeSceneCompletion? completion = null;
            var dispatched = new OwnerSceneTransitionService("world", coordinator).TryDispatch(new NativeSceneRequest("Scene", false, "arrival then failure"),
                new DelegateNativeSceneDispatch(sink => { completion = sink; sink.RequireManagedCompletion(); return NativeSceneDispatchStatus.Dispatched; }));
            coordinator.NotifySceneArrived(new SceneSnapshot("Scene", true, true));
            completion!.NativeCompleted(OperationResult<SceneSnapshot>.Failure(ModErrorCode.External, "native failed after arrival"));
            completion.ManagedCompleted(OperationResult<bool>.Success(true));
            Assert(dispatched.Value!.NativeCompletion.Result.ErrorCode == ModErrorCode.External,
                "An actual native failure while managed work is pending cannot be discarded merely because the scene arrived first.");
        }
        private static void TestProviderRetainsLateFailure()
        {
            var test = new Fixture(); using var cancel = new CancellationTokenSource();
            test.Load.RequireManaged = true;
            var task = test.Service.PrepareAsync(NativeWorldLoadRequest.OpenSandbox(WorldLoadTransition.SceneReplacement), cancel.Token);
            cancel.Cancel(); test.Pump(); test.Load.Complete(); test.Load.FailManaged(); test.Wait(task);
            Assert(task.Result.ErrorCode == ModErrorCode.External && task.Result.ErrorMessage.Contains("Cancelled", StringComparison.Ordinal)
                && task.Result.ErrorMessage.Contains("late provider loader failure", StringComparison.Ordinal),
                "The provider must retain its cancelled caller outcome and subsequent native loader fault.");
            var surfaced = false;
            try { test.Lifetime.Dispose(); } catch (Exception failure) { surfaced = failure.ToString().Contains("late provider loader failure", StringComparison.Ordinal); }
            Assert(surfaced && test.Load.Disposals == 1, "The retained late fault must reach session-owned cleanup even if startup cancellation ignores its returned value.");
            test.Dispatcher.Drain(); test.Dispatcher.Dispose();
        }

        private static void TestAdapterThrowRemainsTerminal()
        {
            var coordinator = new SceneCoordinator();
            var dispatched = new OwnerSceneTransitionService("world", coordinator).TryDispatch(new NativeSceneRequest("Scene", false, "throwing adapter"),
                new DelegateNativeSceneDispatch(_ => throw new InvalidOperationException("adapter exploded")));
            Assert(dispatched.Value!.Completion.Result.ErrorCode == ModErrorCode.External && coordinator.IsSceneBusy,
                "An indeterminate adapter exception fails its caller while retaining native ownership.");
            coordinator.NotifySceneArrived(new SceneSnapshot("Scene", true, true));
            Assert(dispatched.Value.NativeCompletion.Result.ErrorCode == ModErrorCode.External
                && dispatched.Value.NativeCompletion.Result.ErrorMessage.Contains("adapter exploded", StringComparison.Ordinal),
                "Late native success cannot erase the dispatch adapter exception from the terminal outcome.");
        }

        internal static readonly TransformState Spawn = new TransformState(new Vec3(3, 5, 7), new Quat(0, 1, 0, 1), new Vec3(1, 1, 1));
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private sealed class Fixture : IDisposable
        {
            internal readonly HostDispatcher Dispatcher = new HostDispatcher();
            internal readonly OwnerModLifetime Lifetime = new OwnerModLifetime();
            internal readonly Clock Clock = new Clock();
            internal readonly Load Load = new Load();
            internal readonly Backend Backend;
            internal readonly SceneCoordinator Coordinator;
            internal readonly OwnerWorldRuntimeService Service;
            internal Fixture(ISceneTransitionAuthorityPolicy? authority = null)
            {
                Coordinator = new SceneCoordinator(authorityPolicy: authority, dispatcher: Dispatcher); Backend = new Backend(Load);
                Service = new OwnerWorldRuntimeService(Lifetime, new OwnerSceneTransitionService("world.owner", Coordinator, Lifetime.StoppingToken),
                    Backend, Clock, Dispatcher);
            }
            internal void Pump() { for (var i = 0; i < 10; i++) { Dispatcher.Drain(); Thread.Sleep(1); } }
            internal void Wait(Task task) => HostDispatcherTests.Pump(Dispatcher, task);
            public void Dispose() { Lifetime.Dispose(); Dispatcher.Drain(); Dispatcher.Dispose(); }
        }
        private sealed class Backend : IWorldRuntimeBackend
        {
            private readonly Load load; internal int Created; internal Action? OnCreate;
            internal Backend(Load load) { this.load = load; }
            public OperationResult<IWorldNativeLoad> CreateLoad(NativeWorldLoadRequest request) { Created++; OnCreate?.Invoke(); return OperationResult<IWorldNativeLoad>.Success(load); }
            public OperationResult<IReadOnlyList<NativeWorldEntry>> Discover(NativeWorldSource source, int maximumResults) =>
                OperationResult<IReadOnlyList<NativeWorldEntry>>.Success(Array.Empty<NativeWorldEntry>());
        }
        private sealed class Load : IWorldNativeLoad
        {
            private IInternalNativeSceneCompletion? completion;
            internal TransformState? DefaultSpawn;
            internal IReadOnlyList<TransformState> Markers = new[] { Spawn };
            internal bool PlayerReady = true;
            internal bool ContentValid = true;
            internal bool RequireManaged;
            internal int Disposals; internal int Applied; internal bool ThrowOnDispose;
            public string ExpectedSceneName => "Scene";
            public bool RequiresDispatch { get; set; } = true;
            public NativeSceneDispatchStatus Begin(IInternalNativeSceneCompletion value) { completion = value; if (RequireManaged) value.RequireManagedCompletion(); return NativeSceneDispatchStatus.Dispatched; }
            internal void FailManaged() => completion!.ManagedCompleted(OperationResult<bool>.Failure(ModErrorCode.External, "late provider loader failure"));
            internal void Complete() => completion!.NativeCompleted(OperationResult<SceneSnapshot>.Success(new SceneSnapshot("Scene", true, true)));
            public OperationResult<WorldSceneIdentity> CaptureScene() => OperationResult<WorldSceneIdentity>.Success(new WorldSceneIdentity(-17, "Scene"));
            public OperationResult<bool> ValidateScene() => OperationResult<bool>.Success(true);
            public OperationResult<bool> ValidateContent(IEntity? root) => ContentValid ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "foreign content root");
            public OperationResult<TransformState>? ReadDefaultSpawn() => DefaultSpawn.HasValue ? OperationResult<TransformState>.Success(DefaultSpawn.Value) : null;
            public OperationResult<IReadOnlyList<TransformState>> FindSpawnMarkers(IEntity? root, string marker) => OperationResult<IReadOnlyList<TransformState>>.Success(Markers);
            public OperationResult<TransformState>? ApplySpawn(TransformState spawn)
            { if (!PlayerReady) return null; Applied++; return OperationResult<TransformState>.Success(spawn); }
            public void Dispose() { Disposals++; if (ThrowOnDispose) throw new InvalidOperationException("Synthetic preparation cleanup failure."); }
        }
        private sealed class Clock : IWorldRuntimeClock
        {
            private TaskCompletionSource<OperationResult<bool>> frame = NewFrame();
            public DateTime UtcNow { get; private set; } = DateTime.UtcNow;
            public Task<OperationResult<bool>> NextFrameAsync(CancellationToken token) => frame.Task;
            internal void Advance(TimeSpan? time = null)
            { UtcNow += time ?? TimeSpan.FromMilliseconds(16); var previous = frame; frame = NewFrame(); previous.TrySetResult(OperationResult<bool>.Success(true)); }
            private static TaskCompletionSource<OperationResult<bool>> NewFrame() => new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
