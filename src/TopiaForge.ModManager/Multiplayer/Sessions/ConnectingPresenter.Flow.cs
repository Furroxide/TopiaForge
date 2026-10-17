using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>The connecting presenter's flow: starting, following the status, and ending in success or the failure view.</summary>
    internal sealed partial class ConnectingPresenter
    {
        private const int MaximumFailureLines = 8;

        private void LeaveThenJoin(JoinRequest join)
        {
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

                FailLocal("Your game did not close", HostText.ForDisplay(message, HostText.LineChars));
            });
        }

        private void Start(JoinRequest join)
        {
            var control = context.Environment.Control;
            if (control == null)
            {
                Show(join, Stage.Joining);
                FailLocal("Multiplayer is not available", "The multiplayer provider is not loaded.");
                return;
            }

            OperationResult<bool> result;
            try
            {
                var settings = new InternalJoinSettings { Password = join.Password, AllowDirect = join.AllowDirect, TrustedServerId = join.TrustedServerId };
                result = control.Join(join.Target, settings);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
            {
                result = OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }

            Show(join, Stage.Joining);
            if (!result.Succeeded)
            {
                FailLocal("Join refused", HostText.ForDisplay(result.ErrorMessage, HostText.LineChars));
                return;
            }

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
            var path = ConnectingStepMap.PathOf(request!.Target.Kind, request.AllowDirect);
            details[step] = step == (int)ConnectingStep.Synchronizing && details[step].IndexOf('/') > 0
                ? details[step]
                : ConnectingStepMap.DetailOf(status.JoinPhase, (ConnectingStep)step, path);
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

            var message = LiveFailureMessages.ForJoin(reason, status.RejectionCode);
            var stop = (int)ConnectingStepMap.FailureStep(reason, status.RejectionCode, (ConnectingStep)reached);
            for (var step = reached + 1; step < stop; step++) skipped |= 1 << step;
            Fail(message, stop, reason, status);
            failureLines = Lines(status);
            hostSays = HostText.HostSays(status.HostMessage);
            changedServerId = InternalText.IsServerId(status.ChangedServerId) ? status.ChangedServerId : null;
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

        /// <summary>The failure's detail lines: the host's are redacted, this game's own are bounded.</summary>
        private static IReadOnlyList<string> Lines(InternalLiveStatus status)
        {
            var problems = status.ContentProblems;
            if (problems.Count == 0) return Array.Empty<string>();
            var lines = new List<string>(Math.Min(problems.Count, MaximumFailureLines));
            for (var index = 0; index < problems.Count && lines.Count < MaximumFailureLines; index++)
            {
                lines.Add(status.ContentProblemsFromHost
                    ? HostText.ForDisplay(problems[index], HostText.LineChars)
                    : TopiaForge.Mods.UnityUi.TopiaForgePlainText.Sanitize(problems[index], HostText.LineChars));
            }

            if (problems.Count > MaximumFailureLines) lines.Add("And " + (problems.Count - MaximumFailureLines) + " more.");
            return lines.AsReadOnly();
        }
    }
}
