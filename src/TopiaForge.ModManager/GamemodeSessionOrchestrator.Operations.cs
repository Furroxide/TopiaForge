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
        internal Task<OperationResult<bool>> StopAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var completion = Completion();
            dispatcher.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested) completion.TrySetResult(ExceptionFailure(new OperationCanceledException()));
                else if (current == null || current.Identity.SessionId != sessionId) completion.TrySetResult(AdmissionFailure(SessionAdmission.StaleSession));
                else
                {
                    var record = current;
                    RequestBoundStop(sessionId);
                    // Acceptance is distinct from cleanup; a callback may await its own stop request.
                    completion.TrySetResult(OperationResult<bool>.Success(true));
                }
            });
            return completion.Task;
        }

        internal Task<OperationResult<bool>> RestartAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var completion = Completion();
            _ = dispatcher.InvokeAsync(() =>
            {
                if (current == null || current.Identity.SessionId != sessionId)
                    completion.TrySetResult(AdmissionFailure(SessionAdmission.StaleSession));
                else BeginLaunch(current.Identity.Selection, Guid.NewGuid().ToString("N"), cancellationToken, completion, sessionId);
            });
            return completion.Task;
        }

        private void RequestBoundStop(string sessionId)
        {
            if (!dispatcher.IsCurrent || publishing != 0
                || (lifecycle.HasOperation && lifecycle.Current.Phase == SessionPhase.Running))
            { dispatcher.Post(() => RequestBoundStop(sessionId)); return; }
            var record = current;
            if (record == null || record.Identity.SessionId != sessionId || record.StopRequested) return;
            if (!record.Starting)
            {
                var admission = lifecycle.TryAcquire(false, sessionId, out var lease);
                if (admission != SessionAdmission.Accepted) return;
                record.Lease = lease!;
            }
            CancelRecord(record);
            if (!record.Starting) RunDriver(async () => { await CleanupAsync(record); return true; });
        }

        public Task<OperationResult<bool>> StopOwnerAsync(string packageId)
        {
            var completion = Completion();
            dispatcher.Post(() =>
            {
                var record = current;
                if (record == null || !record.Plan.Packages.Any(package => Same(package.Id, packageId)))
                    completion.TrySetResult(OperationResult<bool>.Success(false));
                else { RequestBoundStop(record.Identity.SessionId); Forward(record.Stopped.Task, completion); }
            });
            return completion.Task;
        }

        public Task<OperationResult<bool>> ShutdownAsync()
        {
            var completion = Completion();
            _ = dispatcher.InvokeAsync(() =>
            {
                // Close admission at the host request; actual stop runs after current notifications settle.
                shuttingDown = true;
                dispatcher.Post(() =>
                {
                    var failures = new List<Exception>();
                    try { menuCancellation?.Cancel(); } catch (Exception error) { failures.Add(error); }
                    var record = current;
                    try { if (record != null) RequestBoundStop(record.Identity.SessionId); }
                    catch (Exception error) { failures.Add(error); }
                    var draining = activeWork;
                    RunDriver(async () =>
                    {
                        // A replacement may pass through Idle while its predecessor finishes.
                        // Wait for the whole admitted operation, including menu callbacks and successor cleanup.
                        var operationFaulted = false;
                        try { if (draining != null) await draining; }
                        catch (Exception error) { operationFaulted = true; failures.Add(error); }
                        try { await native.WaitForIdleAsync(); }
                        catch (Exception error) { failures.Add(error); }
                        if (record != null && (!operationFaulted || record.Stopped.Task.IsCompleted))
                        {
                            try
                            {
                                var stopped = await record.Stopped.Task;
                                if (!stopped.Succeeded) failures.Add(new InvalidOperationException(stopped.ErrorMessage));
                            }
                            catch (Exception error) { failures.Add(error); }
                        }
                        foreach (var error in failures) Report(error);
                        completion.TrySetResult(failures.Count == 0 ? OperationResult<bool>.Success(true)
                            : OperationResult<bool>.Failure(ModErrorCode.External,
                                Message(string.Join("; ", failures.Select(error => error.GetBaseException().Message)))));
                        return true;
                    }, ownsOperation: false);
                });
            });
            return completion.Task;
        }

        private static void Forward(Task<OperationResult<bool>> task, TaskCompletionSource<OperationResult<bool>> completion)
        {
            task.ContinueWith(done =>
            {
                if (done.IsCanceled) completion.TrySetResult(ExceptionFailure(new OperationCanceledException()));
                else if (done.Exception != null) completion.TrySetResult(ExceptionFailure(done.Exception));
                else completion.TrySetResult(done.Result);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
