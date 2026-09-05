using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativeTransitionExecutorTests
    {
        public static void Run()
        {
            var failures = new System.Collections.Generic.List<Exception>();
            foreach (var test in new Action[] {
                NativeLoaderDrainTests.Run, OwnerCheckpointSubscriptionTests.Run, LocalImportRequiresFreshResult, LocalImportPreservesAdmission, CrossRouteCancellationRetainsNative,
                RevokedProviderGrantCannotReenter, OwnerReloadSharesOutstandingNative,
                WorkerCompletionWaitsForHost, UncertainDispatchAndLateArrival,
                SynchronousAndPreflightCompletion, LifecycleStopGateAndAuthority,
                ForeignSessionGrantIsRejected, CancellationBeforeNativeCompletionWins, BorrowedAdmissionIsExclusive })
            {
                try { test(); } catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0) throw new AggregateException(failures);
            Console.WriteLine("All native transition executor tests passed.");
        }

        private static void LocalImportRequiresFreshResult()
        {
            var oldImport = new object();
            Assert(!TopiaForge.Worlds.UgcImportCompletionPolicy.IsFresh(oldImport, oldImport),
                "an earlier imported scene cannot prove the current import succeeded");
            Assert(!TopiaForge.Worlds.UgcImportCompletionPolicy.IsFresh(oldImport, null), "a null result is not imported content");
            Assert(TopiaForge.Worlds.UgcImportCompletionPolicy.IsFresh(oldImport, new object()), "new imported content is accepted");
        }

        private static void LocalImportPreservesAdmission()
        {
            var coordinator = new SceneCoordinator();
            var service = new OwnerSceneTransitionService("world.mod", coordinator);
            Start(service, out var pending);
            var calls = 0;
            OperationResult<bool> Import(Action entered)
            {
                calls++; entered(); return OperationResult<bool>.Success(true);
            }
            var blocked = TopiaForge.Worlds.LocalWorldImportOperation.Run(service,
                new SceneSnapshot("SceneA", true, true), default, Import);
            Assert(calls == 0 && blocked.ErrorCode == ModErrorCode.Conflict,
                "the real local-import route preserves Busy and never invokes the importer");
            pending.Sink!.NativeCompleted(Loaded());
            using var stopped = new CancellationTokenSource();
            stopped.Cancel();
            var cancelled = TopiaForge.Worlds.LocalWorldImportOperation.Run(service,
                new SceneSnapshot("SceneA", true, true), stopped.Token, Import);
            Assert(calls == 0 && cancelled.ErrorCode == ModErrorCode.Cancelled,
                "local-import cancellation remains distinct from invalid input");
            var denied = TopiaForge.Worlds.LocalWorldImportOperation.Run(
                new OwnerSceneTransitionService("world.mod", new SceneCoordinator(authorityPolicy: new Deny())),
                new SceneSnapshot("SceneA", true, true), default, Import);
            Assert(calls == 0 && denied.ErrorCode == ModErrorCode.NotAuthoritative,
                "local-import authority refusal is preserved before effects");
            Assert(TopiaForge.Worlds.LocalWorldImportOperation.Run(service,
                new SceneSnapshot("SceneA", true, true), default, Import).Succeeded
                && calls == 1 && !coordinator.IsSceneBusy, "fresh completed local imports release admission");
        }

        private static void ForeignSessionGrantIsRejected()
        {
            var coordinator = new SceneCoordinator();
            coordinator.TryReserve(new NativeTransitionOwner("mode.mod", "runtime:session1", "session1"), "start")
                .TryGetValue(out var reservation);
            var grant = reservation!.BorrowFor("world.mod", "session1");
            var unrelated = new NativeTransitionAccessSlot("session2:world.mod", "session2", () => true);
            var rejected = false;
            try { unrelated.Install(grant).Dispose(); } catch (InvalidOperationException) { rejected = true; }
            reservation.Dispose();
            Assert(rejected, "a provider grant from another session cannot enter this scope");
        }

        private static void CancellationBeforeNativeCompletionWins()
        {
            using var dispatcher = new HostDispatcher();
            using var cancelled = new CancellationTokenSource();
            var coordinator = new SceneCoordinator(dispatcher: dispatcher);
            var result = new OwnerSceneTransitionService("world.mod", coordinator).TryDispatch(Request("race"),
                new DelegateNativeSceneDispatch(sink =>
                {
                    Task.Run(cancelled.Cancel).GetAwaiter().GetResult();
                    sink.NativeCompleted(Loaded());
                    return NativeSceneDispatchStatus.Dispatched;
                }), cancelled.Token);
            Assert(result.TryGetValue(out var operation), "the native call was dispatched");
            var cancelledResult = operation!.Completion.Result.ErrorCode == ModErrorCode.Cancelled;
            dispatcher.Drain();
            Assert(cancelledResult, "cancellation already signaled before native completion must not report success");
            Assert(operation.NativeDrained.IsCompleted && !coordinator.IsSceneBusy, "cancellation does not lose drain");
        }

        private static readonly string[] Routes = { "core", "worlds", "local-world", "restart", "main-menu" };
        private static NativeSceneRequest Request(string route, bool arrival = false) =>
            new NativeSceneRequest("SceneA", false, route, arrival);
        private static OperationResult<SceneSnapshot> Loaded() =>
            OperationResult<SceneSnapshot>.Success(new SceneSnapshot("SceneA", true, true));
        private static IInternalNativeSceneOperation Start(IInternalSceneTransitionService service,
            out Holder holder, CancellationToken token = default, bool arrival = false)
        {
            var capture = new Holder();
            var result = service.TryDispatch(Request("held", arrival), new DelegateNativeSceneDispatch(sink =>
            {
                capture.Sink = sink; capture.Dispatches++;
                return NativeSceneDispatchStatus.Dispatched;
            }), token);
            Assert(result.TryGetValue(out var operation), "dispatch must be admitted: " + result.ErrorMessage);
            holder = capture;
            return operation!;
        }

        private static void CrossRouteCancellationRetainsNative()
        {
            foreach (var startingRoute in Routes)
            {
                var coordinator = new SceneCoordinator();
                using var cancelled = new CancellationTokenSource();
                var operation = Start(new OwnerSceneTransitionService(startingRoute, coordinator), out var holder, cancelled.Token);
                cancelled.Cancel();
                Assert(operation.Completion.Result.ErrorCode == ModErrorCode.Cancelled, "caller receives cancellation");
                Assert(!operation.NativeDrained.IsCompleted && coordinator.IsSceneBusy, "cancellation is not engine completion");
                foreach (var route in Routes)
                {
                    var attempted = new OwnerSceneTransitionService(route, coordinator).TryDispatch(Request(route),
                        new DelegateNativeSceneDispatch(_ => throw new Exception("must not execute")));
                    Assert(attempted.ErrorCode == ModErrorCode.Conflict, "every competing route remains Busy: " + route);
                }
                holder.Sink!.NativeCompleted(Loaded());
                Assert(operation.NativeDrained.IsCompleted && !coordinator.IsSceneBusy, "only native completion admits another route");
            }
        }

        private static void RevokedProviderGrantCannotReenter()
        {
            var coordinator = new SceneCoordinator();
            var admission = coordinator.TryReserve(new NativeTransitionOwner("mode.mod", "runtime:session", "session"), "start");
            Assert(admission.TryGetValue(out var reservation), "lifecycle reserves admission");
            var slot = new NativeTransitionAccessSlot("session:world.mod", "session", () => true);
            var grant = reservation!.BorrowFor("world.mod", "session");
            var scoped = new OwnerSceneTransitionService("world.mod", coordinator, access: slot);
            var binding = slot.Install(grant);
            var operation = Start(scoped, out var holder);
            Assert(scoped.Acquire("SceneA", false, "competing provider request").ErrorCode == ModErrorCode.Conflict,
                "a second provider request is rejected at admission before it can tear down content");
            Assert(!new OwnerSceneTransitionService("mode.mod", coordinator).Acquire("SceneA", false, "startup").Succeeded,
                "mode constructors and startup cannot join provider admission");
            binding.Dispose();
            Assert(!grant.SceneTransitions.Acquire("SceneA", false, "stale").Succeeded, "captured grant is permanently revoked");
            var closed = reservation.CloseAsync();
            Assert(!closed.IsCompleted && coordinator.IsSceneBusy, "grant revocation retains native drain");
            holder.Sink!.NativeCompleted(Loaded());
            Assert(closed.IsCompleted && operation.NativeDrained.IsCompleted && !coordinator.IsSceneBusy, "closed reservation drains once");
        }

        private static void BorrowedAdmissionIsExclusive()
        {
            var coordinator = new SceneCoordinator();
            coordinator.TryReserve(new NativeTransitionOwner("mode.mod", "runtime:session", "session"), "start")
                .TryGetValue(out var reservation);
            var grant = reservation!.BorrowFor("world.mod", "session");
            var service = grant.SceneTransitions;
            Assert(service.Acquire("SceneA", false, "first").TryGetValue(out var first), "first provider route enters");
            var effects = 0;
            var competing = service.Acquire("SceneA", false, "reentrant teardown callback");
            if (competing.TryGetValue(out var extra)) { effects++; extra.Dispose(); }
            Assert(effects == 0 && competing.ErrorCode == ModErrorCode.Conflict,
                "a borrowed route holds exclusive admission before native dispatch and rejects reentrant effects");
            var bypass = service.TryDispatch(Request("bypass"), new DelegateNativeSceneDispatch(_ =>
            {
                effects++; return NativeSceneDispatchStatus.NotDispatched;
            }));
            Assert(effects == 0 && bypass.ErrorCode == ModErrorCode.Conflict,
                "captured parent grants cannot bypass an admitted child route");
            first!.Dispose();
            Assert(service.Acquire("SceneA", false, "retry").TryGetValue(out var retry),
                "disposing an unused child admits a later route");
            var operation = Start(retry!.Transitions, out var native);
            retry.Dispose();
            Assert(service.Acquire("SceneA", false, "draining").ErrorCode == ModErrorCode.Conflict,
                "child disposal does not retire the native work it dispatched");
            native.Sink!.NativeCompleted(Loaded());
            Assert(operation.NativeDrained.IsCompleted, "the admitted child can dispatch and drain");
            reservation.Dispose();
            Assert(!coordinator.IsSceneBusy, "completed provider reservation releases once");
        }

        private static void OwnerReloadSharesOutstandingNative()
        {
            var coordinator = new SceneCoordinator();
            var oldFacade = new OwnerSceneTransitionService("world.mod", coordinator, ownershipId: "runtime1:world.mod");
            var oldOperation = Start(oldFacade, out var old);
            coordinator.RevokeOwnership("runtime1");
            Assert(oldOperation.Completion.Result.ErrorCode == ModErrorCode.Cancelled, "unload cancels caller");
            var replacement = new OwnerSceneTransitionService("world.mod", coordinator, ownershipId: "runtime2:world.mod");
            Assert(replacement.Acquire("SceneB", false, "reload").ErrorCode == ModErrorCode.Conflict, "new runtime shares old drain");
            old.Sink!.NativeCompleted(Loaded());
            Assert(oldFacade.Acquire("SceneB", false, "stale").ErrorCode == ModErrorCode.InvalidState, "old runtime remains revoked");
            var next = Start(replacement, out var fresh);
            old.Sink.NativeCompleted(Loaded());
            Assert(coordinator.IsSceneBusy && !next.NativeDrained.IsCompleted, "stale completion cannot release a newer owner");
            fresh.Sink!.NativeCompleted(Loaded());
        }

        private static void WorkerCompletionWaitsForHost()
        {
            using var dispatcher = new HostDispatcher();
            var coordinator = new SceneCoordinator(dispatcher: dispatcher);
            var operation = Start(new OwnerSceneTransitionService("world.mod", coordinator), out var holder);
            Task.Run(() => holder.Sink!.NativeCompleted(Loaded())).GetAwaiter().GetResult();
            Assert(!operation.NativeDrained.IsCompleted && coordinator.IsSceneBusy, "worker reports do not mutate native ownership");
            dispatcher.Drain();
            Assert(operation.NativeDrained.IsCompleted && !coordinator.IsSceneBusy, "host drains worker report");
        }

        private static void UncertainDispatchAndLateArrival()
        {
            var coordinator = new SceneCoordinator();
            var attempts = 0;
            var result = new OwnerSceneTransitionService("world.mod", coordinator).TryDispatch(Request("reflection", true),
                new DelegateNativeSceneDispatch(_ => { attempts++; throw new InvalidOperationException("observer attach failed"); }));
            Assert(result.TryGetValue(out var operation) && operation!.DispatchStatus == NativeSceneDispatchStatus.Indeterminate,
                "possible native effects stay represented as an admitted uncertain operation");
            Assert(attempts == 1 && coordinator.IsSceneBusy, "uncertainty never dispatches fallback");
            coordinator.NotifySceneArrived(new SceneSnapshot("MainMenu", true, true));
            Assert(coordinator.IsSceneBusy, "unrelated scene cannot clear quarantine");
            coordinator.CheckTimeout(DateTime.UtcNow.AddMinutes(2), TimeSpan.FromSeconds(30));
            Assert(coordinator.IsSceneBusy, "timeout cannot clear quarantine");
            coordinator.NotifySceneArrived(new SceneSnapshot("SceneA", true, true));
            Assert(!coordinator.IsSceneBusy && operation!.NativeDrained.IsCompleted, "late expected arrival retires native work");
        }

        private static void SynchronousAndPreflightCompletion()
        {
            var coordinator = new SceneCoordinator();
            var service = new OwnerSceneTransitionService("world.mod", coordinator);
            var sync = service.TryDispatch(Request("sync"), new DelegateNativeSceneDispatch(sink =>
            {
                sink.NativeCompleted(Loaded());
                return NativeSceneDispatchStatus.Dispatched;
            }));
            Assert(sync.TryGetValue(out var operation) && operation!.NativeDrained.IsCompleted && !coordinator.IsSceneBusy,
                "synchronous native completion sees published ownership");
            var refused = service.TryDispatch(Request("preflight"), new DelegateNativeSceneDispatch(sink =>
            {
                sink.FailCaller(ModErrorCode.NotFound, "missing method");
                return NativeSceneDispatchStatus.NotDispatched;
            }));
            Assert(refused.ErrorCode == ModErrorCode.NotFound && !coordinator.IsSceneBusy, "proven no-dispatch releases admission");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var before = service.TryDispatch(Request("cancel"), new DelegateNativeSceneDispatch(_ => throw new Exception("must not execute")), cancelled.Token);
            Assert(before.ErrorCode == ModErrorCode.Cancelled && !coordinator.IsSceneBusy, "pre-dispatch cancellation has no native effect");
        }

        private static void LifecycleStopGateAndAuthority()
        {
            var coordinator = new SceneCoordinator();
            coordinator.SetSessionAdmissionGate(() => true);
            Assert(new OwnerSceneTransitionService("world.mod", coordinator).Acquire("SceneA", false, "stop").ErrorCode == ModErrorCode.Conflict,
                "Stopping blocks normal requests even between native operations");
            var cleanup = coordinator.TryReserve(new NativeTransitionOwner("mode.mod", "runtime:session", "session"), "cleanup");
            Assert(cleanup.TryGetValue(out var lease), "lifecycle can reserve its cleanup transaction");
            lease!.Dispose();
            var denied = new SceneCoordinator(authorityPolicy: new Deny());
            var result = new OwnerSceneTransitionService("world.mod", denied).TryDispatch(Request("client"),
                new DelegateNativeSceneDispatch(_ => throw new Exception("must not execute")));
            Assert(result.ErrorCode == ModErrorCode.NotAuthoritative && !denied.IsSceneBusy, "authority is checked before effects");
        }

        private sealed class Holder { internal IInternalNativeSceneCompletion? Sink; internal int Dispatches; }
        private sealed class Deny : ISceneTransitionAuthorityPolicy
        {
            public SceneTransitionAuthorityDecision Evaluate(SceneTransitionRequest request) =>
                SceneTransitionAuthorityDecision.Deny("client cannot mutate world");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
