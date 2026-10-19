using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A launch's records follow that launch alone, whatever came before or after it. A join that starts once the game it
    /// left has gone is settled by its own status, never by the end of an earlier join that the provider still reports; a
    /// session still running when a newer launch begins is followed to its end; a command that fails while its launch waits
    /// lets go of the launch; a live join whose session runs as the game closes ends in its records; and a launch that
    /// cannot start says why in game, where the player is looking.
    /// </summary>
    internal static partial class MultiplayerSessionsTests
    {
        private static void LaunchesEndOnTheirOwnRecords()
        {
            AJoinThatStartsAfterAWaitIsSettledByItsOwnStatus();
            ARunningLaunchIsFollowedToItsEndPastTheNextOne();
            ACommandThatFailsWhileItsLaunchWaitsLetsGoOfIt();
            ALiveJoinEndsInItsRecordsAsTheGameCloses();
            ALaunchThatCannotStartSaysSoInGame();
        }

        /// <summary>
        /// The provider reports an ended join until the next one starts. A launched join that starts from the overlay's wait,
        /// after "Leave current game?", began after the frame's status was read, so only a later read can settle it.
        /// </summary>
        private static void AJoinThatStartsAfterAWaitIsSettledByItsOwnStatus()
        {
            var rig = Owned();
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
            rig.Frame();
            rig.Environment.Phase = SessionPhase.Running;
            Offer(rig, LiveJoin("join-after-leaving"));
            That(rig.Prompt?.Title == ConnectingPresenter.LeaveTitle, "a live join while a game runs, after an earlier session ended, asks to leave the game first");
            rig.Answer(ScreenPromptKind.Destructive, true);
            rig.Environment.PendingMenu!(true, "Returned to the main menu.");
            rig.Environment.Phase = SessionPhase.Idle;
            rig.Frame(2);
            That(rig.Control.Calls.Contains("Join:Lan:relay") && !rig.Launcher.Outcomes.Any(outcome => outcome.RequestId == "join-after-leaving") &&
                 rig.Launcher.Progress.Where(record => record.RequestId == "join-after-leaving").All(record => record.Session!.State != "ended"),
                "the join that starts once the game has gone is never settled by the earlier join's end");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Frame(2);
            var joined = rig.Launcher.Outcomes.Single(outcome => outcome.RequestId == "join-after-leaving");
            That(joined.Status == "succeeded" && joined.Kind == "launch", "it reports the join it started (" + joined.Status + ")");
        }

        private static void ARunningLaunchIsFollowedToItsEndPastTheNextOne()
        {
            var rig = Owned();
            var sink = Launch(rig, JoinSession(Typed("192.168.1.20")));
            rig.Frame();
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Frame(2);
            That(sink.Outcome?.Status == "succeeded", "the launched join is in session");
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            Offer(rig, LiveJoin("join-while-joined"));
            var refused = rig.Launcher.Outcomes.Single(outcome => outcome.RequestId == "join-while-joined");
            That(refused.Status == "failed" && refused.Error?.Code == "invalidState" && sink.End == null,
                "a live join while that session runs is turned away, and the session runs on");
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Kicked);
            rig.Frame(2);
            That(sink.End?.EndReason == "kicked", "the first launch is still followed to its session's end (" + (sink.End?.EndReason ?? "none") + ")");
        }

        /// <summary>The runtime's command can fail on a frame the provider's status does not move, as a host's target from the menu does.</summary>
        private static void ACommandThatFailsWhileItsLaunchWaitsLetsGoOfIt()
        {
            var rig = Owned();
            var sink = Launch(rig, HostSession(password: false), startsGame: true, game: LaunchedGame.Starting);
            rig.Frame(3);
            That(rig.Presenter.Launches.IsActive && sink.Outcome == null, "the launch waits while its own game starts");
            sink.Result(SessionLaunchRecords.Refused(new LaunchSessionIdentity(OwnerRequest, "host", LaunchIntent), ModErrorCode.Unavailable,
                "An additive arena requires an existing loaded gameplay scene."), null);
            sink.Game = LaunchedGame.Failed;
            rig.Frame();
            That(!rig.Presenter.Launches.IsActive, "the launch is let go of once its command failed, though the provider's status never moved");
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            Offer(rig, LiveJoin("join-after-a-failed-command"));
            That(rig.Control.Calls.Contains("Join:Lan:relay") && !rig.Launcher.Outcomes.Any(outcome => outcome.RequestId == "join-after-a-failed-command"),
                "so a later live join goes ahead, never turned away as another launch still starting");
        }

        private static void ALiveJoinEndsInItsRecordsAsTheGameCloses()
        {
            var rig = Owned();
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            Offer(rig, LiveJoin("join-as-the-game-closes"));
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Frame(2);
            That(rig.Launcher.Outcomes.Any(outcome => outcome.RequestId == "join-as-the-game-closes" && outcome.Kind == "launch" && outcome.Status == "succeeded"),
                "the live join is in session");
            rig.Presenter.Stop();
            rig.Work.RunAll();
            var end = rig.Launcher.Outcomes.Where(outcome => outcome.RequestId == "join-as-the-game-closes" && outcome.Kind == "session").ToArray();
            That(end.Length == 1 && end[0].Status == "succeeded" && end[0].Multiplayer!.EndReason == "left" && end[0].Phase == "idle",
                "the game closing with its session running ends it in the join's own records, as the player leaving it");

            // The session this game was launched with ends in the runtime's publisher instead, which knows how cleanly the runtime stopped.
            var launched = Owned();
            var sink = Launch(launched, JoinSession(Typed("192.168.1.20")));
            launched.Frame();
            launched.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            launched.Frame(2);
            launched.Presenter.Stop();
            That(sink.Outcome?.Status == "succeeded" && sink.End == null, "a launch request's session is left to the publisher's end at exit");
        }

        private static void ALaunchThatCannotStartSaysSoInGame()
        {
            // Hosting fails before it opens, as when another program holds the launch's own port: the player hears HOST's words.
            var taken = Owned();
            var host = Launch(taken, HostSession(password: false));
            taken.Frame();
            taken.Control.Status = new InternalLiveStatus { IsLiveAvailable = true, HostPhase = InternalHostPhase.Failed, HostFailure = InternalLiveFailure.PortInUse };
            taken.Presenter.Context.Status.RequestRefresh();
            taken.Frame();
            var portInUse = LiveFailureMessages.ForHost(InternalLiveFailure.PortInUse);
            That(host.Outcome?.Multiplayer.Failure == "port-in-use" && taken.Noticed(portInUse.Title + ". " + portInUse.Message),
                "a launched host that cannot open says so in game, as HOST says it");

            // A launch this game turns away tells the player why.
            var stopped = Owned();
            Launch(stopped, HostSession(password: false), startsGame: true);
            stopped.Frame();
            That(stopped.Noticed(LaunchSessionPresenter.NotStarted + " " + LaunchSessionPresenter.GameStopped), "a launch this game cannot start says why");

            // A LAN session never heard: the player sees the search start, and hears that it ended.
            var silent = Owned();
            var unheard = Launch(silent, JoinSession(InternalLaunchTarget.Listing(LanServer, LaunchedDescriptor, lan: true)));
            silent.Frame();
            That(silent.Noticed(LaunchSessionPresenter.LookingOnLan), "a LAN session the launch looks for says so");
            silent.Frame(16, deltaTime: 1f);
            That(unheard.Outcome?.Multiplayer.Failure == "timeout" && silent.Noticed(LaunchSessionPresenter.NotHeardHere),
                "and a session never heard says what to do next");

            // The player's own choice is not told back to them.
            var declined = Owned();
            var cancelled = Launch(declined, HostSession(password: true));
            declined.Frame();
            declined.Prompt!.Answer(false, string.Empty);
            declined.Frame();
            That(cancelled.Outcome?.Status == "cancelled" && !declined.Noticed(LaunchSessionPresenter.NotStarted), "a password turned down tells nothing more");

            // A live join turned away while another launch starts is never announced as joining.
            var busy = Owned();
            busy.Environment.Phase = SessionPhase.Running;
            Launch(busy, JoinSession(Typed("192.168.1.20")));
            busy.Frame();
            busy.Control.Lan = new[] { Listing('1', "LAN game") };
            Offer(busy, LiveJoin("join-while-another-starts"));
            That(busy.Launcher.Outcomes.Single(outcome => outcome.RequestId == "join-while-another-starts").Error?.Code == "conflict" &&
                 !busy.Noticed(LaunchSessionPresenter.LiveJoinArrived), "a live join turned away as a conflict is not announced");
        }
    }
}
