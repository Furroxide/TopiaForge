using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Inactive declaration lifecycle. Binding/catalog activation supplies its production environment later.</summary>
    internal sealed partial class GamemodeSessionOrchestrator : IRuntimeSessionShutdown, IRuntimeSessionSceneObserver
    {
        private readonly IHostDispatcher dispatcher;
        private readonly INativeTransitionExecutor native;
        private readonly IRuntimeSessionEnvironment environment;
        private readonly string runtimeOwnershipId;
        private readonly SessionLifecycle lifecycle = new SessionLifecycle();
        private SessionRecord? current;
        private bool shuttingDown;
        private int sequence;
        private int publishing;
        private Task<bool>? activeWork;
        private CancellationTokenSource? menuCancellation;

        internal GamemodeSessionOrchestrator(IHostDispatcher dispatcher, INativeTransitionExecutor native,
            IRuntimeSessionEnvironment environment, string runtimeOwnershipId)
        {
            this.dispatcher = dispatcher;
            this.native = native;
            this.environment = environment;
            this.runtimeOwnershipId = runtimeOwnershipId;
            native.SetSessionAdmissionGate(() => shuttingDown || lifecycle.HasOperation
                || (lifecycle.Current.Phase != SessionPhase.Idle && lifecycle.Current.Phase != SessionPhase.Running));
        }

        internal SessionStateSnapshot Current => lifecycle.Current;
        internal event Action<SessionStateSnapshot>? StateChanged;
        internal event Action<LaunchProgress>? Progress;
        internal event Action<LaunchOutcome>? Outcome;
        internal event Action<Exception>? DiagnosticFailure;

        internal Task<OperationResult<bool>> StartAsync(LaunchPlanDescriptor descriptor, string requestId,
            CancellationToken cancellationToken = default)
        {
            _ = new LaunchProgress(requestId, 0, "idle");
            var completion = Completion();
            _ = dispatcher.InvokeAsync(() => BeginLaunch(descriptor, requestId, cancellationToken, completion));
            return completion.Task;
        }

        private void BeginLaunch(LaunchPlanDescriptor descriptor, string requestId, CancellationToken cancellationToken,
            TaskCompletionSource<OperationResult<bool>> completion, string? expectedSessionId = null)
        {
            if (shuttingDown || cancellationToken.IsCancellationRequested)
            {
                CompleteCommand(completion, requestId, "launch-target", null,
                    OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The launch was cancelled before preparation."));
                return;
            }
            var admission = lifecycle.TryAcquire(native.IsSceneBusy, expectedSessionId, out var lease);
            if (admission != SessionAdmission.Accepted)
            {
                CompleteCommand(completion, requestId, "launch-target", null, AdmissionFailure(admission));
                return;
            }
            INativeTransitionReservation? reservation = null;
            try
            {
                var snapshot = environment.Capture();
                var resolved = LaunchResolver.ResolveAgain(descriptor, snapshot.Profile, snapshot.Observation, snapshot.Bindings);
                if (!resolved.Resolved)
                {
                    lifecycle.Release(lease!);
                    CompleteCommand(completion, requestId, "launch-target", null,
                        OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The loaded package selection cannot launch this target."), resolved.Blocks);
                    return;
                }
                var plan = resolved.Plan!;
                var modeOwner = Owner(plan, plan.GamemodeId);
                var worldOwner = Owner(plan, plan.WorldId);
                var mode = snapshot.Gamemodes.SingleOrDefault(item => item.Package.Equals(modeOwner) && Same(item.DeclarationId, plan.GamemodeId));
                var world = snapshot.Worlds.SingleOrDefault(item => item.Package.Equals(worldOwner) && Same(item.DeclarationId, plan.WorldFamilyId ?? plan.WorldId));
                if (mode == null || world == null) throw new InvalidOperationException("The binding snapshot has no matching activation constructor.");
                foreach (var package in new[] { modeOwner, worldOwner, Owner(plan, plan.TargetId) }.Distinct())
                    if (!snapshot.Contexts.TryGetValue(package.Id, out var context) || context.Lifetime.IsStopping)
                        throw new InvalidOperationException("The selected package context is unavailable: " + package.Id);
                var identity = new SessionIdentity(Guid.NewGuid().ToString("N"), requestId, plan.Descriptor);
                var reserved = native.TryReserve(new NativeTransitionOwner(worldOwner.Id,
                    runtimeOwnershipId + ":" + identity.SessionId, identity.SessionId), requestId);
                if (!reserved.TryGetValue(out reservation))
                {
                    lifecycle.Release(lease!);
                    CompleteCommand(completion, requestId, "launch-target", null, Failure(reserved));
                    return;
                }
                var record = new SessionRecord(identity, plan, snapshot, mode, world, lease!, reservation, completion);
                RunDriver(() => LaunchAsync(record, cancellationToken));
            }
            catch (Exception error)
            {
                reservation?.Dispose();
                lifecycle.Release(lease!);
                CompleteCommand(completion, requestId, "launch-target", null, ExceptionFailure(error));
            }
        }

        private void RunDriver(Func<Task<bool>> callback, bool ownsOperation = true)
        {
            var work = dispatcher.InvokeCallbackAsync(callback);
            if (ownsOperation) activeWork = work;
            work.ContinueWith(task =>
            {
                if (task.Exception != null) dispatcher.Post(() => Report(task.Exception.Flatten()));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void Commit(SessionRecord record, SessionPhase phase)
        {
            var state = lifecycle.Commit(record.Lease, phase, phase == SessionPhase.Preparing ? record.Identity : null);
            Publish(StateChanged, state);
            Publish(Progress, new LaunchProgress(record.Identity.RequestId, NextSequence(), PhaseName(phase),
                phase == SessionPhase.Idle ? null : record.Identity.SessionId, native.IsSceneBusy));
        }

        private void CompleteCommand(TaskCompletionSource<OperationResult<bool>> completion, string requestId, string command,
            string? sessionId, OperationResult<bool> result, IEnumerable<LaunchBlock>? blocks = null)
        {
            if (!completion.TrySetResult(result)) return;
            var reasons = blocks?.ToArray() ?? Array.Empty<LaunchBlock>();
            var status = result.Succeeded ? "succeeded" : result.ErrorCode == ModErrorCode.Cancelled ? "cancelled" : "failed";
            Publish(Outcome, new LaunchOutcome("launch", requestId, NextSequence(), PhaseName(lifecycle.Current.Phase), status,
                reasons, sessionId, command, !result.Succeeded && reasons.Length == 0
                    ? new LaunchExecutionError(ErrorName(result.ErrorCode), Message(result.ErrorMessage)) : null));
        }

        private void Publish<T>(Action<T>? handlers, T value)
        {
            if (handlers == null) return;
            publishing++;
            try
            {
                foreach (var handler in handlers.GetInvocationList())
                    try { ((Action<T>)handler)(value); } catch (Exception error) { Report(error); }
            }
            finally { publishing--; }
        }
        private void Report(Exception error)
        {
            if (DiagnosticFailure == null) return;
            foreach (var handler in DiagnosticFailure.GetInvocationList()) try { ((Action<Exception>)handler)(error); } catch { }
        }
        private int NextSequence() => checked(++sequence);
        private static TaskCompletionSource<OperationResult<bool>> Completion() => new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static string PhaseName(SessionPhase phase) => phase == SessionPhase.LoadingWorld ? "loading-world"
            : phase == SessionPhase.StartingMode ? "starting-mode" : phase.ToString().ToLowerInvariant();
        private static string ErrorName(ModErrorCode code) => char.ToLowerInvariant(code.ToString()[0]) + code.ToString().Substring(1);
        private static string Message(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "The session operation failed.";
            var scalars = 0; var cut = 0;
            for (var offset = 0; offset < text.Length; offset++)
            {
                if (scalars == 4095) cut = offset;
                if (++scalars > 4096) return text.Substring(0, cut) + "…";
                if (char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1])) offset++;
            }
            return text;
        }
        private static OperationResult<bool> Failure<T>(OperationResult<T> result) where T : notnull => OperationResult<bool>.Failure(result.ErrorCode, Message(result.ErrorMessage));
        private static OperationResult<bool> ExceptionFailure(Exception error) => OperationResult<bool>.Failure(
            error is OperationCanceledException ? ModErrorCode.Cancelled : ModErrorCode.External, Message(error.GetBaseException().Message));
        private static OperationResult<bool> AdmissionFailure(SessionAdmission admission) => OperationResult<bool>.Failure(
            admission == SessionAdmission.StaleSession ? ModErrorCode.InvalidState : ModErrorCode.Conflict,
            admission == SessionAdmission.StaleSession ? "This session handle no longer owns the current session." : "A session or native transition is busy.");
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static PackageIdentity Owner(LaunchPlan plan, string id) => plan.Packages.OrderByDescending(package => package.Id.Length)
            .First(package => id.StartsWith(package.Id + ".", StringComparison.OrdinalIgnoreCase));

        private sealed class SessionRecord
        {
            internal SessionRecord(SessionIdentity identity, LaunchPlan plan, RuntimeSessionSnapshot snapshot,
                SessionImplementation<IGamemodeFactory> mode, SessionImplementation<IWorldContentProvider> world,
                SessionOperationLease lease, INativeTransitionReservation reservation, TaskCompletionSource<OperationResult<bool>> start)
            { Identity = identity; Plan = plan; Snapshot = snapshot; Mode = mode; World = world; Lease = lease; Reservation = reservation; Start = start; }
            internal readonly SessionIdentity Identity;
            internal readonly LaunchPlan Plan;
            internal readonly RuntimeSessionSnapshot Snapshot;
            internal readonly SessionImplementation<IGamemodeFactory> Mode;
            internal readonly SessionImplementation<IWorldContentProvider> World;
            internal SessionOperationLease Lease;
            internal INativeTransitionReservation? Reservation;
            internal readonly TaskCompletionSource<OperationResult<bool>> Start;
            internal readonly TaskCompletionSource<OperationResult<bool>> Stopped = Completion();
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal readonly Dictionary<string, ModContextScope> Scopes = new Dictionary<string, ModContextScope>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, NativeTransitionAccessSlot> Slots = new Dictionary<string, NativeTransitionAccessSlot>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<Exception> Errors = new List<Exception>();
            internal IWorldContentProvider? Provider;
            internal IWorldInstance? Instance;
            internal WorldReadiness? Readiness;
            internal IGamemodeFactory? Factory;
            internal IGamemodeController? Controller;
            internal CancellationTokenRegistration CallerCancellation;
            internal bool Starting = true;
            internal bool ReachedRunning;
            internal bool StopRequested;
            internal bool TerminalPublished;
        }
    }
}
