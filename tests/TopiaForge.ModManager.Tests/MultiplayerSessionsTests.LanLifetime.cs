using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MultiplayerSessionsTests
    {
        private static void HearingTheHostWhileBusyReleasesTheBrowser()
        {
            var rig = SearchStopped(RefollowFailedRig(FollowFailureWords.For(FollowFailureKind.WorldLoadFailed, ModErrorCode.External)));
            var stops = Calls(rig, "StopBrowsing");
            rig.Presenter.Connecting.Retry();
            rig.Environment.Phase = SessionPhase.Stopping;
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            rig.Frame();
            That(rig.Presenter.Connecting.StatusText == ConnectingPresenter.WaitingText && Calls(rig, "StopBrowsing") == stops + 1,
                "hearing the host while the game is busy moves to Waiting and releases the browser");
            var starts = Calls(rig, "StartBrowsing");
            rig.Frame(400);
            That(Calls(rig, "StartBrowsing") == starts, "a game that stays busy cannot keep restarting LAN discovery");
            rig.Control.Lan = Array.Empty<InternalLanListing>();
            rig.Environment.Phase = SessionPhase.Idle;
            rig.Frame();
            That(Calls(rig, "StartBrowsing") == starts + 1 && rig.Presenter.Connecting.StatusText == LaunchSessionPresenter.LookingOnLan,
                "once the game settles, a lost beacon gets a new bounded listen");
            rig.Presenter.Connecting.Cancel();
            rig.Frame();
            That(Calls(rig, "StopBrowsing") == stops + 2, "Cancel releases the new listen too");
        }

        private static void RetrySharesTheLaunchSearch()
        {
            foreach (var launchFirst in new[] { true, false })
            {
                var rig = SearchStopped(RefollowFailedRig(FollowFailureWords.For(FollowFailureKind.WorldLoadFailed, ModErrorCode.External)));
                var starts = Calls(rig, "StartBrowsing");
                var stops = Calls(rig, "StopBrowsing");
                if (!launchFirst) rig.Presenter.Connecting.Retry();
                var launch = Launch(rig, JoinSession(InternalLaunchTarget.Listing(LanServer, LaunchedDescriptor, lan: true)));
                rig.Frame();
                if (launchFirst) rig.Presenter.Connecting.Retry();
                rig.Frame();
                rig.Presenter.Connecting.Cancel();
                rig.Frame();
                That(Calls(rig, "StartBrowsing") == starts + 1 && Calls(rig, "StopBrowsing") == stops,
                    "RETRY and a launched join share one search; cancelling RETRY keeps the launch listening in either start order");
                rig.Control.Lan = new[] { Listing('1', "LAN game") };
                rig.Frame();
                That(launch.Outcome == null && rig.Presenter.Connecting.CanCancel, "the launched join can still hear and join its host");
                rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
                rig.Frame();
                That(launch.Outcome?.Status == "succeeded" && Calls(rig, "StopBrowsing") == stops + 1,
                    "the browser stops exactly once when the remaining launched join settles");
                rig.Frame(10);
                That(Calls(rig, "StopBrowsing") == stops + 1, "settled frames do not stop it again");
            }
        }

        private static void RetryKeepsListeningWhenBrowseCloses()
        {
            var rig = RefollowFailedRig(FollowFailureWords.For(FollowFailureKind.WorldLoadFailed, ModErrorCode.External));
            rig.Control.Lan = Array.Empty<InternalLanListing>();
            var starts = Calls(rig, "StartBrowsing");
            var stops = Calls(rig, "StopBrowsing");
            rig.Presenter.Connecting.Retry();
            rig.Presenter.OnHidden();
            rig.Frame();
            That(Calls(rig, "StartBrowsing") == starts && Calls(rig, "StopBrowsing") == stops,
                "closing BROWSE while RETRY listens keeps the shared browser alive");
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            rig.Frame();
            That(Calls(rig, "StopBrowsing") == stops, "the provider still needs the list while resolving");
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Connecting);
            rig.Frame();
            That(Calls(rig, "StopBrowsing") == stops + 1, "once resolved the last share stops browsing");
        }

        private static void LanSearchOwnershipSurvivesStaleStatus()
        {
            var rig = new SessionsRig();
            var search = rig.Presenter.Context.LanBrowsing;
            // The provider starts a search outside these screens after the tracker's last read.
            rig.Control.Status = new InternalLiveStatus { IsLiveAvailable = true, IsBrowsing = true };
            That(!rig.Presenter.Context.Status.Status.IsBrowsing, "the cached status predates the external search");
            That(search.Start(LanBrowseUse.Join), "the join takes a share without claiming the external browser");
            search.Stop(LanBrowseUse.Join);
            That(Calls(rig, "StartBrowsing") == 0 && Calls(rig, "StopBrowsing") == 0,
                "a fresh action-time status leaves an external search untouched");

            rig.Control.Status = new InternalLiveStatus { IsLiveAvailable = true };
            That(search.Start(LanBrowseUse.Browse) && search.Start(LanBrowseUse.Join) && search.Start(LanBrowseUse.Launch),
                "all three screens share a browser started here");
            search.Stop(LanBrowseUse.Browse);
            search.Stop(LanBrowseUse.Browse);
            search.Stop(LanBrowseUse.Join);
            That(Calls(rig, "StartBrowsing") == 1 && Calls(rig, "StopBrowsing") == 0, "release is idempotent and the launch still needs the list");
            rig.Presenter.Stop();
            search.Stop(LanBrowseUse.Launch);
            That(Calls(rig, "StopBrowsing") == 1, "stopping the screens releases all shares exactly once");
        }
    }
}
