using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class GamemodeSessionOrchestrator
    {
        private async Task<bool> LaunchAsync(SessionRecord record, CancellationToken callerToken)
        {
            if (current != null)
            {
                var previous = current;
                previous.Lease = record.Lease;
                CancelRecord(previous);
                await CleanupAsync(previous, retainLease: true, replacementReserved: true);
                if (previous.Errors.Count > 0)
                {
                    await record.Reservation!.CloseAsync();
                    lifecycle.Release(record.Lease);
                    CompleteCommand(record.Start, record.Identity.RequestId, "launch-target", null,
                        OperationResult<bool>.Failure(ModErrorCode.External, "The previous session failed to clean up."));
                    record.Cancellation.Dispose();
                    return false;
                }
            }
            current = record;
            try
            {
                Commit(record, SessionPhase.Preparing);
                foreach (var package in new[] { record.Mode.Package, record.World.Package, Owner(record.Plan, record.Plan.TargetId) }.Distinct())
                {
                    var slot = new NativeTransitionAccessSlot(record.Identity.SessionId + ":" + package.Id,
                        record.Identity.SessionId, () => !record.StopRequested);
                    record.Slots.Add(package.Id, slot);
                    var scope = await record.Snapshot.Contexts[package.Id].CreateChildScopeAsync(record.Identity.SessionId,
                        record.Cancellation.Token, () => RequestBoundStop(record.Identity.SessionId), slot, dispatcher);
                    record.Scopes.Add(package.Id, scope);
                }
                record.CallerCancellation = callerToken.Register(() => dispatcher.Post(() => RequestBoundStop(record.Identity.SessionId)));
                if (callerToken.IsCancellationRequested) CancelRecord(record);
                ThrowIfStopping(record);
                record.Provider = await dispatcher.InvokeAsync(record.World.Create);
                ThrowIfStopping(record);
                Commit(record, SessionPhase.LoadingWorld);
                ThrowIfStopping(record);
                var worldContext = new WorldLoadContext(record.Identity, record.Scopes[record.World.Package.Id].Context, record.Plan.World.Spawn!);
                OperationResult<IWorldInstance> loaded;
                using (record.Slots[record.World.Package.Id].Install(record.Reservation!.BorrowFor(record.World.Package.Id, record.Identity.SessionId)))
                    loaded = await dispatcher.InvokeCallbackAsync(() => record.Provider.LoadAsync(worldContext, record.Cancellation.Token));
                if (loaded.TryGetValue(out var instance)) record.Instance = instance;
                ThrowIfStopping(record);
                if (!loaded.Succeeded) throw new StartupFailure(loaded.ErrorCode, Message(loaded.ErrorMessage));
                var ready = record.Instance!.Readiness ?? throw new InvalidOperationException("The world provider returned no readiness evidence.");
                record.Readiness = ready;
                Commit(record, SessionPhase.StartingMode);
                ThrowIfStopping(record);
                record.Factory = await dispatcher.InvokeAsync(record.Mode.Create);
                ThrowIfStopping(record);
                var session = new GamemodeSessionView(this, record.Identity, record.Scopes[record.Mode.Package.Id].Context,
                    record.Cancellation.Token, ready);
                var started = await dispatcher.InvokeCallbackAsync(() => record.Factory.StartAsync(session, record.Cancellation.Token));
                if (started.TryGetValue(out var controller)) record.Controller = controller;
                ThrowIfStopping(record);
                if (!started.Succeeded) throw new StartupFailure(started.ErrorCode, Message(started.ErrorMessage));
                await record.Reservation!.CloseAsync();
                record.Reservation = null;
                ThrowIfStopping(record);
                record.Starting = false;
                record.CallerCancellation.Dispose();
                record.ReachedRunning = true;
                Commit(record, SessionPhase.Running);
                lifecycle.Release(record.Lease);
                CompleteCommand(record.Start, record.Identity.RequestId, "launch-target", record.Identity.SessionId, OperationResult<bool>.Success(true));
                return true;
            }
            catch (Exception error)
            {
                if (!(error is OperationCanceledException) && !(error is StartupFailure cancelled && cancelled.Code == ModErrorCode.Cancelled))
                    record.Errors.Add(error);
                var failure = error is StartupFailure startup
                    ? OperationResult<bool>.Failure(startup.Code, startup.Message) : ExceptionFailure(error);
                CancelRecord(record, failure);
                record.Starting = false;
                await CleanupAsync(record);
                return false;
            }
        }

        private void CancelRecord(SessionRecord record, OperationResult<bool>? launchFailure = null)
        {
            if (record.StopRequested) return;
            record.StopRequested = true;
            if (ReferenceEquals(current, record) && lifecycle.Current.Phase != SessionPhase.Stopping)
                Commit(record, SessionPhase.Stopping);
            TryCleanup(record, record.Cancellation.Cancel);
            foreach (var scope in record.Scopes.Values) TryCleanup(record, scope.BeginStop);
            if (record.Starting)
                CompleteCommand(record.Start, record.Identity.RequestId, "launch-target", record.Identity.SessionId,
                    launchFailure ?? OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Session startup was cancelled."));
        }

        private void ThrowIfStopping(SessionRecord record)
        {
            if (record.StopRequested || shuttingDown) throw new OperationCanceledException("The session stopped before startup completed.");
        }
        private sealed class StartupFailure : Exception
        {
            internal StartupFailure(ModErrorCode code, string message) : base(message) { Code = code; }
            internal ModErrorCode Code { get; }
        }
    }
}
