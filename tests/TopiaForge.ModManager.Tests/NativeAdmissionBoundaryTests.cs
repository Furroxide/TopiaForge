using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativeAdmissionBoundaryTests
    {
        internal static void Run()
        {
            var failures = new List<Exception>();
            foreach (var test in new Action[] { ManagedLoaderTimeoutRetainsOwnership,
                AcquireRejectsRevocation, DispatchRejectsRevocation, AcquirePreservesNestedLease,
                AcquirePreservesNestedOperation, DispatchPreservesNestedLease, DispatchPreservesNestedOperation,
                ReservationRejectsRevokedOwner, ReservationRejectsClosedSessionAdmission,
                DirectAcquireRejectsStoppedOwner, BorrowedAcquireRejectsStoppedOwner })
                try { test(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }

        private static void ManagedLoaderTimeoutRetainsOwnership()
        {
            var coordinator = new SceneCoordinator();
            IInternalNativeSceneCompletion? completion = null;
            var started = new OwnerSceneTransitionService("world.mod", coordinator).TryDispatch(Request(),
                new DelegateNativeSceneDispatch(sink =>
                { completion = sink; sink.RequireManagedCompletion(); return NativeSceneDispatchStatus.Dispatched; }));
            var operation = started.Value!;
            coordinator.NotifySceneArrived(Scene());
            Assert(!operation.Completion.IsCompleted && coordinator.IsSceneBusy, "managed work remains pending after scene arrival");
            coordinator.CheckTimeout(DateTime.UtcNow.AddMinutes(2), TimeSpan.FromSeconds(30));
            Assert(operation.Completion.IsCompleted && !operation.Completion.Result.Succeeded,
                "a hung managed loader must time out its caller even after native scene arrival");
            Assert(coordinator.IsSceneBusy && !operation.NativeDrained.IsCompleted,
                "caller timeout must retain native ownership until the managed loader actually finishes");
            completion!.ManagedCompleted(OperationResult<bool>.Success(true));
            Assert(!operation.Completion.Result.Succeeded && operation.NativeDrained.IsCompleted && !coordinator.IsSceneBusy,
                "late managed completion drains ownership without overwriting the timeout outcome");
        }

        private static void AcquireRejectsRevocation()
        {
            foreach (var wholeReservation in new[] { false, true })
            {
                using var fixture = new Fixture();
                fixture.Authority.OnCheck = wholeReservation ? fixture.Reservation.Dispose : fixture.Grant.Dispose;
                var acquired = fixture.Grant.SceneTransitions.Acquire("World", false, "after authority");
                Assert(!acquired.Succeeded && acquired.ErrorCode == ModErrorCode.InvalidState,
                    "Acquire must reject a grant/reservation revoked inside the authority callback");
            }
        }

        private static void DispatchRejectsRevocation()
        {
            foreach (var wholeReservation in new[] { false, true })
            {
                using var fixture = new Fixture();
                fixture.Authority.OnCheck = wholeReservation ? fixture.Reservation.Dispose : fixture.Grant.Dispose;
                var entered = false;
                var dispatched = fixture.Grant.SceneTransitions.TryDispatch(Request(), new DelegateNativeSceneDispatch(_ =>
                { entered = true; return NativeSceneDispatchStatus.Dispatched; }));
                Assert(!dispatched.Succeeded && dispatched.ErrorCode == ModErrorCode.InvalidState && !entered,
                    "Dispatch must not run a native adapter after authority evaluation revoked its ownership");
            }
        }

        private static void AcquirePreservesNestedLease()
        {
            using var fixture = new Fixture();
            IInternalSceneTransitionLease? nested = null;
            fixture.Authority.OnCheck = () => nested = fixture.Grant.SceneTransitions.Acquire("World", false, "nested").Value!;
            var outer = fixture.Grant.SceneTransitions.Acquire("World", false, "outer");
            Assert(!outer.Succeeded && outer.ErrorCode == ModErrorCode.Conflict && nested != null,
                "Acquire must not overwrite a child lease admitted reentrantly by authority evaluation");
            Assert(!fixture.Grant.SceneTransitions.Acquire("World", false, "competing").Succeeded,
                "the nested child must remain the exclusive borrowed admission owner");
            nested!.Dispose();
        }

        private static void AcquirePreservesNestedOperation()
        {
            using var fixture = new Fixture();
            IInternalNativeSceneCompletion? completion = null;
            fixture.Authority.OnCheck = () => fixture.Grant.SceneTransitions.TryDispatch(Request(), new DelegateNativeSceneDispatch(sink =>
            { completion = sink; return NativeSceneDispatchStatus.Dispatched; }));
            var outer = fixture.Grant.SceneTransitions.Acquire("World", false, "outer");
            Assert(!outer.Succeeded && outer.ErrorCode == ModErrorCode.Conflict && completion != null,
                "Acquire must refuse Busy after authority evaluation dispatched another native operation");
            var drain = fixture.Reservation.CloseAsync();
            Assert(!drain.IsCompleted, "the nested operation retains its drain barrier");
            completion!.NativeCompleted(OperationResult<SceneSnapshot>.Success(Scene()));
            Assert(drain.IsCompleted && !fixture.Coordinator.IsSceneBusy, "nested completion still retires its real owner");
        }

        private static void DispatchPreservesNestedLease()
        {
            using var fixture = new Fixture();
            IInternalSceneTransitionLease? nested = null;
            fixture.Authority.OnCheck = () => nested = fixture.Grant.SceneTransitions.Acquire("World", false, "nested").Value!;
            var entered = false;
            var outer = fixture.Grant.SceneTransitions.TryDispatch(Request(), new DelegateNativeSceneDispatch(_ =>
            { entered = true; return NativeSceneDispatchStatus.Dispatched; }));
            Assert(!outer.Succeeded && outer.ErrorCode == ModErrorCode.Conflict && !entered && nested != null,
                "Dispatch cannot bypass a borrowed lease created by its authority callback");
            nested!.Dispose();
        }

        private static void DispatchPreservesNestedOperation()
        {
            using var fixture = new Fixture();
            IInternalNativeSceneCompletion? completion = null;
            IInternalNativeSceneOperation? nested = null;
            var entered = 0;
            fixture.Authority.OnCheck = () => nested = fixture.Grant.SceneTransitions.TryDispatch(Request(),
                new DelegateNativeSceneDispatch(sink =>
                { entered++; completion = sink; return NativeSceneDispatchStatus.Dispatched; })).Value!;
            var outer = fixture.Grant.SceneTransitions.TryDispatch(Request(), new DelegateNativeSceneDispatch(_ =>
            { entered++; return NativeSceneDispatchStatus.Dispatched; }));
            Assert(!outer.Succeeded && outer.ErrorCode == ModErrorCode.Conflict && entered == 1,
                "Dispatch must not overwrite an operation admitted reentrantly by authority evaluation");
            var drain = fixture.Reservation.CloseAsync();
            Assert(!drain.IsCompleted && !nested!.NativeDrained.IsCompleted, "the nested operation remains authoritative until completion");
            completion!.NativeCompleted(OperationResult<SceneSnapshot>.Success(Scene()));
            Assert(drain.IsCompleted && nested!.NativeDrained.IsCompleted && !fixture.Coordinator.IsSceneBusy,
                "the retained nested operation completes the reservation exactly once");
        }

        private static void ReservationRejectsRevokedOwner()
        {
            var authority = new CallbackAuthority();
            var coordinator = new SceneCoordinator(authorityPolicy: authority);
            var owner = new OwnerSceneTransitionService("world.mod", coordinator, ownershipId: "runtime:old:world");
            authority.OnCheck = () => coordinator.RevokeOwnership("runtime:old");
            var acquired = owner.Acquire("World", false, "initial reservation");
            Assert(!acquired.Succeeded && acquired.ErrorCode == ModErrorCode.InvalidState && !coordinator.IsSceneBusy,
                "initial Reserve must not grant ownership revoked by its authority callback");
        }

        private static void ReservationRejectsClosedSessionAdmission()
        {
            var authority = new CallbackAuthority();
            var coordinator = new SceneCoordinator(authorityPolicy: authority);
            var owner = new OwnerSceneTransitionService("world.mod", coordinator);
            authority.OnCheck = () => coordinator.SetSessionAdmissionGate(() => true);
            var acquired = owner.Acquire("World", false, "initial reservation");
            Assert(!acquired.Succeeded && acquired.ErrorCode == ModErrorCode.Conflict && !coordinator.IsSceneBusy,
                "initial Reserve must honor a session admission gate closed by its authority callback");
        }

        private static void DirectAcquireRejectsStoppedOwner()
        {
            using var stopped = new CancellationTokenSource();
            var authority = new CallbackAuthority();
            var coordinator = new SceneCoordinator(authorityPolicy: authority);
            var service = new OwnerSceneTransitionService("world.mod", coordinator, stopped.Token);
            authority.OnCheck = stopped.Cancel;
            var acquired = service.Acquire("World", false, "direct owner stopping");
            Assert(!acquired.Succeeded && acquired.ErrorCode == ModErrorCode.Cancelled && !coordinator.IsSceneBusy,
                "direct Acquire must reject and close its candidate when authority evaluation stops the owner");
        }

        private static void BorrowedAcquireRejectsStoppedOwner()
        {
            using var fixture = new Fixture();
            using var stopped = new CancellationTokenSource();
            var slot = new NativeTransitionAccessSlot("session:world.mod", "session", () => !stopped.IsCancellationRequested);
            using var binding = slot.Install(fixture.Grant);
            var service = new OwnerSceneTransitionService("world.mod", fixture.Coordinator, stopped.Token, slot);
            fixture.Authority.OnCheck = stopped.Cancel;
            var acquired = service.Acquire("World", false, "borrowed owner stopping");
            Assert(!acquired.Succeeded && acquired.ErrorCode == ModErrorCode.Cancelled && fixture.Coordinator.IsSceneBusy,
                "borrowed Acquire must reject its stopped owner without releasing the manager reservation");
            var competitor = fixture.Grant.SceneTransitions.Acquire("World", false, "live competing owner");
            Assert(competitor.Succeeded, "the stopped borrowed owner must release its candidate child lease");
            competitor.Value!.Dispose();
        }

        private static NativeSceneRequest Request() => new NativeSceneRequest("World", false, "authority boundary");
        private static SceneSnapshot Scene() => new SceneSnapshot("World", true, true);
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private sealed class Fixture : IDisposable
        {
            internal readonly CallbackAuthority Authority = new CallbackAuthority();
            internal readonly SceneCoordinator Coordinator;
            internal readonly INativeTransitionReservation Reservation;
            internal readonly INativeTransitionGrant Grant;
            internal Fixture()
            {
                Coordinator = new SceneCoordinator(authorityPolicy: Authority);
                Reservation = Coordinator.TryReserve(new NativeTransitionOwner("world.mod", "runtime:session", "session"), "start").Value!;
                Grant = Reservation.BorrowFor("world.mod", "session");
            }
            public void Dispose() { Grant.Dispose(); Reservation.Dispose(); }
        }
        private sealed class CallbackAuthority : ISceneTransitionAuthorityPolicy
        {
            internal Action? OnCheck;
            public SceneTransitionAuthorityDecision Evaluate(SceneTransitionRequest request)
            {
                var callback = OnCheck;
                OnCheck = null;
                callback?.Invoke();
                return SceneTransitionAuthorityDecision.Allow();
            }
        }
    }
}
