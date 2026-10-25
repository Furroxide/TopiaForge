using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>RETRY after a follow failed: what the failure view shows when it fails again.</summary>
    internal static partial class MultiplayerSessionsTests
    {
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
