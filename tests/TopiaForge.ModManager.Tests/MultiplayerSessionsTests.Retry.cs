using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>RETRY after a follow failed: a LAN host listened for first, and what the failure view shows when it fails again.</summary>
    internal static partial class MultiplayerSessionsTests
    {
        /// <summary>
        /// Seen in the game: RETRY after a follow failed could not join a LAN host again once BROWSE had stopped listening, as
        /// it does while the player plays, since the provider finds a LAN session only among those this game hears. RETRY now
        /// listens for the session first, for a launched LAN join's 15 seconds at most, and joins once it is heard; a host no
        /// longer there ends it in words that say so, with RETRY to listen again. A search the overlay did not start is
        /// never stopped by it, and relay and direct joins never listen.
        /// </summary>
        private static void RetryListensForALanHostFirst()
        {
            var words = FollowFailureWords.For(FollowFailureKind.WorldLoadFailed, ModErrorCode.External);
            var listenFrames = (int)(ConnectingPresenter.LanListenSeconds / 0.05f);

            // The host is heard again: the join goes ahead, and the listening stops once the provider has looked it up.
            var rig = SearchStopped(RefollowFailedRig(words));
            var overlay = rig.Presenter.Connecting;
            var joins = Calls(rig, "Join:");
            var starts = Calls(rig, "StartBrowsing");
            var stops = Calls(rig, "StopBrowsing");
            overlay.Retry();
            rig.Frame();
            That(Calls(rig, "Join:") == joins && Calls(rig, "StartBrowsing") == starts + 1,
                "RETRY of a LAN join whose session is not heard listens on the local network first, and joins nothing yet");
            That(overlay.IsVisible && !overlay.HasFailed && overlay.CanCancel && overlay.Steps.Active == (int)ConnectingStep.Resolving
                && overlay.DetailOf((int)ConnectingStep.Resolving) == ConnectingPresenter.ListeningOnLan && overlay.StatusText == LaunchSessionPresenter.LookingOnLan,
                "the overlay says it looks for the session on the local network, and can be cancelled");
            rig.Frame(20);
            That(Calls(rig, "Join:") == joins && overlay.IsVisible && !overlay.HasFailed, "nothing is joined while the session is not heard");
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            rig.Frame();
            That(Calls(rig, "Join:") == joins + 1 && rig.Control.Calls.Last(call => call.StartsWith("Join:", StringComparison.Ordinal)) == "Join:Lan:relay"
                && Calls(rig, "StopBrowsing") == stops,
                "once the host is heard the join goes ahead, over the local network, still listening while the provider looks it up");
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Connecting);
            rig.Frame();
            That(Calls(rig, "StopBrowsing") == stops + 1, "then the overlay stops the listening it started");

            // The host is gone: after the launched join's bound the join ends in words that say so, and RETRY listens again.
            var gone = SearchStopped(RefollowFailedRig(words));
            var view = gone.Presenter.Connecting;
            joins = Calls(gone, "Join:");
            stops = Calls(gone, "StopBrowsing");
            view.Retry();
            gone.Frame(listenFrames - 2);
            That(view.IsVisible && !view.HasFailed && Calls(gone, "Join:") == joins, "the overlay listens for the whole bound");
            gone.Frame(4);
            That(view.HasFailed && view.FailureTitle == ConnectingPresenter.NotHeardOnLan.Title && view.FailureMessage == ConnectingPresenter.NotHeardOnLan.Message
                && view.Steps.Failed == (int)ConnectingStep.Resolving && view.CanRetry && view.CanCopyDetails,
                "a host no longer on the network ends the join, saying it was not heard there, with RETRY to listen again");
            That(Calls(gone, "Join:") == joins && Calls(gone, "StopBrowsing") == stops + 1, "having joined nothing, and stopped listening");
            That(view.FailureMessage.Contains("local network", StringComparison.Ordinal) && view.FailureMessage.EndsWith("then try again.", StringComparison.Ordinal),
                "the words say where the host was looked for and what to do next");
            starts = Calls(gone, "StartBrowsing");
            view.Retry();
            gone.Frame();
            That(Calls(gone, "StartBrowsing") == starts + 1 && view.DetailOf((int)ConnectingStep.Resolving) == ConnectingPresenter.ListeningOnLan, "RETRY listens again");
            view.Cancel();
            gone.Frame();
            That(!view.IsVisible && Calls(gone, "Join:") == joins && Calls(gone, "StopBrowsing") == stops + 2 && gone.Noticed("Join cancelled."),
                "Cancel while listening stops it, joins nothing and says so");

            // A search BROWSE still runs is its own: the overlay listens through it and neither starts nor stops it.
            var browsing = RefollowFailedRig(words);
            browsing.Control.Lan = Array.Empty<InternalLanListing>();
            browsing.Control.Status.IsBrowsing = true;
            starts = Calls(browsing, "StartBrowsing");
            stops = Calls(browsing, "StopBrowsing");
            browsing.Presenter.Connecting.Retry();
            browsing.Frame(listenFrames + 2);
            That(browsing.Presenter.Connecting.HasFailed && Calls(browsing, "StartBrowsing") == starts && Calls(browsing, "StopBrowsing") == stops,
                "listening through BROWSE's own search neither starts nor stops it");

            // A LAN session still heard is joined at once, as before, and a direct join never listens.
            var heard = RefollowFailedRig(words);
            joins = Calls(heard, "Join:");
            starts = Calls(heard, "StartBrowsing");
            heard.Presenter.Connecting.Retry();
            That(Calls(heard, "Join:") == joins + 1 && Calls(heard, "StartBrowsing") == starts, "RETRY of a LAN session still heard joins it at once");
            var direct = new SessionsRig();
            direct.Presenter.Connect.SetAddress("192.0.2.4:27250");
            direct.Presenter.Connect.JoinAddress();
            direct.Frame();
            direct.Control.Queries[0].Complete(InternalLiveFailure.None, Server('2', "Typed"));
            direct.Frame();
            direct.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.TimedOut);
            direct.Frame();
            joins = Calls(direct, "Join:");
            That(direct.Presenter.Connecting.CanRetry, "a direct join that timed out offers RETRY");
            direct.Presenter.Connecting.Retry();
            That(Calls(direct, "Join:") == joins + 1 && Calls(direct, "StartBrowsing") == 0, "RETRY of a direct join joins at once and never listens on the local network");
        }

        /// <summary>BROWSE stops searching, as when its tab closes while the player plays, and the local network lists nothing.</summary>
        private static SessionsRig SearchStopped(SessionsRig rig)
        {
            rig.Presenter.Browse.StopSearch();
            rig.Control.Lan = Array.Empty<InternalLanListing>();
            rig.Frame();
            return rig;
        }

        private static int Calls(SessionsRig rig, string call) => rig.Control.Calls.Count(entry => entry.StartsWith(call, StringComparison.Ordinal));

        /// <summary>
        /// A RETRY that fails at once replaces one failure with another before the view's next frame: the failure view never
        /// closes in between, so the overlay keeps its failure layout, and the screen gives the new failure's heading and
        /// words, which the overlay binds on every frame (<c>MultiplayerUiArchitectureTests</c> reads that from its source).
        /// </summary>
        private static void ARetryThatFailsAtOnceShowsItsOwnWords()
        {
            var rig = RefollowFailedRig(FollowFailureWords.For(FollowFailureKind.WorldLoadFailed, ModErrorCode.External));
            var overlay = rig.Presenter.Connecting;
            rig.Control.JoinResult = OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The provider is not ready.");
            overlay.Retry();
            That(overlay.IsVisible && overlay.HasFailed, "a RETRY that fails at once is failed again before the view's next frame, so its layout stays");
            That(overlay.FailureTitle == "Join refused" && overlay.FailureMessage == "The provider is not ready." && overlay.StatusText == "Join refused",
                "and the screen gives the new failure's heading and words, never the earlier one's (" + overlay.FailureTitle + ")");
        }

        /// <summary>A rig whose LAN join completed, after which a follow of the host's next world failed with <paramref name="words"/>.</summary>
        private static SessionsRig RefollowFailedRig(FollowFailure words)
        {
            var rig = JoiningRig();
            var follow = rig.Environment.Follow;
            follow.Joined();
            follow.Move(FollowStage.Running, "In the host's world");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-ready", "p1");
            rig.Frame(40);
            follow.Refollowing();
            follow.Move(FollowStage.LoadingWorld, "Loading the host's world");
            rig.Frame();
            follow.Fail(words);
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
            rig.Environment.SessionSnapshot = null;
            rig.Frame();
            var overlay = rig.Presenter.Connecting;
            That(overlay.IsVisible && overlay.HasFailed && overlay.FailureTitle == words.Title && overlay.CanRetry == words.Retryable,
                "a follow that failed after the join shows its own words (" + overlay.FailureTitle + ")");
            return rig;
        }
    }
}
