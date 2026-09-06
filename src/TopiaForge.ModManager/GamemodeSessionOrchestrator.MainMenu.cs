using System;
using System.Collections.Generic;
using System.Linq;
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
                OperationResult<INativeTransitionReservation> reserved;
                try
                {
                    reserved = native.TryReserve(new NativeTransitionOwner(owner, runtimeOwnershipId + ":menu:" + requestId), requestId);
                }
                catch (Exception error)
                {
                    lifecycle.Release(lease!);
                    CompleteCommand(completion, requestId, "main-menu", null, ExceptionFailure(error));
                    return;
                }
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
            var failures = new List<Exception>();
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
            catch (Exception error)
            {
                result = ExceptionFailure(error);
                if (!(error is OperationCanceledException)) failures.Add(error);
            }
            finally
            {
                // A late close failure is still terminal cleanup evidence; it cannot strand
                // the command task or the lifecycle lease after native ownership has drained.
                try { await reservation.CloseAsync(); }
                catch (Exception error) { failures.Add(error); }
                menuCancellation = null;
            }
            if (failures.Count != 0)
            {
                result = OperationResult<bool>.Failure(ModErrorCode.External,
                    Message(string.Join("; ", new[] { result.ErrorMessage }.Concat(failures.SelectMany(CleanupMessages))
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))));
                foreach (var error in failures) Report(error);
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
