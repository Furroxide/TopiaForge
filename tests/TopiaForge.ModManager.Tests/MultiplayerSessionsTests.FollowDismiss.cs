using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MultiplayerSessionsTests
    {
        private static void DismissingARefollowHidesOnlyThatAttempt()
        {
            // The follower, not the card's linger or the presenter's last frame, classifies a re-follow.
            foreach (var framesBefore in new[] { 0, 1, 40 })
                foreach (var loading in new[] { FollowStage.LoadingWorld, FollowStage.LoadingLevel })
                {
                    var rig = RefollowDismissRig(framesBefore);
                    var follow = rig.Environment.Follow;
                    var overlay = rig.Presenter.Connecting;
                    follow.Refollowing();
                    follow.Move(loading, "Loading the host's world");
                    if (framesBefore != 0) rig.Frame();
                    var leaves = rig.Control.Calls.Count(call => call == "Leave");
                    overlay.Dismiss(); // Escape and the title-bar close share this handler.
                    overlay.Dismiss();
                    rig.Frame(40);
                    That(!overlay.IsVisible && rig.Control.Calls.Count(call => call == "Leave") == leaves,
                        "dismissing a re-follow hides it past the old reopen delay without leaving, even before the join card closes");
                    foreach (var progress in new[] { FollowStage.Preparing, loading, FollowStage.Synchronizing, FollowStage.Participant })
                    {
                        follow.Move(progress, "Follow progress");
                        rig.Frame(20);
                        That(!overlay.IsVisible, "progress and retry stages cannot reopen the dismissed attempt");
                    }
                    That(!rig.Noticed(ConnectingPresenter.LeftWhileFollowing) && !rig.Noticed("Join cancelled."),
                        "hiding a re-follow never reports cancellation");

                    // No presenter frame sees the arrival: the next attempt still has to reopen the card.
                    follow.Move(FollowStage.Running, "In the host's world");
                    follow.Refollowing();
                    follow.Move(loading, "Loading the next world");
                    rig.Frame();
                    That(overlay.IsVisible && overlay.CanCancel && overlay.Title == ConnectingPresenter.FollowingTitle,
                        "the next re-follow shows even when arrival and restart fell between presenter frames");
                    overlay.Cancel();
                    overlay.Dismiss();
                    follow.Fail(FollowFailureWords.For(FollowFailureKind.SynchronizeFailed, ModErrorCode.TimedOut));
                    rig.Frame(40);
                    That(!overlay.IsVisible && rig.Control.Calls.Count(call => call == "Leave") == leaves + 1,
                        "CANCEL on the next re-follow leaves once and suppresses its late failure");
                }
        }

        private static void AHiddenRefollowStillShowsItsFailure()
        {
            foreach (var retryable in new[] { false, true })
            {
                var rig = RefollowDismissRig(40);
                var follow = rig.Environment.Follow;
                var overlay = rig.Presenter.Connecting;
                follow.Refollowing();
                follow.Move(FollowStage.LoadingWorld, "Loading the host's world");
                rig.Frame();
                overlay.Dismiss();
                rig.Frame(40);
                var words = FollowFailureWords.For(FollowFailureKind.WorldLoadFailed,
                    retryable ? ModErrorCode.External : ModErrorCode.NotFound);
                follow.Fail(words);
                rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
                rig.Frame();
                That(overlay.IsVisible && overlay.HasFailed && overlay.FailureTitle == words.Title
                    && overlay.FailureMessage == words.Message && overlay.CanRetry == retryable && !overlay.CanCancel
                    && overlay.CanCopyDetails && overlay.Steps.Failed == (int)words.Step,
                    "a hidden re-follow's failure shows its own words, failed step and available actions");
                overlay.Dismiss();
                rig.Frame(40);
                That(!overlay.IsVisible, "closing that failure keeps it closed");
            }
        }

        private static void CancellingARefollowBeforeTheJoinCardClosesStillLeavesOnce()
        {
            var rig = RefollowDismissRig(0);
            var follow = rig.Environment.Follow;
            var overlay = rig.Presenter.Connecting;
            follow.Refollowing();
            follow.Move(FollowStage.LoadingWorld, "Loading the next world");
            var leaves = rig.Control.Calls.Count(call => call == "Leave");
            overlay.Cancel();
            overlay.Dismiss();
            rig.Frame(40);
            That(!overlay.IsVisible && rig.Control.Calls.Count(call => call == "Leave") == leaves + 1,
                "CANCEL uses the follower's classification even before the presenter advances the first join card");
            follow.Fail(FollowFailureWords.For(FollowFailureKind.SynchronizeFailed, ModErrorCode.TimedOut));
            rig.Frame(40);
            That(!overlay.IsVisible && rig.Control.Calls.Count(call => call == "Leave") == leaves + 1,
                "a late failure stays closed after cancelling that early re-follow");
            That(rig.Noticed(ConnectingPresenter.LeftWhileFollowing), "the early re-follow reports leaving the session");
        }

        private static void ClosingTheFirstJoinStillCancelsOnce()
        {
            foreach (var cancel in new[] { false, true })
                foreach (var staleRefollow in new[] { false, true })
                {
                    var rig = JoiningRig(staleRefollow ? status => { status.Joined(); status.Refollowing(); } : null);
                    var follow = rig.Environment.Follow;
                    if (!staleRefollow)
                    {
                        follow.Joined();
                        follow.Move(FollowStage.LoadingWorld, "Loading the host's world");
                    }
                    rig.Frame();
                    var overlay = rig.Presenter.Connecting;
                    var leaves = rig.Control.Calls.Count(call => call == "Leave");
                    if (cancel) overlay.Cancel();
                    else overlay.Dismiss();
                    overlay.Dismiss();
                    follow.Fail(FollowFailureWords.For(FollowFailureKind.WorldLoadFailed, ModErrorCode.External));
                    rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
                    rig.Frame(40);
                    That(!overlay.IsVisible && rig.Control.Calls.Count(call => call == "Leave") == leaves + 1,
                        "Escape, close and CANCEL on a first join cancel once and keep a late failure closed");
                    That(rig.Notices.Count(notice => notice.Text == "Join cancelled.") == 1,
                        "the first join still reports its cancellation once, including with a stale earlier re-follow");
                }
        }

        private static SessionsRig RefollowDismissRig(int frames)
        {
            var rig = JoiningRig();
            rig.Environment.Follow.Joined();
            rig.Environment.Follow.Move(FollowStage.Running, "In the host's world");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-ready", "p1");
            if (frames > 0) rig.Frame(frames);
            return rig;
        }
    }
}
