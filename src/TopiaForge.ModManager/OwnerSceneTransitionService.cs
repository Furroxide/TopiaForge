using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerSceneTransitionService : IInternalSceneTransitionService
    {
        private readonly string ownerModId;
        private readonly SceneCoordinator coordinator;
        private readonly CancellationToken stoppingToken;
        private readonly NativeTransitionAccessSlot? access;
        private readonly string ownershipId;

        public OwnerSceneTransitionService(string ownerModId, SceneCoordinator coordinator,
            CancellationToken stoppingToken = default, NativeTransitionAccessSlot? access = null,
            string? ownershipId = null)
        {
            this.ownerModId = ownerModId;
            this.coordinator = coordinator;
            this.stoppingToken = stoppingToken;
            this.access = access;
            this.ownershipId = ownershipId ?? access?.OwnershipId ?? ownerModId;
        }

        public bool IsBusy => coordinator.IsSceneBusy;
        private bool IsAlive => !stoppingToken.IsCancellationRequested && (access?.IsAlive ?? true);

        public OperationResult<IInternalSceneTransitionLease> Acquire(string sceneName, bool automatic, string reason)
        {
            coordinator.AssertCurrent();
            if (!IsAlive)
                return OperationResult<IInternalSceneTransitionLease>.Failure(ModErrorCode.Cancelled, "The scene owner has stopped.");
            if (access?.Borrowed is IInternalSceneTransitionService borrowed)
            {
                var child = borrowed.Acquire(sceneName, automatic, reason);
                if (!child.TryGetValue(out var lease)) return child;
                var candidate = new LifetimeLease(lease, stoppingToken);
                if (!IsAlive)
                {
                    candidate.Dispose();
                    return OperationResult<IInternalSceneTransitionLease>.Failure(ModErrorCode.Cancelled, "The scene owner has stopped.");
                }
                return OperationResult<IInternalSceneTransitionLease>.Success(candidate);
            }
            var request = new SceneTransitionRequest(ownerModId, sceneName,
                automatic ? SceneTransitionPriority.Automatic : SceneTransitionPriority.UserInitiated, reason);
            var result = coordinator.Reserve(new NativeTransitionOwner(ownerModId, ownershipId, access?.SessionId), request);
            if (!result.TryGetValue(out var reservation))
                return OperationResult<IInternalSceneTransitionLease>.Failure(result.ErrorCode, result.ErrorMessage);
            var native = (NativeTransitionReservation)reservation;
            var registration = stoppingToken.Register(native.RevokeOwner);
            _ = native.DrainTask.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Authority evaluation can synchronously stop this owner after the entry check.
            if (!IsAlive)
            {
                native.Dispose();
                return OperationResult<IInternalSceneTransitionLease>.Failure(ModErrorCode.Cancelled, "The scene owner has stopped.");
            }
            return OperationResult<IInternalSceneTransitionLease>.Success(native);
        }

        public OperationResult<IInternalNativeSceneOperation> TryDispatch(
            NativeSceneRequest request, IInternalNativeSceneDispatch dispatch, CancellationToken callerToken = default)
        {
            coordinator.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));
            if (!IsAlive || callerToken.IsCancellationRequested)
                return OperationResult<IInternalNativeSceneOperation>.Failure(ModErrorCode.Cancelled, "The scene owner or caller has stopped.");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, callerToken);
            OperationResult<IInternalNativeSceneOperation> result;
            if (access?.Borrowed is IInternalSceneTransitionService borrowed)
            {
                result = borrowed.TryDispatch(request, dispatch, linked.Token);
            }
            else
            {
                var admission = Acquire(request.SceneName, request.Automatic, request.Reason);
                if (!admission.TryGetValue(out var lease))
                {
                    linked.Dispose();
                    return OperationResult<IInternalNativeSceneOperation>.Failure(admission.ErrorCode, admission.ErrorMessage);
                }
                try { result = lease.Transitions.TryDispatch(request, dispatch, linked.Token); }
                finally { lease.Dispose(); }
            }
            if (result.TryGetValue(out var operation))
                _ = operation.Completion.ContinueWith(_ => linked.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else linked.Dispose();
            return result;
        }
        private sealed class LifetimeLease : IInternalSceneTransitionLease
        {
            private readonly IInternalSceneTransitionLease lease;
            private readonly CancellationTokenRegistration stopping;
            public LifetimeLease(IInternalSceneTransitionLease lease, CancellationToken token)
            {
                this.lease = lease;
                stopping = token.Register(lease.Dispose);
            }
            public IInternalSceneTransitionService Transitions => lease.Transitions;
            public void Dispose() { stopping.Dispose(); lease.Dispose(); }
        }
    }
}
