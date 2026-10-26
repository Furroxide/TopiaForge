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
    /// How a follow ends (WS3.8): in the host's world, waiting to try again, after a look for this game's own worlds, at the
    /// menu when the host's level cannot load here, or failed. And what a follow reports while it works: each stage to the
    /// connecting overlay, progress on its readiness hold, and reasons to the manager log, scrubbed.
    /// </summary>
    internal sealed partial class SessionFollower
    {
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
                else if (stranded) Arrived(finished.Target, FollowStage.LevelUnavailable, FollowFailureWords.LevelNotLoaded);
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
            arrivedSceneName = scene.Name;
            busyTotal = 0f;
            ReleaseHold();
            Status.Move(stage, detail);
            if (!Status.IsRefollow) return;

            // A follow after the join says where it took the player: back in the host's world, or to the main menu the host
            // went to, which this game loaded by itself.
            if (target.Kind == HostWorldKind.World) Status.Notify(FollowFailureWords.Rejoined);
            else if (stage == FollowStage.AtMenu) Status.Notify(FollowFailureWords.HostAtMenu);
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
