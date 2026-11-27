using System;
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
    /// host's world by itself, it follows it again, eight different-level escapes in a join at most; a native restart of
    /// the same level still rebuilds the host's world without spending that allowance. At the main menu, it leaves the session.
    /// A failure ends the join with its own words (<see cref="FollowFailureWords"/>) and returns the game to the main menu;
    /// the reason the runtime, a provider or a mod gave goes to the manager log alone, without addresses or paths. A join the
    /// launcher planned has its follows report to the launcher under that join's request; any other join's report nothing.
    /// Unity-free; main thread only. A frame at rest compares a few fields and allocates nothing. How a follow ends, and what
    /// it reports meanwhile, is in SessionFollower.Finish.cs.
    /// </summary>
    internal sealed partial class SessionFollower : ISessionFollowOwner
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
        private readonly SessionMapReceiver maps;
        private readonly WorldWireReader reader = new WorldWireReader();
        private IInternalMultiplayerControl? control;
        private IInternalReadinessHold? hold;
        private IDisposable? grant;
        private string? launchRequestId;
        private HostWorldState? latest;
        private HostWorldState? applied;

        // Unlike applied, this also identifies an attempt while it waits for a busy game or discovers its world.
        private HostWorldState? following;
        private Pending? pending;
        private TaskCompletionSource<OperationResult<bool>>? arrival;
        private CancellationTokenRegistration arrivalCancel;
        private float progressWait;
        private float busyWait;
        private float busyTotal;
        private int refollows;
        private ulong streamGeneration;
        private bool discovered;
        private bool loaded;
        // Completed-join history survives retirement of an announcement when the host replaces its stream.
        private bool hasCompletedFollow;
        private bool stranded;
        private bool leavingForMenu;
        private bool ending;
        private int arrivalShown = -1;
        private int arrivedScene;
        private string? arrivedSceneName;

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
            maps = new SessionMapReceiver(channel, dispatcher, bytes =>
            {
                Status.Move(FollowStage.LoadingWorld, "Downloading the host's map (" + (bytes / 1024) + " KiB)");
                ReportProgress();
            });
        }

        /// <summary>Gets where the follow stands, for the connecting overlay.</summary>
        internal SessionFollowStatus Status { get; } = new SessionFollowStatus();

        /// <summary>Gets whether this game is joined and follows its host.</summary>
        internal bool IsJoined { get; private set; }

        public bool IsFollowing => IsJoined;

        /// <summary>Gets whether this follower's readiness hold is open, for tests and diagnostics.</summary>
        internal bool HoldsReadiness => hold != null;

        /// <summary>Gets the valid announcement this game followed; null before arrival or after its stream is replaced.</summary>
        internal HostWorldState? Applied => applied;

        /// <summary>Gets this join's different-level corrections; native restarts of the same level do not spend the allowance.</summary>
        internal int Refollows => refollows;

        /// <summary>Gets the announcements that could not be read, for tests and diagnostics.</summary>
        internal long Unreadable { get; private set; }

        /// <summary>
        /// Gets the request this join's follows report to the launcher under: the launcher-planned join's own, taken at link-up,
        /// or null for a join started in game, whose follows report nothing to the launcher.
        /// </summary>
        internal string? LaunchRequestId => launchRequestId;

        /// <summary>
        /// This game linked up to a host. A game the player started meanwhile is never replaced (#224): the join ends with
        /// its own words, and joining again asks "Leave current game?" first. Otherwise the follow holds readiness and waits
        /// for the host's announcement. <paramref name="launchedJoin"/> is the request of the launcher-planned join this is,
        /// which every follow of the join reports under; null for a join started in game.
        /// </summary>
        internal void Joined(IInternalMultiplayerControl joined, string? launchedJoin = null)
        {
            if (joined == null) throw new ArgumentNullException(nameof(joined));
            if (IsJoined) return;
            Status.Joined(launchedJoin);
            if (sessions.Current.Phase != SessionPhase.Idle || scene.IsGameplay)
            {
                log.Warn("Multiplayer: a game started here while the join resolved, so the join stopped and that game kept running.");
                Status.Fail(FollowFailureWords.For(FollowFailureKind.StartedGameWhileJoining, ModErrorCode.InvalidState));
                leave();
                return;
            }

            streamGeneration = channel()?.Generation ?? 0;
            IsJoined = true;
            control = joined;
            launchRequestId = launchedJoin;
            ending = false;
            latest = null;
            applied = null;
            hasCompletedFollow = false;
            following = null;
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
            maps.Clear();
            IsJoined = false;
            var running = pending;
            pending = null;
            running?.Cancellation.Cancel();
            EndArrival();
            ReleaseHold();
            ReleaseGrant();
            control = null;
            launchRequestId = null;
            ending = false;
            latest = null;
            applied = null;
            hasCompletedFollow = false;
            following = null;
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
            var generation = channel()?.Generation ?? 0;
            if (generation != streamGeneration)
            {
                streamGeneration = generation;
                TakeHold();
                Status.Move(FollowStage.AwaitingHost, "Waiting for the host's new world");
                latest = null;
                applied = null;
                following = null;
                if (pending != null) { pending.Superseded = true; pending.Cancellation.Cancel(); }
                maps.Clear();
            }
            maps.Pump(elapsed);
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
                if (maps.Receive(message.Payload)) continue;
                if (SessionFollowWire.TryRead(reader, message.Payload, out var state)) latest = state;
                else Unreadable++;
            }
        }

        /// <summary>Starts following <paramref name="target"/>: the host's world, its level, or the main menu.</summary>
        private void Begin(HostWorldState target)
        {
            var refollow = hasCompletedFollow || Status.IsRefollow;
            if (refollow && !target.SameAs(following)) Status.Refollowing();
            following = target;
            switch (target.Kind)
            {
                case HostWorldKind.World:
                    TakeHold();
                    loaded = true;
                    stranded = false;
                    var plan = new SessionFollowPlan(target.SessionId!, target.Request!, target.GamemodeId!, target.WorldId!,
                        target.WorldFamilyId, target.Transition!, target.Scene, target.Spawn, WaitForWorldAsync, launchRequestId, target.Map,
                        (token, progress) => maps.ReadAsync(target, token, progress));
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
                        hasCompletedFollow = true;
                        stranded = false;
                        ReleaseHold();
                        Status.Move(FollowStage.AtMenu, "The host is at the main menu");
                        return;
                    }

                    loaded = true;
                    var menu = new Pending(target);
                    pending = menu;
                    Status.Move(FollowStage.ReturningToMenu, "The host went to the main menu");
                    menu.Other = sessions.FollowMainMenuAsync(menu.Cancellation.Token);
                    menu.Completion = menu.Other;
                    break;
            }

            ReportProgress();
        }

        /// <summary>
        /// At rest: this game should be where the host is. Back at the main menu by itself, it leaves the session; out of
        /// the host's world or level any other way, it follows the host again.
        /// </summary>
        private void Verify()
        {
            var target = applied!;
            var waitingAtMenu = target.Kind == HostWorldKind.Menu || stranded;
            if (waitingAtMenu && !scene.IsGameplay) return;
            if (scene.IsMainMenu && scene.Version != arrivedScene)
            {
                log.Info("Multiplayer: this game returned to the main menu by itself, so it left the session.");
                Status.Notify(FollowFailureWords.MenuLeft);
                leavingForMenu = true;
                ending = true;
                leave();
                return;
            }

            var inWorld = !waitingAtMenu && (target.Kind == HostWorldKind.World
                ? string.Equals(sessions.FollowedSessionId, target.SessionId, StringComparison.Ordinal) && sessions.Current.Phase == SessionPhase.Running
                : !scene.IsGameplay || (string.Equals(scene.Name, target.Scene, StringComparison.OrdinalIgnoreCase) && scene.Version == arrivedScene));
            if (inWorld) return;
            var sameLevelRestart = !waitingAtMenu && scene.IsGameplay && scene.Version != arrivedScene
                && string.Equals(scene.Name, arrivedSceneName, StringComparison.OrdinalIgnoreCase);
            if (!sameLevelRestart && ++refollows > MaximumRefollows)
            {
                Fail(FollowFailureKind.KeepsLeaving, ModErrorCode.InvalidState, "This game left the host's world " + refollows + " times.");
                return;
            }

            log.Info("Multiplayer: this game left the host's world by itself, so it follows the host again.");
            Status.Refollowing();
            following = target;
            if (stranded)
            {
                // Correct a local load back to the waiting menu without retrying
                // the unavailable level. A new host announcement still replaces it.
                var waiting = new Pending(target) { ToMenu = true };
                pending = waiting;
                Status.Move(FollowStage.ReturningToMenu, FollowFailureWords.LevelNotLoaded);
                waiting.Other = sessions.FollowMainMenuAsync(waiting.Cancellation.Token);
                waiting.Completion = waiting.Other;
            }
            else applied = null;
        }

        /// <summary>Ends the join with the failure's own words; the game returns to the main menu once it has left.</summary>
        private void Fail(FollowFailureKind kind, ModErrorCode code, string reason)
        {
            LogReason("Multiplayer: following the host's world failed: " + kind + " (" + code + ")", reason);
            Status.Fail(kind == FollowFailureKind.WorldLoadFailed && following?.Map != null
                ? FollowFailureWords.Map(code) : FollowFailureWords.For(kind, code));
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
    }
}
