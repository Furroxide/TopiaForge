using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativeLoaderDrainTests
    {
        internal static void Run()
        {
            var failures = new List<Exception>();
            foreach (var test in new Action[] { NativeAdmissionBoundaryTests.Run, ArrivalWaitsForManagedCompletion, ManagedCompletionWaitsForArrival,
                SynchronousArrivalWaitsForObserverAttachment, BorrowedAdmissionRechecksAuthority,
                RefusalCannotAuthorizeFallback, TrackerPreservesCurrentLoaderFailure })
                try { test(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        private static void ArrivalWaitsForManagedCompletion()
        {
            var coordinator = new SceneCoordinator();
            IInternalNativeSceneCompletion? sink = null;
            var dispatched = new OwnerSceneTransitionService("world.mod", coordinator).TryDispatch(
                new NativeSceneRequest("World", false, "managed load"), new DelegateNativeSceneDispatch(value =>
                { sink = value; sink.RequireManagedCompletion(); return NativeSceneDispatchStatus.Dispatched; }));
            var operation = dispatched.Value!;
            coordinator.NotifySceneArrived(Scene());
            Assert(coordinator.IsSceneBusy && !operation.Completion.IsCompleted && !operation.NativeDrained.IsCompleted,
                "scene arrival must not retire an attached loader Task that remains pending");
            sink!.ManagedCompleted(OperationResult<bool>.Failure(ModErrorCode.External, "post-scene loader failure"));
            Assert(operation.Completion.Result.ErrorCode == ModErrorCode.External && operation.NativeDrained.IsCompleted
                && !coordinator.IsSceneBusy, "post-scene loader failure is retained and final completion drains once");
        }
        private static void ManagedCompletionWaitsForArrival()
        {
            var coordinator = new SceneCoordinator();
            var dispatched = new OwnerSceneTransitionService("world.mod", coordinator).TryDispatch(
                new NativeSceneRequest("World", false, "managed load"), new DelegateNativeSceneDispatch(sink =>
                { sink.RequireManagedCompletion(); sink.ManagedCompleted(OperationResult<bool>.Success(true)); return NativeSceneDispatchStatus.Dispatched; }));
            Assert(!dispatched.Value!.Completion.IsCompleted && coordinator.IsSceneBusy, "managed completion alone cannot prove scene arrival");
            coordinator.NotifySceneArrived(Scene());
            Assert(dispatched.Value.Completion.Result.Succeeded && !coordinator.IsSceneBusy, "both readiness signals release admission");
        }
        private static void SynchronousArrivalWaitsForObserverAttachment()
        {
            var coordinator = new SceneCoordinator();
            IInternalNativeSceneCompletion? observed = null;
            var dispatched = new OwnerSceneTransitionService("world.mod", coordinator).TryDispatch(
                new NativeSceneRequest("World", false, "synchronous native arrival"), new DelegateNativeSceneDispatch(sink =>
                {
                    observed = sink;
                    coordinator.NotifySceneArrived(Scene());
                    sink.RequireManagedCompletion();
                    return NativeSceneDispatchStatus.Dispatched;
                }));
            Assert(!dispatched.Value!.Completion.IsCompleted, "synchronous scene callback cannot publish success before observer attachment finishes");
            observed!.ManagedCompleted(OperationResult<bool>.Success(true));
            Assert(dispatched.Value.Completion.Result.Succeeded && !coordinator.IsSceneBusy, "synchronous arrival drains after its observer");
        }
        private static void BorrowedAdmissionRechecksAuthority()
        {
            var authority = new Authority();
            var coordinator = new SceneCoordinator(authorityPolicy: authority);
            using var reservation = coordinator.TryReserve(new NativeTransitionOwner("world.mod", "runtime:session", "session"), "start").Value!;
            using var grant = reservation.BorrowFor("world.mod", "session");
            authority.Allowed = false;
            var acquired = grant.SceneTransitions.Acquire("World", false, "world replacement teardown");
            Assert(!acquired.Succeeded && acquired.ErrorCode == ModErrorCode.NotAuthoritative,
                "borrowed Acquire must recheck current authority before caller-side teardown");
        }
        private static void RefusalCannotAuthorizeFallback()
        {
            foreach (var code in new[] { ModErrorCode.NotAuthoritative, ModErrorCode.InvalidState, ModErrorCode.Conflict, ModErrorCode.Cancelled, ModErrorCode.External })
            {
                var result = WorldNativeDispatchResult.From(OperationResult<IInternalNativeSceneOperation>.Failure(code, "refused"));
                Assert(!result.Accepted && !result.CanFallback && result.ErrorCode == code,
                    "structured " + code + " refusal cannot authorize arena fallback");
            }
            Assert(WorldNativeDispatchResult.From(OperationResult<IInternalNativeSceneOperation>.Failure(
                ModErrorCode.Unavailable, "no native entrypoint")).CanFallback, "an unsupported route with no effects permits fallback");
        }
        private static void TrackerPreservesCurrentLoaderFailure()
        {
            var tracker = new SceneTransitionTracker();
            var current = tracker.Begin(0, "World");
            tracker.ResolveSceneArrival("World");
            tracker.ReportFailure(current, "post-scene failure");
            Assert(tracker.ConsumeFailure(1, 30) == "post-scene failure", "a current loader fault after scene arrival must end its provisional session");
            var old = tracker.Begin(2, "Other");
            tracker.ResolveSceneArrival("Other");
            tracker.Abandon();
            tracker.ReportFailure(old, "stale failure");
            Assert(tracker.ConsumeFailure(3, 30) == null, "an ended scene generation cannot affect its successor");
        }
        private static SceneSnapshot Scene() => new SceneSnapshot("World", true, true);
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private sealed class Authority : ISceneTransitionAuthorityPolicy
        {
            internal bool Allowed = true;
            public SceneTransitionAuthorityDecision Evaluate(SceneTransitionRequest request) =>
                Allowed ? SceneTransitionAuthorityDecision.Allow() : SceneTransitionAuthorityDecision.Deny("authority changed");
        }
    }
}
