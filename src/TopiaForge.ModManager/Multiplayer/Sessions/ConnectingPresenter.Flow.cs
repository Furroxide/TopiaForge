using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>The connecting presenter's flow: the realm verdict, starting, following the status, and ending in success or the failure view.</summary>
    internal sealed partial class ConnectingPresenter
    {
        private const int MaximumFailureLines = 8;

        /// <summary>
        /// Acts on the runtime's realm verdict for <paramref name="join"/>: joins, asks "Leave current game?", waits on the
        /// overlay while the game is starting or stopping, or says why not. <paramref name="fromWait"/> is set when a wait on
        /// the overlay asks again; a verdict other than busy or a join then takes the overlay down first.
        /// </summary>
        private void Decide(JoinRequest join, bool fromWait)
        {
            var environment = context.Environment;
            var realm = environment.Realm;
            switch (SessionRealmGate.ForJoin(environment.EvaluateRealmChange(NetRealm.Client), realm))
            {
                case RealmDecision.Join:
                    Start(join);
                    return;
                case RealmDecision.Busy:
                    if (!fromWait) Wait(join);
                    return;
                case RealmDecision.ConfirmLeaveGame:
                    if (fromWait) Hide();
                    // The question is about the game running now: once it ends by itself, or another join starts, it is withdrawn.
                    ScreenPrompt? asked = null;
                    asked = PrivacyPrompts.Destructive(LeaveTitle, LeaveBody, LeaveConfirm, confirmed =>
                    {
                        if (confirmed) LeaveThenJoin(join);
                        else if (join.Launch != null) Declined(join, asked!.IsWithdrawn);
                    }, leaveStillAsked);
                    context.Ask(asked);
                    return;
                case RealmDecision.StandaloneRefused:
                    if (fromWait) Hide();
                    var standalone = MultiplayerRuntime.StandaloneRefusal(environment.StandaloneMods, "join");
                    context.Notify(standalone, ScreenTone.Warning);
                    join.Launch?.Refuse(ModErrorCode.InvalidState, standalone);
                    return;
                default:
                    if (fromWait) Hide();
                    var leaveFirst = realm == NetRealm.ListenHost ? "Stop hosting before you join another session." : "Leave this session before you join another.";
                    context.Notify(leaveFirst, ScreenTone.Warning);
                    join.Launch?.Refuse(ModErrorCode.InvalidState, leaveFirst);
                    return;
            }
        }

        /// <summary>
        /// A launch's "Leave current game?" answered no. Withdrawn because the game ended by itself, the join is decided again;
        /// turned down by the player, the launch is cancelled and the game runs on.
        /// </summary>
        private void Declined(JoinRequest join, bool withdrawn)
        {
            if (withdrawn) Begin(join);
            else join.Launch!.Refuse(ModErrorCode.Cancelled, KeptGame);
        }

        /// <summary>
        /// Asks the realm rules again while the overlay waits: the join starts once they allow it, and never before. After
        /// "Leave current game?" the game is still leaving while the rules say it runs or is busy: the session ending, then
        /// the main menu loading, which the runtime counts as busy until it has loaded.
        /// </summary>
        private void Settle()
        {
            var join = request!;
            if (stage == Stage.Leaving)
            {
                var decision = SessionRealmGate.ForJoin(context.Environment.EvaluateRealmChange(NetRealm.Client), context.Environment.Realm);
                if (decision == RealmDecision.Busy || decision == RealmDecision.ConfirmLeaveGame) return;
            }

            Decide(join, fromWait: true);
        }

        /// <summary>Shows the overlay waiting for the current game to settle; Cancel works meanwhile.</summary>
        private void Wait(JoinRequest join)
        {
            Show(join, Stage.Waiting);
            details[(int)ConnectingStep.Resolving] = ConnectingStepMap.WaitingForGame;
            statusText = WaitingText;
        }

        private void LeaveThenJoin(JoinRequest join)
        {
            if (IsBusy)
            {
                context.Notify(AlreadyJoining, ScreenTone.Warning);
                join.Launch?.Refuse(ModErrorCode.Conflict, AlreadyJoining);
                return;
            }

            Show(join, Stage.Leaving);
            details[(int)ConnectingStep.Resolving] = ConnectingStepMap.LeavingGame;
            statusText = ConnectingStepMap.LeavingGame + "...";
            menuReturned = false;
            context.Environment.ReturnToMainMenu((returned, message) =>
            {
                if (stage != Stage.Leaving || !ReferenceEquals(request, join)) return;
                if (returned)
                {
                    menuReturned = true;
                    return;
                }

                var said = HostText.ForDisplay(message, HostText.LineChars);
                FailLocal("Your game did not close", said);
                join.Launch?.Refuse(ModErrorCode.External, said);
            });
        }

        private void Start(JoinRequest join)
        {
            OperationResult<bool> result;
            try
            {
                // A launch joins under its own name; any other join under the runtime's.
                var settings = new InternalJoinSettings
                {
                    DisplayName = join.Launch?.Plan.DisplayName,
                    Password = join.Password,
                    AllowDirect = join.AllowDirect,
                    TrustedServerId = join.TrustedServerId
                };

                // The runtime asks the realm rules again before the provider hears of the join; a game that began to change
                // meanwhile is waited for, never joined.
                var change = context.Environment.Join(join.Target, settings);
                if (change.Verdict == NetRealmTransitionVerdict.Busy)
                {
                    Wait(join);
                    return;
                }

                result = change.Result;
            }
            catch (Exception exception)
            {
                // A join that waited starts from the frame, so whatever the provider throws ends it here, once, on the
                // failure view, rather than escaping that frame and being tried again on the next.
                result = LiveFailureMessages.Thrown(exception);
            }

            Show(join, Stage.Joining);
            if (!result.Succeeded)
            {
                FailLocal("Join refused", HostText.ForDisplay(result.ErrorMessage, HostText.LineChars));
                join.Launch?.Refuse(result.ErrorCode, result.ErrorMessage);
                return;
            }

            join.Launch?.Started();
            readsAtStart = context.Status.Reads;
            details[(int)ConnectingStep.Resolving] = ConnectingStepMap.FindingHost;
            statusText = "Joining " + title + "...";
            context.Status.RequestRefresh();
        }

        private void Show(JoinRequest join, Stage next)
        {
            request = join;
            title = join.Title;
            stage = next;
            cancelRequested = false;
            retryWait = 0d;
            retryWhenWaited = false;
            shownWait = -1;
            failure = null;
            failureStatus = null;
            changedServerId = null;
            failureLines = Array.Empty<string>();
            hostSays = string.Empty;
            passwordError = string.Empty;
            linger = 0f;
            reached = 0;
            held = false;
            active = (int)ConnectingStep.Resolving;
            failed = -1;
            complete = false;
            skipped = 0;
            for (var index = 0; index < details.Length; index++) details[index] = string.Empty;
            Bump();
        }

        private void Follow()
        {
            var tracker = context.Status;
            if (tracker.Reads <= readsAtStart) return;
            var status = tracker.Status;
            switch (status.JoinPhase)
            {
                case InternalJoinPhase.None:
                    return;
                case InternalJoinPhase.Ended:
                    Ended(status);
                    return;
                case InternalJoinPhase.InSession:
                    Succeed(status);
                    return;
            }

            var step = (int)ConnectingStepMap.StepOf(status.JoinPhase, tracker.Snapshot?.State, status.IsReadinessHeld);
            if (step == (int)ConnectingStep.LoadingWorld) held = true;
            if (step == (int)ConnectingStep.Synchronizing && !held) skipped |= 1 << (int)ConnectingStep.LoadingWorld;

            // The stepper only walks forward, except back to Connecting while a lost link reconnects.
            if (step < reached && status.JoinPhase != InternalJoinPhase.Reconnecting) step = reached;
            reached = Math.Max(reached, step);
            var route = status.Internet.JoinRoute;
            var path = ConnectingStepMap.PathOf(request!.Target.Kind, request.AllowDirect, route);
            details[step] = step == (int)ConnectingStep.Synchronizing && details[step].IndexOf('/') > 0
                ? details[step]
                : ConnectingStepMap.DetailOf(status.JoinPhase, (ConnectingStep)step, path);

            // Once the link is up the status names its route, and the Connecting step says how it connected.
            if (route != InternalConnectionRoute.None) details[(int)ConnectingStep.Connecting] = path;
            if (active != step)
            {
                active = step;
                Bump();
            }
        }

        private void Ended(InternalLiveStatus status)
        {
            var reason = status.JoinFailure;
            if (reason == InternalLiveFailure.Left || (reason == InternalLiveFailure.Cancelled && cancelRequested))
            {
                Hide();
                return;
            }

            var message = LiveFailureMessages.ForJoin(reason, status.RejectionCode, status.Internet.HostAllowsDirect);
            var stop = (int)ConnectingStepMap.FailureStep(reason, status.RejectionCode, (ConnectingStep)reached);
            for (var step = reached + 1; step < stop; step++) skipped |= 1 << step;
            Fail(message, stop, reason, status);
            failureLines = Lines(status);
            hostSays = HostText.HostSays(status.HostMessage);
            changedServerId = InternalText.IsServerId(status.ChangedServerId) ? status.ChangedServerId : null;
            switch (reason)
            {
                case InternalLiveFailure.ServiceRateLimited when status.Internet.RetryAfterSeconds > 0:
                    // The service named how long to wait: the overlay counts it down, then joins again by itself.
                    failure = LiveFailureMessages.WaitingOutTheService;
                    retryWait = Math.Min(status.Internet.RetryAfterSeconds, MaximumRetryWaitSeconds);
                    retryWhenWaited = true;
                    shownWait = -1;
                    CountDown(0f);
                    break;
                case InternalLiveFailure.ServerNotListed:
                    ForgetUnlisted(request!.Target.ServerId);
                    break;
                // A launch keeps the privacy the launcher planned: the launcher, told why the join failed, asks the player there.
                case InternalLiveFailure.RelayUnavailable when status.Internet.HostAllowsDirect && !request!.AllowDirect && request.Launch == null:
                    OfferDirect(request!);
                    break;
            }
        }

        /// <summary>
        /// The relay is unavailable, and the host takes direct connections: the approved per-session prompt may let this one
        /// join go directly. Allowing starts the join again with a direct connection allowed for this session only;
        /// cancelling keeps Hide my IP on and the failure view up, and contacts nothing. It asks only while this failure
        /// shows.
        /// </summary>
        private void OfferDirect(JoinRequest failedJoin)
        {
            context.Privacy.EnsureDirect(
                () =>
                {
                    Hide();
                    Begin(failedJoin.Direct());
                },
                () => context.Notify(ConnectPresenter.NothingContacted, ScreenTone.Neutral),
                () => stage == Stage.Failed && ReferenceEquals(request, failedJoin));
        }

        /// <summary>
        /// Counts down the service's named delay: until it ends RETRY waits and the status line says how long, and once it
        /// ends the join starts again by itself, so the player never has to come back to press it.
        /// </summary>
        private void CountDown(float deltaTime)
        {
            retryWait = Math.Max(0d, retryWait - (deltaTime > 0f && !float.IsNaN(deltaTime) ? deltaTime : 0d));
            var seconds = (int)Math.Ceiling(retryWait);
            if (seconds == shownWait) return;
            shownWait = seconds;
            statusText = seconds > 0
                ? failure!.Title + ": trying again in " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + (seconds == 1 ? " second." : " seconds.")
                : failure!.Title;
            if (seconds == 0 && retryWhenWaited) Retry();
        }

        /// <summary>A listed server the service no longer lists leaves the recent servers.</summary>
        private void ForgetUnlisted(string? serverId)
        {
            if (!InternalText.IsServerId(serverId) || !context.Settings.IsLoaded) return;
            context.Settings.Update(context.Settings.Current.ForgetJoin(serverId!));
        }

        private void Succeed(InternalLiveStatus status)
        {
            if (!held) skipped |= 1 << (int)ConnectingStep.LoadingWorld;
            complete = true;
            active = -1;
            stage = Stage.Succeeded;
            linger = 0f;
            statusText = "Joined " + title + ".";
            Bump();
            context.Notify(statusText, ScreenTone.Success);
            RecordHistory(status.Server);
        }

        private void FailLocal(string heading, string message)
        {
            Fail(new FailureMessage(heading, message, ConnectingStep.Resolving, retryable: true), Math.Max(reached, 0), InternalLiveFailure.None,
                new InternalLiveStatus { JoinDetail = message });
        }

        private void Fail(FailureMessage message, int step, InternalLiveFailure reason, InternalLiveStatus status)
        {
            failure = message;
            failureCode = reason;
            failureStatus = status;
            failed = step;
            active = -1;
            complete = false;
            stage = Stage.Failed;
            statusText = message.Title;
            Bump();
        }

        private void Hide()
        {
            if (stage == Stage.Hidden) return;
            stage = Stage.Hidden;
            passwordError = string.Empty;
            Bump();
        }

        private void Bump()
        {
            version++;
            steps = new ConnectingSteps(version, active, failed, complete, skipped);
        }

        private void RecordHistory(InternalServerInfo? server)
        {
            if (server == null || !InternalText.IsServerId(server.ServerId) || !context.Settings.IsLoaded) return;
            context.Settings.Update(context.Settings.Current.RecordJoin(new ServerBookmark(server.ServerId, server.Name, context.Environment.UnixSeconds)));
        }

        /// <summary>
        /// The failure's detail lines: the host's are redacted; this game's own, which name the host's packages by the ids
        /// and versions it gave, lose any address in those, and are bounded.
        /// </summary>
        private static IReadOnlyList<string> Lines(InternalLiveStatus status)
        {
            var problems = status.ContentProblems;
            if (problems.Count == 0) return Array.Empty<string>();
            var lines = new List<string>(Math.Min(problems.Count, MaximumFailureLines));
            for (var index = 0; index < problems.Count && lines.Count < MaximumFailureLines; index++)
            {
                lines.Add(status.ContentProblemsFromHost
                    ? HostText.ForDisplay(problems[index], HostText.LineChars)
                    : HostText.ForOwnLine(problems[index]));
            }

            if (problems.Count > MaximumFailureLines) lines.Add("And " + (problems.Count - MaximumFailureLines) + " more.");
            return lines.AsReadOnly();
        }
    }
}
