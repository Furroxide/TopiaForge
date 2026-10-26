using System;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>A joined player whose game waits at the main menu: what the overlay, MULTIPLAYER and SESSION show of it.</summary>
    internal static partial class MultiplayerSessionsTests
    {
        /// <summary>
        /// Seen in the game: a guest whose host went to its menu, or who was left at the menu because the host's level could
        /// not load here, was told nothing after the first toast, while MULTIPLAYER and SESSION still read "Joined Follow Host"
        /// and "Playing on the host's session.". Both now say why the game waits, for as long as it does, from the follow alone,
        /// and go back once the guest follows the host's next world; leaving clears it. A settled frame of it allocates nothing.
        /// </summary>
        private static void AGuestWaitingAtTheMenuIsToldWhy()
        {
            var rig = JoiningRig();
            var follow = rig.Environment.Follow;
            follow.Joined();
            follow.Move(FollowStage.Running, "In the host's world");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-ready", "p1");
            rig.Frame(40);
            var presenter = rig.Presenter;
            var joined = presenter.StatusText;
            const string Playing = "Playing on the host's session.";
            That(joined.StartsWith("Joined ", StringComparison.Ordinal) && presenter.StatusTone == ScreenTone.Success && presenter.Session.Subtitle == Playing,
                "in the host's world, MULTIPLAYER and SESSION say the player is joined (" + joined + ")");

            // The host goes to its menu, and the follow takes the game there: only the follow moves, the control's status does not.
            var version = presenter.Context.Status.Version;
            follow.Refollowing();
            follow.Move(FollowStage.ReturningToMenu, "The host went to the main menu");
            rig.Frame();
            follow.Move(FollowStage.AtMenu, "The host is at the main menu");
            rig.Frame();
            That(presenter.Context.Status.Version == version, "the control's status did not move");
            That(presenter.StatusText == FollowFailureWords.HostAtMenu && presenter.StatusTone == ScreenTone.Neutral && presenter.Session.Subtitle == FollowFailureWords.HostAtMenu,
                "MULTIPLAYER and SESSION say the host is at its menu and the player joins its next world (" + presenter.StatusText + " / " + presenter.Session.Subtitle + ")");
            rig.Frame(100);
            That(presenter.StatusText == FollowFailureWords.HostAtMenu && presenter.Session.Subtitle == FollowFailureWords.HostAtMenu, "for as long as the game waits");

            // The host's next world clears it.
            follow.Move(FollowStage.Preparing, "Getting your mods ready");
            rig.Frame();
            That(presenter.StatusText == joined && presenter.Session.Subtitle == Playing, "following the host's next world clears it");

            // A level this game cannot load leaves the game at the menu: both say that instead.
            follow.Move(FollowStage.LevelUnavailable, FollowFailureWords.LevelNotLoaded);
            rig.Frame();
            That(presenter.StatusText == FollowFailureWords.HostLevelUnavailable && presenter.Session.Subtitle == FollowFailureWords.HostLevelUnavailable,
                "a guest left at the menu because the host's level could not load here is told so for as long as it waits");
            follow.Move(FollowStage.Running, "In the host's world");
            rig.Frame();
            That(presenter.StatusText == joined && presenter.Session.Subtitle == Playing, "and back in the host's world it clears");

            // Leaving clears it with the session.
            follow.Move(FollowStage.AtMenu, "The host is at the main menu");
            rig.Frame();
            rig.Status(InternalSessionRole.Solo);
            rig.Environment.SessionSnapshot = null;
            follow.Ended();
            rig.Frame();
            That(presenter.StatusText == "Playing on your own." && presenter.Session.Subtitle.Length == 0 && follow.WaitingLine.Length == 0, "leaving clears it");

            // Waiting at the host's menu, settled, allocates nothing.
            var settled = JoiningRig();
            settled.Environment.Follow.Joined();
            settled.Environment.Follow.Move(FollowStage.AtMenu, "The host is at the main menu");
            settled.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            settled.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-menu", "p1");
            settled.Frame(40);
            That(settled.Presenter.StatusText == FollowFailureWords.HostAtMenu && settled.Presenter.Session.Subtitle == FollowFailureWords.HostAtMenu,
                "a join to a host at its menu says so on MULTIPLAYER and SESSION too");
            Settled("joined, waiting at the host's menu", settled);
            settled.Environment.Follow.Move(FollowStage.LevelUnavailable, FollowFailureWords.LevelNotLoaded);
            settled.Frame();
            Settled("joined, waiting for a level this game can load", settled);
        }

        /// <summary>
        /// Seen in the game: "Your game could not load the level the host is in…" was drawn as a success, every step ticked
        /// and a CANCEL that cancelled nothing, as the overlay counted the menu the game waits at as the follow's end. Now
        /// Loading world shows not done, the view says what happened and offers CLOSE, and it stays until closed; the toast
        /// still shows, the player stays in the session, and the host's next world takes the view over. A join whose host is
        /// in such a level shows the same, never a host at its menu.
        /// </summary>
        private static void ALevelThisGameCannotLoadShowsAsNotLoaded()
        {
            var rig = JoiningRig();
            var follow = rig.Environment.Follow;
            follow.Joined();
            follow.Move(FollowStage.Running, "In the host's world");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-ready", "p1");
            rig.Frame(40);
            var overlay = rig.Presenter.Connecting;
            follow.Refollowing();
            follow.Move(FollowStage.LoadingLevel, "Loading the host's level");
            rig.Frame();
            That(overlay.IsVisible && overlay.Title == ConnectingPresenter.FollowingTitle && overlay.CanCancel,
                "following the host into a native level shows, with Cancel");

            // As the follower does: the toast once the level fails, then the menu it waits at.
            follow.Notify(FollowFailureWords.LevelNotLoaded);
            follow.Move(FollowStage.LevelUnavailable, FollowFailureWords.LevelNotLoaded);
            rig.Frame();
            That(overlay.IsVisible && !overlay.HasFailed && !overlay.Steps.Complete && overlay.Steps.Failed == (int)ConnectingStep.LoadingWorld,
                "a level this game cannot load shows Loading world not done, never every step ticked (failed " + overlay.Steps.Failed + ")");
            That(overlay.StatusText == FollowFailureWords.LevelNotLoaded && !overlay.CanCancel && rig.Noticed(FollowFailureWords.LevelNotLoaded),
                "the view says what happened, with nothing Cancel would stop, and the toast still shows");
            rig.Frame(60);
            var leaves = rig.Control.Calls.Count(call => call == "Leave");
            overlay.Cancel();
            rig.Frame();
            That(overlay.IsVisible && rig.Control.Calls.Count(call => call == "Leave") == leaves,
                "it stays until the player closes it, and Cancel leaves nothing, so the view offers CLOSE instead");
            overlay.Dismiss();
            rig.Frame();
            That(!overlay.IsVisible && rig.Control.Calls.Count(call => call == "Leave") == leaves, "CLOSE closes it, and the player stays in the session");

            // The host's next world takes the view over, even while it shows.
            follow.Move(FollowStage.LevelUnavailable, FollowFailureWords.LevelNotLoaded);
            follow.Refollowing();
            follow.Move(FollowStage.LoadingLevel, "Loading the host's level");
            rig.Frame();
            follow.Move(FollowStage.LevelUnavailable, FollowFailureWords.LevelNotLoaded);
            rig.Frame();
            That(overlay.IsVisible && !overlay.CanCancel, "the view shows again for the host's next level this game cannot load");
            follow.Move(FollowStage.Preparing, "Getting your mods ready");
            rig.Frame();
            That(overlay.IsVisible && overlay.Title == ConnectingPresenter.FollowingTitle && overlay.CanCancel
                && overlay.Steps.Active == (int)ConnectingStep.LoadingWorld && overlay.Steps.Failed < 0,
                "and the host's next world replaces it with the follow");

            // The player leaves while the view shows: it closes.
            follow.Move(FollowStage.LevelUnavailable, FollowFailureWords.LevelNotLoaded);
            rig.Frame();
            follow.Ended();
            rig.Frame();
            That(!overlay.IsVisible, "leaving the session closes the view");

            // A join whose host is in a level this game cannot load completes in the session, shown as what it is.
            var joining = JoiningRig();
            var status = joining.Environment.Follow;
            status.Joined();
            status.Move(FollowStage.LoadingLevel, "Loading the host's level");
            joining.Frame();
            status.Notify(FollowFailureWords.LevelNotLoaded);
            status.Move(FollowStage.LevelUnavailable, FollowFailureWords.LevelNotLoaded);
            joining.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            joining.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-level", "p1");
            joining.Frame();
            var view = joining.Presenter.Connecting;
            That(view.IsVisible && !view.Steps.Complete && view.Steps.Failed == (int)ConnectingStep.LoadingWorld
                && view.StatusText == FollowFailureWords.LevelNotLoaded && !view.CanCancel,
                "a join whose host is in a level this game cannot load shows Loading world not done, with CLOSE");
            That(joining.Noticed("Joined LAN game.") && joining.Noticed(FollowFailureWords.LevelNotLoaded) && !joining.Noticed(ConnectingPresenter.HostAtMenu),
                "the join and why the game waits are toasted, and the host is never said to be at its menu");
            joining.Frame(60);
            That(view.IsVisible, "and the view stays until the player closes it");

            // A real later failure replaces the settled view, and RETRY can start another join after it left.
            var failure = FollowFailureWords.For(FollowFailureKind.WorldLoadFailed, ModErrorCode.External);
            status.Refollowing();
            status.Fail(failure);
            joining.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
            joining.Environment.SessionSnapshot = null;
            joining.Frame();
            That(view.HasFailed && view.FailureTitle == failure.Title && view.CanRetry, "a stranded view never hides a later failure");
            var joins = Calls(joining, "Join:");
            view.Retry();
            That(Calls(joining, "Join:") == joins + 1 && view.CanCancel && !view.HasFailed, "the settled view does not block another join");
        }
    }
}
