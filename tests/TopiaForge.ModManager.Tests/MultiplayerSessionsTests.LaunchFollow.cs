using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Session follow reports to the launcher only for a join the launcher planned, under that join's request (WS3.8). A
    /// launched join hands the realm its request while its launch still waits for its result; a join started in game, and
    /// RETRY after a launched join failed, hand none. A live join's records take the follow's progress under its join id:
    /// the follow's phase and the host's world session move under the join's block once it is in session, the success
    /// stands at Running with that session, and the end names it; the records then let go of the follow's progress.
    /// </summary>
    internal static partial class MultiplayerSessionsTests
    {
        private static void LaunchedJoinsReportTheirFollows()
        {
            ALaunchedJoinHandsTheRealmItsRequest();
            ALiveJoinsRecordsTakeItsFollowsProgress();
            LiveJoinFollowFailuresReachTheLauncher();
        }

        private static void LiveJoinFollowFailuresReachTheLauncher()
        {
            foreach (var started in new[] { false, true })
            {
                const string join = "join-follow-failed";
                var rig = Owned();
                rig.Control.Lan = new[] { Listing('1', "LAN game") };
                Offer(rig, LiveJoin(join));
                rig.Frame();
                rig.Status(InternalSessionRole.Client, started ? InternalJoinPhase.InSession : InternalJoinPhase.Synchronizing);
                rig.Environment.ReportSessionProgress(new LaunchProgress(join, 1, "running", "session-a1"));
                rig.Frame(2);
                rig.Environment.Follow.Joined(join);
                var words = FollowFailureWords.For(FollowFailureKind.WorldLoadFailed, ModErrorCode.External);
                rig.Environment.Follow.Fail(words);
                rig.Environment.ReportSessionProgress(new LaunchProgress(join, 2, "idle"));
                rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Left);
                rig.Frame(3);
                var outcome = rig.Launcher.Outcomes.Last();
                That(outcome.Multiplayer!.Failure == (started ? null : "world-follow-failed") && outcome.Multiplayer.Detail == words.Title + ". " + words.Message,
                    "the live join presenter forwards the follow's fixed reason before generic transport cancellation");
                That(started ? outcome.Kind == "session" && outcome.SessionId == "session-a1"
                    && outcome.Multiplayer.EndReason == "world-follow-failed" : outcome.Kind == "launch" && outcome.Status == "failed",
                    "live join failure uses the right record and keeps the previous world after cleanup");
            }
        }

        private static void ALaunchedJoinHandsTheRealmItsRequest()
        {
            var rig = Owned();
            var sink = Launch(rig, JoinSession(Typed("192.168.1.20")));
            rig.Frame();
            That(rig.Control.Calls.Contains("Join:Direct:relay") && rig.Environment.Runtime.LaunchedJoin == OwnerRequest,
                "a launched join goes online under its launch request, which session follow reports under");
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.TimedOut);
            rig.Frame();
            That(sink.Outcome?.Status == "failed" && rig.Presenter.Connecting.CanRetry, "the launched join times out, its launch settled, and RETRY is offered");
            rig.Presenter.Connecting.Retry();
            rig.Frame();
            That(rig.Control.Calls.Count(call => call == "Join:Direct:relay") == 2 && rig.Environment.Runtime.LaunchedJoin == null,
                "RETRY joins again as the player's own join, with no request: the launcher keeps the result it has");

            var own = BrowsingRig(ready: true);
            own.Presenter.Browse.Select(LanServer);
            own.Presenter.Browse.Join();
            own.Frame();
            That(own.Control.Calls.Contains("Join:Lan:relay") && own.Environment.Runtime.LaunchedJoin == null, "a join started in game hands no request");
        }

        private static void ALiveJoinsRecordsTakeItsFollowsProgress()
        {
            const string join = "join-03";
            var rig = Owned();
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            Offer(rig, LiveJoin(join));
            rig.Frame();
            That(rig.Control.Calls.Contains("Join:Lan:relay") && rig.Environment.Runtime.LaunchedJoin == join,
                "a live join goes online under its join id, which session follow reports under");
            That(rig.Environment.ProgressSubscribers.Count(subscriber => subscriber.RequestId == join) == 1, "and its records take the progress reported under it");

            // The follow runs while the join synchronizes: the join's own phase stands. Records land a frame after they are written.
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.Synchronizing);
            rig.Frame(2);
            var written = rig.Launcher.Progress.Count;
            rig.Environment.ReportSessionProgress(new LaunchProgress(join, 3, "preparing", "session-a1"));
            rig.Environment.ReportSessionProgress(new LaunchProgress(join, 6, "running", "session-a1"));
            rig.Frame(2);
            That(rig.Launcher.Progress.Count == written && rig.Launcher.Progress.Last().Phase == "synchronizing" && rig.Launcher.Progress.Last().SessionId == null,
                "while the join synchronizes, the follow's progress waits behind the join's own phase");

            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Frame(2);
            var joined = rig.Launcher.Outcomes.Last();
            var inSession = rig.Launcher.Progress.Last();
            That(joined.RequestId == join && joined.Status == "succeeded" && joined.Phase == "running" && joined.SessionId == "session-a1",
                "in session in the host's world, the live join succeeds at Running with the host's world session (" + joined.Phase + ", " + joined.SessionId + ")");
            That(inSession.RequestId == join && inSession.Phase == "running" && inSession.SessionId == "session-a1" && inSession.Session!.State == "in-session",
                "and its progress says so, under its join id");

            // The host changes worlds: the follow's phases move on under the join's block; another request's change nothing.
            rig.Environment.ReportSessionProgress(new LaunchProgress(join, 9, "preparing", "session-b2"));
            rig.Environment.ReportSessionProgress(new LaunchProgress("someone-else", 10, "running", "session-zz"));
            rig.Environment.ReportSessionProgress(new LaunchProgress(join, 8, "stopping", "session-a1"));
            rig.Frame(2);
            var moved = rig.Launcher.Progress.Last();
            That(moved.Phase == "preparing" && moved.SessionId == "session-b2" && moved.Session!.State == "in-session" && moved.Sequence > inSession.Sequence,
                "the host's next world moves the phase on under the join's block, and an older or another request's record changes nothing");
            rig.Environment.ReportSessionProgress(new LaunchProgress(join, 12, "running", "session-b2"));
            rig.Frame();

            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.HostEnded);
            rig.Frame(2);
            var ended = rig.Launcher.Outcomes.Last();
            That(ended.Kind == "session" && ended.RequestId == join && ended.SessionId == "session-b2" && ended.Multiplayer!.EndReason == "host-ended",
                "the join's end names the host's world it was in");
            That(rig.Environment.ProgressSubscribers.Count == 0 && rig.Environment.Runtime.LaunchedJoin == null,
                "and the records let go of the follow's progress, as the realm lets go of the join");
            var after = rig.Launcher.Progress.Count;
            rig.Environment.ReportSessionProgress(new LaunchProgress(join, 14, "idle"));
            rig.Frame(2);
            That(rig.Launcher.Progress.Count == after, "nothing the follow reports after the end reaches the join's records");
        }
    }
}
