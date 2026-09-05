using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class NativeTransitionReservation : INativeTransitionReservation, IInternalSceneTransitionLease
    {
        private readonly SceneCoordinator coordinator;
        private readonly List<Grant> grants = new List<Grant>();
        private readonly TaskCompletionSource<NativeDrainResult> drained =
            new TaskCompletionSource<NativeDrainResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private NativeSceneOperation? operation;
        private Grant? borrowedLease;
        private bool closed;

        internal NativeTransitionReservation(SceneCoordinator coordinator, NativeTransitionOwner owner, SceneTransitionRequest request)
        {
            this.coordinator = coordinator;
            Owner = owner;
            Info = new SceneClaimInfo(owner.PackageId, request.SceneName, request.Priority, request.Reason, DateTime.UtcNow);
            Transitions = new Grant(this, owner.PackageId, owner.SessionId ?? string.Empty);
        }
        internal Task<NativeDrainResult> DrainTask => drained.Task;
        internal NativeTransitionOwner Owner { get; }
        internal SceneClaimInfo Info { get; }
        public IInternalSceneTransitionService Transitions { get; }

        public INativeTransitionGrant BorrowFor(string packageId, string sessionId)
        {
            coordinator.AssertCurrent();
            if (closed) throw new ObjectDisposedException(nameof(NativeTransitionReservation));
            if (Owner.SessionId != null && Owner.SessionId != sessionId)
                throw new InvalidOperationException("A reservation cannot be borrowed by a different session.");
            var grant = new Grant(this, packageId, sessionId);
            grants.Add(grant);
            return grant;
        }

        public Task<NativeDrainResult> CloseAsync()
        {
            coordinator.Post(() =>
            {
                closed = true;
                foreach (var grant in grants) grant.Dispose();
                ReleaseIfDrained();
            });
            return drained.Task;
        }
        public void Dispose() { _ = CloseAsync(); }

        internal void RevokeOwner()
        {
            coordinator.Post(() =>
            {
                operation?.Cancel("The transition owner was unloaded.");
                _ = CloseAsync();
            });
        }

        internal bool OwnsPackage(string id) => string.Equals(Owner.PackageId, id, StringComparison.OrdinalIgnoreCase)
            || (operation != null && string.Equals(operation.OwnerPackageId, id, StringComparison.OrdinalIgnoreCase));

        private OperationResult<IInternalNativeSceneOperation> Dispatch(
            Grant grant, NativeSceneRequest request, IInternalNativeSceneDispatch dispatch, CancellationToken token)
        {
            coordinator.AssertCurrent();
            var refused = CheckDispatchState(grant, token);
            if (refused != null) return refused;
            var denied = coordinator.CheckAuthority(new SceneTransitionRequest(
                grant.PackageId, request.SceneName,
                request.Automatic ? SceneTransitionPriority.Automatic : SceneTransitionPriority.UserInitiated,
                request.Reason));
            if (denied != null) return Failure(ModErrorCode.NotAuthoritative, denied);
            // Authority is provider-backed code and may revoke or reenter this reservation.
            refused = CheckDispatchState(grant, token);
            if (refused != null) return refused;

            var started = new NativeSceneOperation(coordinator, this, grant.PackageId, request, token);
            operation = started; // Publish before Begin: callbacks may complete synchronously.
            started.Begin(dispatch);
            return started.DispatchStatus == NativeSceneDispatchStatus.NotDispatched
                ? Failure(started.InitialErrorCode, started.InitialErrorMessage)
                : OperationResult<IInternalNativeSceneOperation>.Success(started);
        }

        private OperationResult<IInternalNativeSceneOperation>? CheckDispatchState(Grant grant, CancellationToken token)
        {
            if (closed || grant.Revoked)
                return Failure(ModErrorCode.InvalidState, "The native transition grant has been revoked.");
            if (operation != null)
                return Failure(ModErrorCode.Conflict, "Another native operation is still draining.");
            if (borrowedLease != null && !borrowedLease.Revoked && !ReferenceEquals(borrowedLease, grant))
                return Failure(ModErrorCode.Conflict, "Another provider route holds native admission.");
            if (token.IsCancellationRequested)
                return Failure(ModErrorCode.Cancelled, "The transition was cancelled before native dispatch.");
            return null;
        }

        internal void OperationDrained(NativeSceneOperation finished)
        {
            coordinator.AssertCurrent();
            if (!ReferenceEquals(operation, finished)) return;
            operation = null;
            ReleaseIfDrained();
        }
        private void ReleaseIfDrained()
        {
            if (!closed || operation != null) return;
            coordinator.Release(this);
            drained.TrySetResult(NativeDrainResult.Drained);
        }
        internal void ObserveScene(SceneSnapshot scene) => operation?.ObserveScene(scene);
        internal void CheckTimeout(DateTime nowUtc, TimeSpan timeout) => operation?.CheckTimeout(nowUtc, timeout);
        private static OperationResult<IInternalNativeSceneOperation> Failure(ModErrorCode code, string message) =>
            OperationResult<IInternalNativeSceneOperation>.Failure(code, message);

        private sealed class Grant : INativeTransitionGrant, IInternalSceneTransitionService, IInternalSceneTransitionLease
        {
            private readonly NativeTransitionReservation reservation;
            private readonly string sessionId;
            private int revoked;
            private readonly Grant? parent;
            public Grant(NativeTransitionReservation reservation, string packageId, string sessionId, Grant? parent = null)
            {
                this.parent = parent;
                this.reservation = reservation;
                PackageId = packageId;
                this.sessionId = sessionId;
            }
            internal string PackageId { get; }
            public string SessionId => sessionId;
            internal bool Revoked => Volatile.Read(ref revoked) != 0 || (parent?.Revoked ?? false);
            public bool IsBusy => reservation.coordinator.IsSceneBusy;
            public IInternalSceneTransitionService SceneTransitions => this;
            public IInternalSceneTransitionService Transitions => this;
            public void Dispose() => Interlocked.Exchange(ref revoked, 1);
            public OperationResult<IInternalSceneTransitionLease> Acquire(string sceneName, bool automatic, string reason)
            {
                reservation.coordinator.AssertCurrent();
                var refused = CheckAcquireState();
                if (refused != null) return refused;
                var denied = reservation.coordinator.CheckAuthority(new SceneTransitionRequest(PackageId, sceneName,
                    automatic ? SceneTransitionPriority.Automatic : SceneTransitionPriority.UserInitiated, reason));
                if (denied != null)
                    return OperationResult<IInternalSceneTransitionLease>.Failure(ModErrorCode.NotAuthoritative, denied);
                refused = CheckAcquireState();
                if (refused != null) return refused;
                // Reserve before returning: the caller may invoke reentrant teardown callbacks before dispatch.
                // Closing this child revokes access without releasing the orchestrator's native reservation.
                var child = new Grant(reservation, PackageId, sessionId, this);
                reservation.borrowedLease = child;
                return OperationResult<IInternalSceneTransitionLease>.Success(child);
            }
            private OperationResult<IInternalSceneTransitionLease>? CheckAcquireState()
            {
                if (Revoked || reservation.closed)
                    return OperationResult<IInternalSceneTransitionLease>.Failure(ModErrorCode.InvalidState, "The native transition grant has been revoked.");
                if (reservation.operation != null
                    || (reservation.borrowedLease != null && !reservation.borrowedLease.Revoked))
                    return OperationResult<IInternalSceneTransitionLease>.Failure(ModErrorCode.Conflict,
                        "Another provider route holds native admission or its native work is still draining.");
                return null;
            }
            public OperationResult<IInternalNativeSceneOperation> TryDispatch(
                NativeSceneRequest request, IInternalNativeSceneDispatch dispatch, CancellationToken callerToken = default) =>
                reservation.Dispatch(this, request, dispatch, callerToken);
        }
    }
}
