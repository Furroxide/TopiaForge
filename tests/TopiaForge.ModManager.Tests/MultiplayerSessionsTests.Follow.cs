using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The connecting overlay over session follow (WS3.8): the follow's stages fill the join's last steps with this game's
    /// own lines, a join the follow ends shows the follow's words, a follow after the join shows "Following the host" and
    /// can be cancelled, and the follow's notices show once each.
    /// </summary>
    internal static partial class MultiplayerSessionsTests
    {
        private static void ConnectingShowsTheFollow()
        {
            var rig = JoiningRig();
            var follow = rig.Environment.Follow;
            follow.Joined();
            follow.Move(FollowStage.LoadingWorld, "Loading the host's level", 1, 2);
            rig.Frame();
            var overlay = rig.Presenter.Connecting;
            That(overlay.Steps.Active == (int)ConnectingStep.LoadingWorld && overlay.DetailOf((int)ConnectingStep.LoadingWorld) == "Loading the host's level (1/2)",
                "while the follow loads the host's level, Loading world says so, step 1 of 2 (" + overlay.DetailOf(overlay.Steps.Active) + ")");
            follow.Move(FollowStage.LoadingWorld, "Building the host's arena", 2, 2);
            rig.Frame();
            That(overlay.DetailOf((int)ConnectingStep.LoadingWorld) == "Building the host's arena (2/2)", "then the arena over it, step 2 of 2");
            follow.Move(FollowStage.Synchronizing, "2/5");
            rig.Frame();
            That(overlay.Steps.Active == (int)ConnectingStep.Synchronizing && overlay.DetailOf((int)ConnectingStep.Synchronizing) == "2/5",
                "then Synchronizing, n of m");
            follow.Move(FollowStage.Participant, "Starting your controls");
            rig.Frame();
            That(overlay.Steps.Active == (int)ConnectingStep.Spawning && overlay.DetailOf((int)ConnectingStep.Spawning) == "Starting your controls",
                "then Spawning, as this player's controls start");
            follow.Move(FollowStage.Running, "In the host's world");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-ready", "p1");
            rig.Frame();
            That(overlay.Steps.Complete && rig.Noticed("Joined LAN game."), "once the host's world runs and the player is Ready, the join completes");
            That(!rig.Noticed(ConnectingPresenter.HostAtMenu), "a host in a world is not said to be at the menu");

            var atMenu = JoiningRig();
            atMenu.Environment.Follow.Joined();
            atMenu.Environment.Follow.Move(FollowStage.AtMenu, "The host is at the main menu");
            atMenu.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            atMenu.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-menu", "p1");
            atMenu.Frame();
            That(atMenu.Noticed(ConnectingPresenter.HostAtMenu), "a join to a host at the main menu says the player joins its next world");
        }

        private static void AFollowFailureShowsItsOwnWords()
        {
            foreach (var (kind, code, retryable) in new[]
            {
                (FollowFailureKind.WorldLoadFailed, ModErrorCode.NotFound, false),
                (FollowFailureKind.SynchronizeFailed, ModErrorCode.TimedOut, true),
                (FollowFailureKind.StartedGameWhileJoining, ModErrorCode.InvalidState, true)
            })
            {
                var rig = JoiningRig();
                var follow = rig.Environment.Follow;
                follow.Joined();
                follow.Move(FollowStage.LoadingWorld, "Loading the host's world");
                rig.Frame();
                var words = FollowFailureWords.For(kind, code);
                follow.Fail(words);
                rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
                rig.Frame();
                var overlay = rig.Presenter.Connecting;
                That(overlay.HasFailed && overlay.FailureTitle == words.Title && overlay.FailureMessage == words.Message,
                    kind + ": a join the follow ended shows the follow's own words, not a left join (" + overlay.FailureTitle + ")");
                That(overlay.CanRetry == retryable, kind + ": RETRY shows only where joining again can help");
                That(overlay.Steps.Failed == Math.Max((int)words.Step, (int)ConnectingStep.LoadingWorld), kind + ": the failure stops at its own step");
                That(!overlay.FailureMessage.Contains("Caller:") && !overlay.FailureTitle.Contains("Caller:"), kind + ": no internal text reaches the overlay");
            }

            // A join the player left by itself, with no follow failure, still closes quietly.
            var left = JoiningRig();
            left.Environment.Follow.Joined();
            left.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
            left.Frame();
            That(!left.Presenter.Connecting.IsVisible, "a join left without a follow failure closes the overlay as before");

            // A failure of an earlier join is not this one's.
            var earlier = JoiningRig(follow =>
            {
                follow.Joined();
                follow.Fail(FollowFailureWords.For(FollowFailureKind.Differs, ModErrorCode.InvalidState));
            });
            earlier.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
            earlier.Frame();
            That(!earlier.Presenter.Connecting.IsVisible, "a follow failure from an earlier join never ends this one");
        }

        private static void AFollowAfterTheJoinShowsFollowing(bool dismiss)
        {
            var rig = JoiningRig();
            var follow = rig.Environment.Follow;
            follow.Joined();
            follow.Move(FollowStage.Running, "In the host's world");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-ready", "p1");
            rig.Frame(40);
            var overlay = rig.Presenter.Connecting;
            That(!overlay.IsVisible, "the join is done and the overlay closed");

            follow.Refollowing();
            follow.Move(FollowStage.Preparing, "Getting your mods ready");
            rig.Frame();
            That(overlay.IsVisible && overlay.Title == ConnectingPresenter.FollowingTitle && overlay.Steps.Active == (int)ConnectingStep.LoadingWorld
                && overlay.DetailOf((int)ConnectingStep.LoadingWorld) == "Getting your mods ready" && overlay.CanCancel,
                "when the host changes worlds, the overlay shows \"Following the host\" with the first four steps done");
            follow.Move(FollowStage.Synchronizing, "Receiving the world");
            rig.Frame();
            That(overlay.Steps.Active == (int)ConnectingStep.Synchronizing, "and walks on with the follow");
            follow.Move(FollowStage.Running, "In the host's world");
            follow.Notify(FollowFailureWords.Rejoined);
            rig.Frame();
            That(overlay.Steps.Complete && overlay.StatusText == FollowFailureWords.Rejoined && rig.Noticed(FollowFailureWords.Rejoined),
                "then says it is back in the host's world");
            rig.Frame(40);
            That(!overlay.IsVisible, "and closes");

            follow.Move(FollowStage.LoadingWorld, "Loading the host's world");
            rig.Frame();
            That(overlay.IsVisible && overlay.Title == ConnectingPresenter.FollowingTitle, "the next follow shows again");
            var leaves = rig.Control.Calls.Count(call => call == "Leave");
            if (dismiss) overlay.Dismiss();
            else overlay.Cancel();
            rig.Frame();
            That(rig.Control.Calls.Count(call => call == "Leave") == leaves + 1 && !overlay.IsVisible && rig.Noticed(ConnectingPresenter.LeftWhileFollowing),
                "Escape or close on it cancels the follow and leaves the session, which is never refused");
            follow.Fail(FollowFailureWords.For(FollowFailureKind.SynchronizeFailed, ModErrorCode.TimedOut));
            rig.Frame();
            That(!overlay.IsVisible, "a late failure of the cancelled follow stays closed");

            var notices = rig.Notices.Count(notice => notice.Text == FollowFailureWords.MenuLeft);
            follow.Notify(FollowFailureWords.MenuLeft);
            rig.Frame(5);
            That(rig.Notices.Count(notice => notice.Text == FollowFailureWords.MenuLeft) == notices + 1, "each of the follow's notices shows once");
            That(SessionPresenter.LeaveBody == "You leave the session and return to the main menu.", "\"Leave server?\" says the game returns to the main menu");
        }

        private static void ARefollowFailureShowsItsOwnWords()
        {
            foreach (var betweenFrames in new[] { false, true })
                foreach (var loading in new[] { FollowStage.LoadingWorld, FollowStage.LoadingLevel })
                {
                    var rig = JoiningRig();
                    var follow = rig.Environment.Follow;
                    follow.Joined();
                    follow.Move(FollowStage.Running, "In the host's world");
                    rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
                    rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-ready", "p1");
                    rig.Frame(40);
                    follow.Refollowing();
                    follow.Move(loading, "Loading the host's world");
                    if (!betweenFrames)
                    {
                        rig.Frame();
                        That(rig.Presenter.Connecting.IsVisible, "a world or native level re-follow shows its progress");
                    }
                    var words = FollowFailureWords.For(FollowFailureKind.SynchronizeFailed, ModErrorCode.TimedOut);
                    follow.Fail(words);
                    rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
                    rig.Frame();
                    var overlay = rig.Presenter.Connecting;
                    That(overlay.IsVisible && overlay.HasFailed && overlay.FailureTitle == words.Title && overlay.CanRetry,
                        "a failed re-follow explains what happened and offers RETRY, including between frames");
                    overlay.Dismiss();
                    rig.Frame(3);
                    That(!overlay.IsVisible, "a dismissed re-follow failure stays closed");
                }
        }

        /// <summary>A rig whose join to the LAN session '1' has linked up and synchronizes, held, as a follow begins.</summary>
        private static SessionsRig JoiningRig(Action<SessionFollowStatus>? before = null)
        {
            var rig = BrowsingRig(ready: true);
            before?.Invoke(rig.Environment.Follow);
            rig.Presenter.Browse.Select(new string('1', 64));
            rig.Presenter.Browse.Join();
            rig.Frame();
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.Synchronizing);
            rig.Control.Status.IsReadinessHeld = true;
            rig.Control.Status.Server = Server('1', "LAN game");
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Synchronizing, "s-joining", "p1");
            rig.Frame();
            That(rig.Presenter.Connecting.IsVisible && rig.Presenter.Connecting.Steps.Active == (int)ConnectingStep.LoadingWorld,
                "the join synchronizes, held, on the Loading world step");
            return rig;
        }
    }
}
