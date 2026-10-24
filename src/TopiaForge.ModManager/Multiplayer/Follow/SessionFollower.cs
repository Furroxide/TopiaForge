using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A joined player's side of session follow (WS3.8). From link-up until this game leaves, it keeps its own readiness
    /// hold, so the player is Ready only once it runs the host's world, and the follow grant, which lets its scene loads
    /// through the multiplayer authority policy. It applies the host's newest announcement: the host's world session,
    /// through the orchestrator's follow; the level the host is in outside a session, by its scene name; or the main menu.
    /// When the host moves on mid-follow, the follow is cancelled and the new world followed. When this game leaves the
    /// host's world by itself, it follows it again, eight times in a join at most; at the main menu, it leaves the session.
    /// A failure ends the join with its own words (<see cref="FollowFailureWords"/>) and returns the game to the main menu;
    /// the reason the runtime, a provider or a mod gave goes to the manager log alone, without addresses or paths.
    /// Unity-free; main thread only. A frame at rest compares a few fields and allocates nothing.
    /// </summary>
    internal sealed class SessionFollower : ISessionFollowOwner
    {
        internal const int MaximumRefollows = 8;
        internal const string HoldReason = "session follow";

        /// <summary>How often a follow at work says so, keeping the hold's idle bound from ending a join whose world loads slowly.</summary>
        internal const float ProgressSeconds = 1f;

        /// <summary>How long a follow waits before trying again when the game is busy, and how long it keeps trying.</summary>
        internal const float BusyRetrySeconds = 0.25f;

        internal const float BusyGiveUpSeconds = 30f;

        private const int MaximumLoggedReason = 300;

        private readonly GamemodeSessionOrchestrator sessions;
        private readonly FollowedScene scene;
        private readonly Func<IInternalSystemChannel?> channel;
        private readonly Func<IDisposable> grantFollow;
        private readonly Action leave;
        private readonly Func<NetworkWorldProgress> world;
        private readonly Func<CancellationToken, Task>? discover;
        private readonly IHostDispatcher dispatcher;
        private readonly IMultiplayerLog log;
        private readonly WorldWireReader reader = new WorldWireReader();
        private IInternalMultiplayerControl? control;
        private IInternalReadinessHold? hold;
        private IDisposable? grant;
        private HostWorldState? latest;
        private HostWorldState? applied;
        private Pending? pending;
        private TaskCompletionSource<OperationResult<bool>>? arrival;
        private CancellationTokenRegistration arrivalCancel;
        private float progressWait;
        private float busyWait;
        private float busyTotal;
        private int refollows;
        private bool discovered;
        private bool loaded;
        private bool stranded;
        private bool leavingForMenu;
        private bool ending;
        private int arrivalShown = -1;
        private int arrivedScene;

        internal SessionFollower(GamemodeSessionOrchestrator sessions, FollowedScene scene, Func<IInternalSystemChannel?> channel,
            Func<IDisposable> grantFollow, Action leave, Func<NetworkWorldProgress> world, Func<CancellationToken, Task>? discover,
            IHostDispatcher dispatcher, IMultiplayerLog log)
        {
            this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            this.scene = scene ?? throw new ArgumentNullException(nameof(scene));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.grantFollow = grantFollow ?? throw new ArgumentNullException(nameof(grantFollow));
            this.leave = leave ?? throw new ArgumentNullException(nameof(leave));
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.discover = discover;
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Gets where the follow stands, for the connecting overlay.</summary>
        internal SessionFollowStatus Status { get; } = new SessionFollowStatus();

        /// <summary>Gets whether this game is joined and follows its host.</summary>
        internal bool IsJoined { get; private set; }

        public bool IsFollowing => IsJoined;

        /// <summary>Gets whether this follower's readiness hold is open, for tests and diagnostics.</summary>
        internal bool HoldsReadiness => hold != null;

        /// <summary>Gets the announcement this game is in now, as followed; null before the first follow ends.</summary>
        internal HostWorldState? Applied => applied;

        /// <summary>Gets how many times this game followed the host again in this join after leaving its world by itself.</summary>
        internal int Refollows => refollows;

        /// <summary>Gets the announcements that could not be read, for tests and diagnostics.</summary>
        internal long Unreadable { get; private set; }

        /// <summary>
        /// This game linked up to a host. A game the player started meanwhile is never replaced (#224): the join ends with
        /// its own words, and joining again asks "Leave current game?" first. Otherwise the follow holds readiness and waits
        /// for the host's announcement.
        /// </summary>
        internal void Joined(IInternalMultiplayerControl joined)
        {
            if (joined == null) throw new ArgumentNullException(nameof(joined));
            if (IsJoined) return;
            Status.Joined();
            if (sessions.Current.Phase != SessionPhase.Idle || scene.IsGameplay)
            {
                log.Warn("Multiplayer: a game started here while the join resolved, so the join stopped and that game kept running.");
                Status.Fail(FollowFailureWords.For(FollowFailureKind.StartedGameWhileJoining, ModErrorCode.InvalidState));
                leave();
                return;
            }

            IsJoined = true;
            control = joined;
            ending = false;
            latest = null;
            applied = null;
            refollows = 0;
            discovered = false;
            loaded = false;
            stranded = false;
            leavingForMenu = false;
            busyWait = 0f;
            busyTotal = 0f;
            arrivalShown = -1;
            TakeHold();
            try
            {
                grant = grantFollow();
            }
            catch (Exception exception)
            {
                log.Error("Multiplayer: the follow grant could not be taken: " + exception.GetType().Name + ".");
            }
        }

        /// <summary>
        /// This game left the session, or the host ended it: a follow on its way ends, and a game that followed the host
        /// returns to the main menu, unless the player's own return to it led here or the game is shutting down.
        /// </summary>
        internal void Left(bool detaching)
        {
            var wasJoined = IsJoined;
            IsJoined = false;
            var running = pending;
            pending = null;
            running?.Cancellation.Cancel();
            EndArrival();
            ReleaseHold();
            ReleaseGrant();
            control = null;
            ending = false;
            latest = null;
            applied = null;
            if (!wasJoined) return;
            var returnToMenu = !detaching && !leavingForMenu && loaded && (!scene.IsMainMenu || sessions.RunsFollowedSession);
            leavingForMenu = false;
            loaded = false;
            if (!returnToMenu)
            {
                if (Status.Stage != FollowStage.Failed) Status.Ended();
                return;
            }

            if (Status.Stage != FollowStage.Failed) Status.Move(FollowStage.ReturningToMenu, "Returning to the main menu");
            var failed = Status.Stage == FollowStage.Failed;
            var after = running?.Completion ?? Task.CompletedTask;
            _ = after.ContinueWith(_ => dispatcher.Post(() => ReturnToMenu(failed)), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>The player asked to go to the main menu from inside the host's world: this game leaves the session, and the menu loads as that request's own.</summary>
        public void LeaveForMainMenu()
        {
            if (!IsJoined) return;
            leavingForMenu = true;
            ending = true;
            leave();
        }

        /// <summary>One frame while joined: reads the host's announcements, moves the follow on, and checks this game is still in the host's world.</summary>
        internal void Pump(float deltaTime)
        {
            if (!IsJoined || ending) return;
            var elapsed = deltaTime > 0f && !float.IsNaN(deltaTime) && !float.IsInfinity(deltaTime) ? deltaTime : 0f;
            Receive();
            if (pending != null)
            {
                if (!pending.Completion.IsCompleted)
                {
                    if (latest != null && !pending.Superseded && !latest.SameAs(pending.Target))
                    {
                        pending.Superseded = true;
                        pending.Cancellation.Cancel();
                    }

                    WatchArrival();
                    ReportWork(elapsed);
                    return;
                }

                var finished = pending;
                pending = null;
                Finish(finished);
                if (!IsJoined || ending || pending != null) return;
            }

            if (busyWait > 0f)
            {
                busyWait -= elapsed;
                busyTotal += elapsed;
                if (busyWait > 0f) return;
            }

            if (latest == null) return;
            if (!latest.SameAs(applied)) Begin(latest);
            else Verify();
        }

        private void Receive()
        {
            var open = channel();
            if (open == null || !open.IsOpen) return;
            while (open.TryReceive(out var message))
            {
                if (SessionFollowWire.TryRead(reader, message.Payload, out var state)) latest = state;
                else Unreadable++;
            }
        }

        /// <summary>Starts following <paramref name="target"/>: the host's world, its level, or the main menu.</summary>
        private void Begin(HostWorldState target)
        {
            var refollow = applied != null || Status.IsRefollow;
            if (refollow) Status.Refollowing();
            switch (target.Kind)
            {
                case HostWorldKind.World:
                    TakeHold();
                    loaded = true;
                    stranded = false;
                    var plan = new SessionFollowPlan(target.SessionId!, target.Request!, target.GamemodeId!, target.WorldId!,
                        target.WorldFamilyId, target.Transition!, target.Scene, target.Spawn, WaitForWorldAsync);
                    var follow = new Pending(target);
                    pending = follow;
                    Status.Move(FollowStage.Preparing, "Getting your mods ready");
                    follow.World = sessions.FollowAsync(plan, Reached, follow.Cancellation.Token);
                    follow.Completion = follow.World;
                    break;
                case HostWorldKind.Level:
                    TakeHold();
                    loaded = true;
                    stranded = false;
                    var level = new Pending(target);
                    pending = level;
                    Status.Move(FollowStage.LoadingLevel, "Loading the host's level");
                    level.Other = sessions.FollowLevelAsync(target.Scene!, level.Cancellation.Token);
                    level.Completion = level.Other;
                    break;
                default:
                    if (!sessions.RunsFollowedSession && !scene.IsGameplay)
                    {
                        // Already at the menu, where the host is: nothing loads, and the player is Ready there.
                        applied = target;
                        stranded = false;
                        ReleaseHold();
                        Status.Move(FollowStage.AtMenu, "The host is at the main menu");
                        return;
                    }

                    var menu = new Pending(target);
                    pending = menu;
                    Status.Move(FollowStage.ReturningToMenu, "The host went to the main menu");
                    menu.Other = sessions.FollowMainMenuAsync(menu.Cancellation.Token);
                    menu.Completion = menu.Other;
                    break;
            }

            ReportProgress();
        }

        /// <summary>A follow ended: this game is in the host's world, waits to try again, follows the host's newer world, or failed.</summary>
        private void Finish(Pending finished)
        {
            finished.Cancellation.Dispose();
            EndArrival();
            if (finished.Discovery)
            {
                // Whatever the look found, the host's world is followed again; a look that failed leaves it not found.
                if (finished.Completion.IsFaulted)
                    LogReason("Multiplayer: looking for this game's worlds failed", finished.Completion.Exception?.GetBaseException().Message);
                return;
            }

            if (finished.Completion.IsFaulted || finished.Completion.IsCanceled)
            {
                Fail(FollowFailureKind.PrepareFailed, ModErrorCode.External, "The follow faulted: "
                    + (finished.Completion.Exception?.GetBaseException().GetType().Name ?? "cancelled") + ".");
                return;
            }

            if (finished.Superseded) return;
            if (finished.World != null)
            {
                var result = finished.World.Result;
                if (result.Succeeded)
                {
                    Arrived(finished.Target, FollowStage.Running, "In the host's world");
                    return;
                }

                if (result.IsBusy)
                {
                    Busy(result.Reason);
                    return;
                }

                if (result.IsCancelled) return;
                if (result.Failure == FollowFailureKind.WorldNotFound && discover != null && !discovered)
                {
                    // The host found its world among the game's own levels, which this game may not have listed yet.
                    discovered = true;
                    var look = new Pending(finished.Target) { Discovery = true };
                    pending = look;
                    look.Completion = discover(look.Cancellation.Token);
                    return;
                }

                Fail(result.Failure, result.Code, result.Stage + ": " + result.Reason);
                return;
            }

            var other = finished.Other!.Result;
            var toMenu = finished.ToMenu || finished.Target.Kind == HostWorldKind.Menu;
            if (other.Succeeded)
            {
                if (!toMenu) Arrived(finished.Target, FollowStage.InLevel, "In the host's level");
                else if (stranded) Arrived(finished.Target, FollowStage.AtMenu, FollowFailureWords.LevelNotLoaded);
                else Arrived(finished.Target, FollowStage.AtMenu, "The host is at the main menu");
                return;
            }

            if (other.ErrorCode == ModErrorCode.Cancelled) return;
            if (other.ErrorCode == ModErrorCode.Conflict && other.ErrorMessage == GamemodeSessionOrchestrator.BusyMessage)
            {
                Busy(other.ErrorMessage);
                return;
            }

            if (!toMenu && !stranded)
            {
                // The host's level is not one this game can load: the player waits at the menu, joined, for the host's next world.
                LogReason("Multiplayer: the host's level could not be loaded here (" + other.ErrorCode + ")", other.ErrorMessage);
                stranded = true;
                Status.Notify(FollowFailureWords.LevelNotLoaded);
                var menu = new Pending(finished.Target) { ToMenu = true };
                pending = menu;
                menu.Other = sessions.FollowMainMenuAsync(menu.Cancellation.Token);
                menu.Completion = menu.Other;
                return;
            }

            Fail(FollowFailureKind.CleanupFailed, other.ErrorCode, other.ErrorMessage);
        }

        private void Arrived(HostWorldState target, FollowStage stage, string detail)
        {
            applied = target;
            arrivedScene = scene.Version;
            busyTotal = 0f;
            ReleaseHold();
            Status.Move(stage, detail);
            if (Status.IsRefollow && target.Kind == HostWorldKind.World) Status.Notify(FollowFailureWords.Rejoined);
        }

        private void Busy(string reason)
        {
            if (busyTotal >= BusyGiveUpSeconds)
            {
                Fail(FollowFailureKind.PrepareFailed, ModErrorCode.Conflict, "This game stayed busy: " + reason);
                return;
            }

            busyWait = BusyRetrySeconds;
        }

        /// <summary>
        /// At rest: this game should be where the host is. Back at the main menu by itself, it leaves the session; out of
        /// the host's world or level any other way, it follows the host again.
        /// </summary>
        private void Verify()
        {
            var target = applied!;
            if (target.Kind == HostWorldKind.Menu || stranded) return;
            if (scene.IsMainMenu && scene.Version != arrivedScene)
            {
                log.Info("Multiplayer: this game returned to the main menu by itself, so it left the session.");
                Status.Notify(FollowFailureWords.MenuLeft);
                leavingForMenu = true;
                ending = true;
                leave();
                return;
            }

            var inWorld = target.Kind == HostWorldKind.World
                ? string.Equals(sessions.FollowedSessionId, target.SessionId, StringComparison.Ordinal) && sessions.Current.Phase == SessionPhase.Running
                : !scene.IsGameplay || string.Equals(scene.Name, target.Scene, StringComparison.OrdinalIgnoreCase);
            if (inWorld) return;
            if (++refollows > MaximumRefollows)
            {
                Fail(FollowFailureKind.KeepsLeaving, ModErrorCode.InvalidState, "This game left the host's world " + refollows + " times.");
                return;
            }

            log.Info("Multiplayer: this game left the host's world by itself, so it follows the host again.");
            applied = null;
            Status.Refollowing();
        }

        /// <summary>Ends the join with the failure's own words; the game returns to the main menu once it has left.</summary>
        private void Fail(FollowFailureKind kind, ModErrorCode code, string reason)
        {
            LogReason("Multiplayer: following the host's world failed: " + kind + " (" + code + ")", reason);
            Status.Fail(FollowFailureWords.For(kind, code));
            ending = true;
            leave();
        }

        private void ReturnToMenu(bool failed)
        {
            _ = sessions.FollowMainMenuAsync(CancellationToken.None).ContinueWith(task => dispatcher.Post(() =>
            {
                var result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                if (result == null || !result.Succeeded)
                    LogReason("Multiplayer: the main menu did not load after leaving the session", result?.ErrorMessage ?? "faulted");
                if (!failed && !IsJoined && Status.Stage == FollowStage.ReturningToMenu) Status.Ended();
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void Reached(SessionFollowStage stage, int step, int steps)
        {
            switch (stage)
            {
                case SessionFollowStage.Preparing:
                    Status.Move(FollowStage.Preparing, "Getting your mods ready");
                    break;
                case SessionFollowStage.LoadingWorld:
                    Status.Move(FollowStage.LoadingWorld, steps == 2 && step == 1 ? "Loading the host's level"
                        : steps == 2 ? "Building the host's arena" : "Loading the host's world", step, steps);
                    break;
                case SessionFollowStage.Synchronizing:
                    arrivalShown = -1;
                    Status.Move(FollowStage.Synchronizing, ConnectingStepMap.ReceivingWorld);
                    break;
                case SessionFollowStage.Participant:
                    Status.Move(FollowStage.Participant, "Starting your controls");
                    break;
            }

            ReportProgress();
        }

        /// <summary>The follow's Synchronizing stage: it ends once the network world has the host's world in place.</summary>
        private Task<OperationResult<bool>> WaitForWorldAsync(CancellationToken token)
        {
            if (WorldArrived()) return Task.FromResult(OperationResult<bool>.Success(true));
            if (token.IsCancellationRequested) return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The follow was cancelled."));
            EndArrival();
            var waiting = new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
            arrival = waiting;
            arrivalCancel = token.Register(() => waiting.TrySetResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The follow was cancelled.")));
            return waiting.Task;
        }

        private void WatchArrival()
        {
            var waiting = arrival;
            if (waiting == null) return;
            var progress = world();
            if (progress.Phase == NetworkWorldPhase.ReceivingBaseline && progress.EntriesExpected > 0)
            {
                var done = Math.Min(progress.EntriesReceived, progress.EntriesExpected);
                if (done != arrivalShown)
                {
                    arrivalShown = done;
                    Status.Move(FollowStage.Synchronizing, done + "/" + progress.EntriesExpected);
                }
            }

            if (!WorldArrived()) return;
            arrival = null;
            arrivalCancel.Dispose();
            waiting.TrySetResult(OperationResult<bool>.Success(true));
        }

        private bool WorldArrived()
        {
            var phase = world().Phase;
            return phase == NetworkWorldPhase.Synchronized || phase == NetworkWorldPhase.Offline;
        }

        private void EndArrival()
        {
            var waiting = arrival;
            arrival = null;
            arrivalCancel.Dispose();
            waiting?.TrySetResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The follow was cancelled."));
        }

        /// <summary>Says the follow is at work, once a second, while it loads; never while it only waits for the host's world.</summary>
        private void ReportWork(float elapsed)
        {
            if (arrival != null) return;
            progressWait -= elapsed;
            if (progressWait > 0f) return;
            ReportProgress();
        }

        private void ReportProgress()
        {
            progressWait = ProgressSeconds;
            try
            {
                hold?.ReportProgress();
            }
            catch (Exception exception)
            {
                log.Error("Multiplayer: the follow's progress could not be reported: " + exception.GetType().Name + ".");
            }
        }

        private void TakeHold()
        {
            if (hold != null) return;
            try
            {
                hold = control?.HoldReadiness(HoldReason);
            }
            catch (Exception exception)
            {
                log.Error("Multiplayer: the follow's readiness hold could not be taken: " + exception.GetType().Name + ".");
            }
        }

        private void ReleaseHold()
        {
            var current = hold;
            hold = null;
            try
            {
                current?.Dispose();
            }
            catch (Exception exception)
            {
                log.Error("Multiplayer: the follow's readiness hold could not be released: " + exception.GetType().Name + ".");
            }
        }

        private void ReleaseGrant()
        {
            var current = grant;
            grant = null;
            try
            {
                current?.Dispose();
            }
            catch (Exception exception)
            {
                log.Error("Multiplayer: the follow grant could not be released: " + exception.GetType().Name + ".");
            }
        }

        /// <summary>Writes a reason to the manager log: without addresses, without paths, and bounded.</summary>
        private void LogReason(string what, string? reason)
        {
            log.Warn(what + ": " + Scrub(reason) + ".");
        }

        /// <summary>A reason as the manager log may keep it: every address and every path goes, and it is bounded.</summary>
        internal static string Scrub(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "no reason given";
            var text = new StringBuilder(Math.Min(reason!.Length, MaximumLoggedReason));
            foreach (var token in reason.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(token.IndexOf('/') >= 0 || token.IndexOf('\\') >= 0 ? "[path]" : token);
                if (text.Length >= MaximumLoggedReason) break;
            }

            var redacted = MultiplayerDiagnosticRedactor.Redact(text.ToString());
            return redacted.Length <= MaximumLoggedReason ? redacted : redacted.Substring(0, MaximumLoggedReason);
        }

        /// <summary>A follow on its way, of one announcement.</summary>
        private sealed class Pending
        {
            internal Pending(HostWorldState target)
            {
                Target = target;
            }

            internal HostWorldState Target { get; }

            internal CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();

            internal Task<SessionFollowResult>? World { get; set; }

            internal Task<OperationResult<bool>>? Other { get; set; }

            internal Task Completion { get; set; } = Task.CompletedTask;

            /// <summary>Gets or sets whether a newer announcement took this follow's place.</summary>
            internal bool Superseded { get; set; }

            /// <summary>Gets or sets whether this is a look for the game's own worlds, after which the host's world is followed again.</summary>
            internal bool Discovery { get; set; }

            /// <summary>Gets or sets whether this returns the game to the main menu, as the host's level could not be loaded.</summary>
            internal bool ToMenu { get; set; }
        }
    }
}
