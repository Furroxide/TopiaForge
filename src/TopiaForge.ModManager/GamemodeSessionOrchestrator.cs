using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Authoritative declaration lifecycle composed with verified runtime bindings.</summary>
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
        internal bool CanHostCurrentWorld => !(current?.World is MapWorldImplementation)
            && !(current?.Instance is TopiaForge.Mods.Internal.IInternalMapWorldInstance);

        /// <summary>
        /// Gets the local session's phase as the multiplayer realm rules read it. An idle session still counts as stopping
        /// while it changes: a return to the main menu that has ended the session but still loads the menu, any other
        /// operation or native scene change that holds it, and shutdown. The orchestrator refuses to start a session then,
        /// so a join, which replaces the local session, waits as well.
        /// </summary>
        internal SessionPhase RealmPhase
        {
            get
            {
                var phase = lifecycle.Current.Phase;
                return phase == SessionPhase.Idle && (shuttingDown || lifecycle.HasOperation || native.IsSceneBusy) ? SessionPhase.Stopping : phase;
            }
        }

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
            // A joined player's world is the host's to choose (WS3.8): its session follower loads it, and nothing else may.
            if (Follower?.IsFollowing == true)
            {
                CompleteCommand(completion, requestId, "launch-target", null,
                    OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, FollowerCannotLaunch));
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
                if (snapshot.PackageCleanupPending)
                {
                    lifecycle.Release(lease!);
                    CompleteCommand(completion, requestId, "launch-target", null,
                        OperationResult<bool>.Failure(ModErrorCode.Conflict, "Package native cleanup is Busy."));
                    return;
                }
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
                var record = new SessionRecord(identity, plan, snapshot, mode, world, lease!, reservation, completion,
                    RealmOf(snapshot.Contexts[modeOwner.Id]));
                RunDriver(() => LaunchAsync(record, cancellationToken));
            }
            catch (Exception error)
            {
                reservation?.Dispose();
                lifecycle.Release(lease!);
                CompleteCommand(completion, requestId, "launch-target", null, ExceptionFailure(error));
            }
        }

        private void RunDriver(Func<Task<bool>> callback, bool ownsOperation = true, bool followed = false)
        {
            var work = dispatcher.InvokeCallbackAsync(callback);
            if (ownsOperation) activeWork = work;
            work.ContinueWith(task =>
            {
                if (task.Exception != null) dispatcher.Post(() => Report(task.Exception.Flatten(), followed));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void Commit(SessionRecord record, SessionPhase phase)
        {
            var state = lifecycle.Commit(record.Lease, phase, phase == SessionPhase.Preparing ? record.Identity : null);
            publicState = CapturePublicState(state, record);
            Publish(StateChanged, state);
            Publish(publicStateChanged, publicState);
            if (ReportsToLauncher(record))
                Publish(Progress, new LaunchProgress(record.Identity.RequestId, NextSequence(), PhaseName(phase),
                    phase == SessionPhase.Idle ? null : record.Identity.SessionId, native.IsSceneBusy));
        }

        /// <summary>
        /// Gets whether a session's progress and terminal outcome go to the launcher: every session this game starts, under its
        /// own request, and a follow of the host's world only when it belongs to a launcher-planned join, under that join's
        /// request (WS3.8). A follow of a join started in game publishes nothing to the launcher.
        /// </summary>
        private static bool ReportsToLauncher(SessionRecord record) => record.Follow == null || record.Follow.LaunchRequestId != null;

        private void CompleteCommand(TaskCompletionSource<OperationResult<bool>> completion, string requestId, string command,
            string? sessionId, OperationResult<bool> result, IEnumerable<LaunchBlock>? blocks = null)
        {
            // SDK failures can contain an undefined enum value. Normalize before either the
            // command task or its wire outcome receives author-controlled result metadata.
            if (!result.Succeeded) result = Failure(result);
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
        private void Report(Exception error, bool followed = false)
        {
            if (DiagnosticFailure == null) return;
            // Follow failures may include host-selected content. Keep neither the
            // original exception nor its inner exceptions/stack in a diagnostic.
            if (followed) error = new InvalidOperationException(SessionFollower.Scrub(string.Join("; ", CleanupMessages(error))));
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
        private static OperationResult<bool> Failure<T>(OperationResult<T> result) where T : notnull
        {
            var code = result.ErrorCode;
            if (code == ModErrorCode.None || !Enum.IsDefined(typeof(ModErrorCode), code))
                return OperationResult<bool>.Failure(ModErrorCode.External, Message(
                    "An implementation returned an unsupported operation error code (" + (int)code + "). " + result.ErrorMessage));
            return OperationResult<bool>.Failure(code, Message(result.ErrorMessage));
        }
        private static OperationResult<bool> ExceptionFailure(Exception error) => OperationResult<bool>.Failure(
            error is OperationCanceledException ? ModErrorCode.Cancelled
                : error.GetBaseException() is InvalidRuntimeSelectionException ? ModErrorCode.InvalidState : ModErrorCode.External,
            Message(error.GetBaseException().Message));
        private static OperationResult<bool> AdmissionFailure(SessionAdmission admission) => OperationResult<bool>.Failure(
            admission == SessionAdmission.StaleSession ? ModErrorCode.InvalidState : ModErrorCode.Conflict,
            admission == SessionAdmission.StaleSession ? "This session handle no longer owns the current session." : BusyMessage);

        /// <summary>Why a command was not admitted while another session operation or native transition runs; it may be tried again.</summary>
        internal const string BusyMessage = "A session or native transition is busy.";
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static PackageIdentity Owner(LaunchPlan plan, string id) => plan.Packages.OrderByDescending(package => package.Id.Length)
            .First(package => id.StartsWith(package.Id + ".", StringComparison.OrdinalIgnoreCase));

        private sealed class SessionRecord
        {
            internal SessionRecord(SessionIdentity identity, LaunchPlan plan, RuntimeSessionSnapshot snapshot,
                ISessionImplementation<IGamemodeFactory> mode, ISessionImplementation<IWorldContentProvider> world,
                SessionOperationLease lease, INativeTransitionReservation reservation, TaskCompletionSource<OperationResult<bool>> start,
                NetRealm realm)
            { Identity = identity; Plan = plan; Snapshot = snapshot; Mode = mode; World = world; Lease = lease; Reservation = reservation; Start = start; Realm = realm; }
            internal readonly SessionIdentity Identity;
            /// <summary>The realm the session started in, which decides what of the gamemode runs here; see Participant.cs.</summary>
            internal readonly NetRealm Realm;
            internal readonly LaunchPlan Plan;
            internal readonly RuntimeSessionSnapshot Snapshot;
            internal readonly ISessionImplementation<IGamemodeFactory> Mode;
            internal readonly ISessionImplementation<IWorldContentProvider> World;
            internal SessionOperationLease Lease;
            internal INativeTransitionReservation? Reservation;
            internal readonly TaskCompletionSource<OperationResult<bool>> Start;
            /// <summary>Gets the host's world this session follows on a joined player (WS3.8); null for a session this game started.</summary>
            internal SessionFollowPlan? Follow;
            /// <summary>Gets this session as a listen host announces it to its players, once described (WS3.8).</summary>
            internal HostWorldState? Announced;
            internal readonly TaskCompletionSource<OperationResult<bool>> Stopped = Completion();
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal readonly Dictionary<string, ModContextScope> Scopes = new Dictionary<string, ModContextScope>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, NativeTransitionAccessSlot> Slots = new Dictionary<string, NativeTransitionAccessSlot>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<Exception> Errors = new List<Exception>();
            internal readonly List<IDisposable> ContentResources = new List<IDisposable>();
            internal bool ContentOperation;
            internal IWorldContentProvider? Provider;
            internal IWorldInstance? Instance;
            internal WorldReadiness? Readiness;
            internal bool PresenceInvalidated;
            internal IGamemodeFactory? Factory;
            internal IGamemodeController? Controller;
            internal IGamemodeParticipantController? ParticipantController;
            internal CancellationTokenRegistration CallerCancellation;
            internal bool Starting = true;
            internal bool ReachedRunning;
            internal bool StopRequested;
            internal bool TerminalPublished;
        }
    }
}
