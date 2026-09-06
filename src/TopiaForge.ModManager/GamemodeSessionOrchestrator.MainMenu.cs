using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class GamemodeSessionOrchestrator
    {
        internal Task<OperationResult<bool>> ReturnToMainMenuAsync(string? sessionId = null, CancellationToken cancellationToken = default)
        {
            var completion = Completion();
            _ = dispatcher.InvokeAsync(() =>
            {
                var requestId = Guid.NewGuid().ToString("N");
                if (shuttingDown || cancellationToken.IsCancellationRequested)
                { CompleteCommand(completion, requestId, "main-menu", null, ExceptionFailure(new OperationCanceledException())); return; }
                var admission = lifecycle.TryAcquire(native.IsSceneBusy, sessionId, out var lease);
                if (admission != SessionAdmission.Accepted)
                { CompleteCommand(completion, requestId, "main-menu", null, AdmissionFailure(admission)); return; }
                var owner = current == null ? "topiaforge.manager" : Owner(current.Plan, current.Plan.TargetId).Id;
                var reserved = native.TryReserve(new NativeTransitionOwner(owner, runtimeOwnershipId + ":menu:" + requestId), requestId);
                if (!reserved.TryGetValue(out var reservation))
                {
                    lifecycle.Release(lease!);
                    CompleteCommand(completion, requestId, "main-menu", null, Failure(reserved));
                    return;
                }
                RunDriver(() => MainMenuAsync(current, lease!, reservation, owner, requestId, cancellationToken, completion));
            });
            return completion.Task;
        }

        private async Task<bool> MainMenuAsync(SessionRecord? previous, SessionOperationLease lease,
            INativeTransitionReservation reservation, string owner, string requestId, CancellationToken cancellationToken,
            TaskCompletionSource<OperationResult<bool>> completion)
        {
            var result = OperationResult<bool>.Success(true);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            menuCancellation = cancellation;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (previous != null)
                {
                    previous.Lease = lease;
                    CancelRecord(previous);
                    await CleanupAsync(previous, retainLease: true, replacementReserved: true);
                    if (previous.Errors.Count > 0) throw new InvalidOperationException("The previous session failed to clean up.");
                }
                if (shuttingDown) cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                using (var grant = reservation.BorrowFor(owner, requestId))
                    result = await dispatcher.InvokeCallbackAsync(() => environment.LoadMainMenuAsync(grant.SceneTransitions, cancellation.Token));
            }
            catch (Exception error) { result = ExceptionFailure(error); }
            finally
            {
                await reservation.CloseAsync();
                menuCancellation = null;
            }
            lifecycle.Release(lease);
            CompleteCommand(completion, requestId, "main-menu", null, result);
            return result.Succeeded;
        }

        public void OnSceneLifecycle(SceneLifecycleEvent scene)
        {
            var observed = lifecycle.Current;
            if (observed.Phase != SessionPhase.Running || scene.IsInitial) return;
            var owner = observed.Identity;
            dispatcher.Post(() =>
            {
                var record = current;
                if (record == null || !ReferenceEquals(record.Identity, owner) || lifecycle.Current.Phase != SessionPhase.Running
                    || record.Plan.Gamemode.SceneChangePolicy != ModGamemodeDeclaration.EndSessionPolicy) return;
                var owned = record.Readiness!.Scene;
                var sameInstance = scene.SceneInstanceId == owned.InstanceId;
                if ((sameInstance && scene.Phase == SceneLifecyclePhase.Unloaded)
                    || (!sameInstance && scene.IsActive && (scene.Phase == SceneLifecyclePhase.Activated
                        || (scene.Phase == SceneLifecyclePhase.Loaded && scene.Mode == SceneLoadMode.Single))))
                    RequestBoundStop(record.Identity.SessionId);
            });
        }
    }
}
