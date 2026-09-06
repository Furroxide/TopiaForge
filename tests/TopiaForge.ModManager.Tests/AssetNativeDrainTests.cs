using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class AssetNativeDrainTests
    {
        internal static void Run()
        {
            CancelledBundleRetainsNativeOwnership();
            StoppedOwnerDisposesLatePrefab();
            LateCleanupFailureSurvivesCancellation();
            StartedRequestFailureStillDrains();
            BackingBundleRemainsPinned();
            SuccessfulHandleBelongsToLifetime();
            StoppedOwnerAllocatesNothing();
            ExpectedFailurePreservesCode();
            CancelledNativeFailureRemainsVisible();
            CancellationDuringHandoffCleansOnce();
            ObservationFailureRetainsNativeOwnership();
            OwnerStopDuringHandoffIsCancellation(false);
            OwnerStopDuringHandoffIsCancellation(true);
            Console.WriteLine("AssetNativeDrainTests passed.");
        }
        private static void CancelledBundleRetainsNativeOwnership()
        {
            using var lifetime = new Lifetime(); using var cancel = new CancellationTokenSource();
            var request = new Request();
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "bundle", () => request, cancel.Token);
            Assert(request.HasStarted && !caller.IsCompleted, "A bundle request must be enrolled before its native work begins.");
            cancel.Cancel();
            Assert(caller.Result.ErrorCode == ModErrorCode.Cancelled, "Caller cancellation must return promptly.");
            var drain = lifetime.DrainNativeWorkAsync();
            Assert(!drain.IsCompleted, "Cancelled native bundle work must retain owner admission.");
            request.IsDone = true; AssetNativeRequestPump.Poll(); Await(drain);
            Assert(request.Result.Disposals == 1 && request.Disposals == 1, "Late bundle and request cleanup must finish before drain.");
        }
        private static void StoppedOwnerDisposesLatePrefab()
        {
            using var lifetime = new Lifetime(); var request = new Request();
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "prefab", () => request, default);
            lifetime.Stop(); var drain = lifetime.DrainNativeWorkAsync();
            Assert(caller.Result.ErrorCode == ModErrorCode.Cancelled && !drain.IsCompleted, "Owner stop cancels the caller, but awaits the prefab engine request.");
            request.IsDone = true; AssetNativeRequestPump.Poll(); Await(drain);
            Assert(request.Result.Disposals == 1, "A stopped owner must not publish a late prefab.");
        }
        private static void LateCleanupFailureSurvivesCancellation()
        {
            using var lifetime = new Lifetime(); using var cancel = new CancellationTokenSource();
            var request = new Request { ThrowDispose = true }; request.Result.ThrowDispose = true;
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "throwing-bundle", () => request, cancel.Token);
            cancel.Cancel(); var drain = lifetime.DrainNativeWorkAsync();
            request.IsDone = true; AssetNativeRequestPump.Poll();
            var failure = Failure(drain);
            Assert(caller.Result.ErrorCode == ModErrorCode.Cancelled && failure.Contains("late-handle") && failure.Contains("request-release"),
                "Both late cleanup failures must survive an earlier caller cancellation.");
            Assert(request.Disposals == 1, "Throwing handle cleanup cannot skip request cleanup.");
        }
        private static void StartedRequestFailureStillDrains()
        {
            using var lifetime = new Lifetime(); var request = new Request { ThrowAfterStart = true };
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "indeterminate-start", () => request, default);
            var drain = lifetime.DrainNativeWorkAsync();
            Assert(caller.Result.ErrorCode == ModErrorCode.External && !drain.IsCompleted, "An adapter failure after native allocation cannot fake completion.");
            request.IsDone = true; AssetNativeRequestPump.Poll();
            Assert(Failure(drain).Contains("after-start") && request.Result.Disposals == 1, "Polling must clean a late result after adapter startup failed.");
        }
        private static void BackingBundleRemainsPinned()
        {
            using var lifetime = new Lifetime(); var releases = 0;
            var bundle = new AssetNativeResource<object>(new object(), _ => releases++);
            var pin = bundle.Acquire(); var request = new Request { OnDispose = pin.Dispose };
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "prefab", () => request, default);
            bundle.Dispose();
            Assert(!bundle.IsAlive && releases == 0, "Disposing a bundle must not unload backing data during native prefab work.");
            lifetime.Stop(); request.IsDone = true; AssetNativeRequestPump.Poll(); Await(lifetime.DrainNativeWorkAsync());
            Assert(releases == 1 && caller.Result.ErrorCode == ModErrorCode.Cancelled, "Bundle unload must follow native prefab completion exactly once.");
        }
        private static void SuccessfulHandleBelongsToLifetime()
        {
            using var lifetime = new Lifetime(); var request = new Request { IsDone = true };
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "ready", () => request, default);
            Assert(caller.Result.Succeeded && request.Result.Disposals == 0, "A ready asset must remain owned after publication.");
            Await(lifetime.DrainNativeWorkAsync()); lifetime.Dispose();
            Assert(request.Result.Disposals == 1 && request.Disposals == 1, "Lifetime owns the successful handle; native request cleanup finishes independently.");
        }
        private static void StoppedOwnerAllocatesNothing()
        {
            using var lifetime = new Lifetime(); lifetime.Stop(); var created = false;
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "stopped", () => { created = true; return new Request(); }, default);
            Assert(!created && caller.Result.ErrorCode == ModErrorCode.Cancelled, "A stopped lifetime must reject before any native allocation.");
        }
        private static void ExpectedFailurePreservesCode()
        {
            using var lifetime = new Lifetime(); var request = new Request { IsDone = true, Missing = true };
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "missing-prefab", () => request, default);
            Assert(caller.Result.ErrorCode == ModErrorCode.NotFound, "Native ownership must preserve the SDK's expected operation error codes.");
            Await(lifetime.DrainNativeWorkAsync());
        }
        private static void CancelledNativeFailureRemainsVisible()
        {
            using var lifetime = new Lifetime(); using var cancel = new CancellationTokenSource();
            var request = new Request { Missing = true };
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "late-missing-prefab", () => request, cancel.Token);
            cancel.Cancel(); request.IsDone = true; AssetNativeRequestPump.Poll();
            Assert(caller.Result.ErrorCode == ModErrorCode.Cancelled
                && Failure(lifetime.DrainNativeWorkAsync()).Contains("missing-prefab"),
                "A native failure hidden by earlier cancellation must remain in terminal cleanup evidence.");
        }
        private static void CancellationDuringHandoffCleansOnce()
        {
            using var lifetime = new Lifetime(); using var cancel = new CancellationTokenSource();
            lifetime.OnTrack = cancel.Cancel; var request = new Request { IsDone = true };
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "handoff", () => request, cancel.Token);
            Assert(caller.Result.ErrorCode == ModErrorCode.Cancelled && request.Result.Disposals == 1,
                "Cancellation while transferring a result must clean it before the native ticket retires.");
            Await(lifetime.DrainNativeWorkAsync()); lifetime.Dispose();
            Assert(request.Result.Disposals == 1, "Rejected late result cannot be disposed twice through its lifetime.");
        }
        private static void OwnerStopDuringHandoffIsCancellation(bool throwingCleanup)
        {
            using var lifetime = new Lifetime(); lifetime.BeforeTrack = lifetime.Stop;
            var request = new Request { IsDone = true }; request.Result.ThrowDisposed = throwingCleanup;
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "owner-handoff", () => request, default);
            Assert(caller.Result.ErrorCode == ModErrorCode.Cancelled && request.Result.Disposals == 1,
                "Owner stop during lifetime admission must cancel and clean the returned handle once.");
            if (throwingCleanup) Assert(Failure(lifetime.DrainNativeWorkAsync()).Contains("late-disposed-handle"),
                "A real ObjectDisposedException thrown by cleanup cannot be mistaken for normal lifetime rejection.");
            else Await(lifetime.DrainNativeWorkAsync());
        }
        private static void ObservationFailureRetainsNativeOwnership()
        {
            using var lifetime = new Lifetime(); var request = new Request { ThrowObservation = true };
            var caller = AssetNativeOperation<Handle>.Start(lifetime, "observation", () => request, default);
            var drain = lifetime.DrainNativeWorkAsync();
            Assert(caller.Result.ErrorCode == ModErrorCode.External && !drain.IsCompleted,
                "An observation exception must not retire indeterminate engine work.");
            request.ThrowObservation = false; request.IsDone = true; AssetNativeRequestPump.Poll();
            Assert(Failure(drain).Contains("observation-failure") && request.Result.Disposals == 1,
                "The process pump must still clean the late result and retain the observation failure.");
        }
        private static void Await(Task task) { if (!task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Native test drain did not finish."); }
        private static string Failure(Task task)
        { try { Await(task); } catch (AggregateException error) { return error.ToString(); } throw new InvalidOperationException("Expected native cleanup failure."); }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private sealed class Request : IAssetNativeRequest<Handle>
        {
            internal Handle Result = new Handle(); internal bool ThrowAfterStart; internal bool ThrowDispose; internal bool Missing; internal bool ThrowObservation;
            internal Action? OnDispose; internal int Disposals;
            public bool HasStarted { get; private set; }
            private bool done;
            public bool IsDone { get { if (ThrowObservation) throw new InvalidOperationException("observation-failure"); return done; } set { done = value; } }
            public void Start() { HasStarted = true; if (ThrowAfterStart) throw new InvalidOperationException("after-start"); }
            public OperationResult<Handle> ReadResult() => Missing
                ? OperationResult<Handle>.Failure(ModErrorCode.NotFound, "missing-prefab") : OperationResult<Handle>.Success(Result);
            public void Dispose() { Disposals++; OnDispose?.Invoke(); if (ThrowDispose) throw new InvalidOperationException("request-release"); }
        }
        private sealed class Handle : IDisposable
        {
            internal int Disposals; internal bool ThrowDispose; internal bool ThrowDisposed;
            public void Dispose() { Disposals++; if (ThrowDisposed) throw new ObjectDisposedException("late-disposed-handle"); if (ThrowDispose) throw new InvalidOperationException("late-handle"); }
        }
        private sealed class Lifetime : IModLifetime, IInternalNativeWorkLifetime
        {
            private readonly CancellationTokenSource stopping = new CancellationTokenSource();
            private readonly AssetNativeWorkTracker work = new AssetNativeWorkTracker();
            internal Action? OnTrack; internal Action? BeforeTrack;
            private readonly List<IDisposable> resources = new List<IDisposable>();
            public bool IsStopping => stopping.IsCancellationRequested;
            public CancellationToken StoppingToken => stopping.Token;
            public IDisposable Track(IDisposable resource) { BeforeTrack?.Invoke(); if (IsStopping) { resource.Dispose(); throw new ObjectDisposedException("owner"); } resources.Add(resource); OnTrack?.Invoke(); return resource; }
            public IDisposable Defer(Action action) => throw new NotSupportedException();
            public AssetNativeWorkTicket RegisterNativeWork(string description) => work.Begin(description);
            public Task DrainNativeWorkAsync() => work.DrainAsync();
            internal void Stop() { work.Stop(); stopping.Cancel(); }
            public void Dispose() { Stop(); foreach (var resource in resources) resource.Dispose(); resources.Clear(); }
        }
    }
}
