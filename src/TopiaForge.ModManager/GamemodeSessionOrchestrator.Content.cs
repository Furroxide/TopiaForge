using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class GamemodeSessionOrchestrator
    {
        internal Task<OperationResult<bool>> RunContentOperationAsync(ModContext caller, string sessionId,
            Func<IInternalSessionContentContext, CancellationToken, Task<OperationResult<IDisposable>>> callback,
            CancellationToken token)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var completion = Completion();
            _ = dispatcher.InvokeAsync(() =>
            {
                var record = current;
                if (shuttingDown || caller.Lifetime.IsStopping || token.IsCancellationRequested)
                { completion.TrySetResult(ExceptionFailure(new OperationCanceledException())); return; }
                if (record == null || record.Identity.SessionId != sessionId)
                { completion.TrySetResult(AdmissionFailure(SessionAdmission.StaleSession)); return; }
                if (!record.Snapshot.Contexts.TryGetValue(caller.Identity.Id, out var root) || !caller.IsWithin(root))
                { completion.TrySetResult(OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The caller is outside this session's selected package contexts.")); return; }
                if (lifecycle.Current.Phase != SessionPhase.Running)
                { completion.TrySetResult(AdmissionFailure(SessionAdmission.Busy)); return; }
                var admission = lifecycle.TryAcquire(native.IsSceneBusy, sessionId, out var lease);
                if (admission != SessionAdmission.Accepted)
                { completion.TrySetResult(AdmissionFailure(admission)); return; }
                var workId = sessionId + ":content:" + Guid.NewGuid().ToString("N");
                OperationResult<INativeTransitionReservation> reserved;
                try
                {
                    reserved = native.TryReserve(new NativeTransitionOwner(caller.Identity.Id,
                        runtimeOwnershipId + ":" + workId, sessionId), workId);
                }
                catch (Exception error)
                {
                    lifecycle.Release(lease!);
                    completion.TrySetResult(ExceptionFailure(error));
                    return;
                }
                if (!reserved.TryGetValue(out var reservation))
                { lifecycle.Release(lease!); completion.TrySetResult(Failure(reserved)); return; }
                record.Lease = lease!;
                record.ContentOperation = true;
                RunDriver(() => RunContentAsync(record, caller, workId, reservation, callback, token, completion));
            });
            return completion.Task;
        }

        private async Task<bool> RunContentAsync(SessionRecord record, ModContext caller, string workId,
            INativeTransitionReservation reservation,
            Func<IInternalSessionContentContext, CancellationToken, Task<OperationResult<IDisposable>>> callback,
            CancellationToken token, TaskCompletionSource<OperationResult<bool>> completion)
        {
            var failures = new List<Exception>();
            var result = OperationResult<bool>.Failure(ModErrorCode.External, "The content operation did not complete.");
            var slot = new NativeTransitionAccessSlot(workId, record.Identity.SessionId, () => !record.StopRequested && !caller.Lifetime.IsStopping);
            ModContextScope? scope = null;
            IDisposable? content = null;
            using var cancellation = new CancellationTokenSource();
            void Cancel()
            {
                try { cancellation.Cancel(); } catch (Exception error) { failures.Add(error); }
                try { scope?.BeginStop(); } catch (Exception error) { failures.Add(error); }
            }
            using var callerCancellation = token.Register(() => dispatcher.Post(Cancel));
            using var sessionCancellation = record.Cancellation.Token.Register(() => dispatcher.Post(Cancel));
            using var ownerCancellation = caller.Lifetime.StoppingToken.Register(() => dispatcher.Post(Cancel));
            try
            {
                if (token.IsCancellationRequested || record.StopRequested || caller.Lifetime.IsStopping) Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                scope = await caller.CreateChildScopeAsync(workId, record.Cancellation.Token,
                    () => RequestBoundStop(record.Identity.SessionId), slot, dispatcher);
                record.Scopes.Add(workId, scope);
                record.Slots.Add(workId, slot);
                if (cancellation.IsCancellationRequested) scope.BeginStop();
                cancellation.Token.ThrowIfCancellationRequested();
                OperationResult<IDisposable> loaded;
                using (slot.Install(reservation.BorrowFor(caller.Identity.Id, record.Identity.SessionId)))
                {
                    loaded = await dispatcher.InvokeCallbackAsync(() => callback(new SessionContentContext(record.Identity.SessionId,
                        record.Readiness!, scope.Context), cancellation.Token));
                    // Take ownership before disposing the borrowed grant: revocation may itself fail.
                    if (loaded.TryGetValue(out var value)) content = value;
                }
                result = loaded.Succeeded ? OperationResult<bool>.Success(true) : Failure(loaded);
                if (token.IsCancellationRequested || record.StopRequested || caller.Lifetime.IsStopping) Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                result = ExceptionFailure(error);
                if (!(error is OperationCanceledException)) failures.Add(error);
            }
            if (!result.Succeeded) Cancel();
            try { await reservation.CloseAsync(); } catch (Exception error) { failures.Add(error); }
            if (scope != null)
            {
                try { await scope.DrainNativeWorkAsync(); } catch (Exception error) { failures.Add(error); }
            }
            if (token.IsCancellationRequested || record.StopRequested || caller.Lifetime.IsStopping)
            {
                Cancel();
                result = ExceptionFailure(new OperationCanceledException());
            }
            // Cleanup also cancels the operation token. Only external stop/cancellation above
            // may replace a delivered provider failure with Cancelled.
            if (failures.Count != 0) result = OperationResult<bool>.Failure(ModErrorCode.External, "Content operation cleanup failed.");
            if (result.Succeeded && content != null)
                record.ContentResources.Add(content);
            else
            {
                try { scope?.BeginStop(); } catch (Exception error) { failures.Add(error); }
                try { content?.Dispose(); } catch (Exception error) { failures.Add(error); }
                if (scope != null)
                    try { await scope.CloseAsync(); } catch (Exception error) { failures.Add(error); }
                record.Scopes.Remove(workId);
                record.Slots.Remove(workId);
            }
            if (failures.Count != 0)
            {
                record.Errors.AddRange(failures);
                result = OperationResult<bool>.Failure(ModErrorCode.External,
                    Message(result.ErrorMessage + " " + string.Join("; ", failures.SelectMany(CleanupMessages).Distinct(StringComparer.Ordinal))));
                foreach (var error in failures) Report(error);
            }
            record.ContentOperation = false;
            if (record.StopRequested) await CleanupAsync(record);
            else lifecycle.Release(record.Lease);
            completion.TrySetResult(result);
            return result.Succeeded;
        }

        private sealed class SessionContentContext : IInternalSessionContentContext
        {
            internal SessionContentContext(string sessionId, WorldReadiness world, ModContext context)
            { SessionId = sessionId; World = world; Context = context; }
            public string SessionId { get; }
            public WorldReadiness World { get; }
            public IModContext Context { get; }
        }
    }
}
