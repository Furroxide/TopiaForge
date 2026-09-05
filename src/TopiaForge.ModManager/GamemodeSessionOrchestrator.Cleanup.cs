using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class GamemodeSessionOrchestrator
    {
        private async Task CleanupAsync(SessionRecord record, bool retainLease = false, bool replacementReserved = false)
        {
            if (record.TerminalPublished) return;
            CancelRecord(record);
            if (record.Reservation != null)
            {
                var reservation = record.Reservation;
                record.Reservation = null;
                try { await reservation.CloseAsync(); } catch (Exception error) { record.Errors.Add(error); }
            }
            else if (!replacementReserved)
            {
                try { await native.WaitForIdleAsync(); } catch (Exception error) { record.Errors.Add(error); }
            }
            // Clear owned references before invoking extension code; reentrancy cannot dispose them twice.
            var resources = new IDisposable?[] { record.Controller, record.Factory as IDisposable, record.Instance, record.Provider as IDisposable };
            record.Controller = null;
            record.Factory = null;
            record.Instance = null;
            record.Provider = null;
            var disposed = new List<IDisposable>();
            foreach (var resource in resources)
            {
                if (resource == null || disposed.Any(previous => ReferenceEquals(previous, resource))) continue;
                disposed.Add(resource);
                TryCleanup(record, resource.Dispose);
            }
            foreach (var scope in record.Scopes.Values.Reverse())
            {
                try { await scope.DrainRejectedResourcesAsync(); } catch (Exception error) { record.Errors.Add(error); }
                TryCleanup(record, scope.Dispose);
                try { await scope.DrainRejectedResourcesAsync(); } catch (Exception error) { record.Errors.Add(error); }
            }
            TryCleanup(record, record.CallerCancellation.Dispose);
            TryCleanup(record, record.Cancellation.Dispose);
            record.Scopes.Clear();
            record.Slots.Clear();
            if (ReferenceEquals(current, record)) current = null;
            Commit(record, SessionPhase.Idle);
            record.TerminalPublished = true;
            foreach (var error in record.Errors) Report(error);
            var result = record.Errors.Count == 0 ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(ModErrorCode.External, Message(string.Join("; ", record.Errors.Select(error => Message(error.GetBaseException().Message)))));
            Publish(Outcome, new LaunchOutcome("session", record.Identity.RequestId, NextSequence(), "idle",
                result.Succeeded ? (record.ReachedRunning ? "succeeded" : "cancelled") : "failed", Array.Empty<LaunchBlock>(), record.Identity.SessionId,
                error: result.Succeeded ? null : new LaunchExecutionError("external", result.ErrorMessage)));
            if (!retainLease) lifecycle.Release(record.Lease);
            record.Stopped.TrySetResult(result);
        }

        private static void TryCleanup(SessionRecord record, Action cleanup)
        {
            try { cleanup(); } catch (Exception error) { record.Errors.Add(error); }
        }
    }
}
