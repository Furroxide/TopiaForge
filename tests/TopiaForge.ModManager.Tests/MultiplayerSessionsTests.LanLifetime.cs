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
            var shown = rig.Presenter.Connecting.Steps.Version;
            rig.Frame(400);
            That(Calls(rig, "StartBrowsing") == starts && rig.Presenter.Connecting.Steps.Version == shown,
                "a game that stays busy cannot keep restarting LAN discovery, nor show the wait again each frame");
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
                var joins = Calls(rig, "Join:");
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
                That(launch.Outcome == null && rig.Presenter.Connecting.CanCancel && Calls(rig, "Join:") == joins + 1,
                    "the launched join can still hear and join its host");
                rig.Frame(10);
                That(Calls(rig, "StopBrowsing") == stops,
                    "and keeps listening while the provider looks its session up, as a launch lets go of the browser only once it settles");
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
            search.Stop(LanBrowseUse.Launch);
            That(Calls(rig, "StopBrowsing") == 1, "a share released twice is released once, so the launch's release is the last and stops the browser");

            That(search.Start(LanBrowseUse.Join) && search.Start(LanBrowseUse.Launch) && Calls(rig, "StartBrowsing") == 2,
                "the next share starts a browser of its own");
            rig.Presenter.Stop();
            search.Stop(LanBrowseUse.Launch);
            That(Calls(rig, "StopBrowsing") == 2, "stopping the screens releases all shares exactly once");
            That(search.Start(LanBrowseUse.Browse) && Calls(rig, "StartBrowsing") == 3, "and keeps none of them, so a later share starts its own browser");
        }

        /// <summary>
        /// A provider that throws as it starts listening leaves no share behind: BROWSE says it could not listen and does not
        /// search, and once the provider can listen again the next search starts a browser of its own, which its release stops.
        /// </summary>
        private static void ALanSearchThatCannotStartHoldsNoShare()
        {
            var rig = new SessionsRig();
            var browse = rig.Presenter.Browse;
            rig.Control.ThrowOnStartBrowsing = new InvalidOperationException("The discovery port is in use.");
            browse.StartSearch();
            rig.Frame();
            That(!browse.IsSearching && Calls(rig, "StartBrowsing") == 1 && Calls(rig, "StopBrowsing") == 0
                && rig.Noticed("This game could not listen on the local network."),
                "a provider that cannot start listening leaves BROWSE not searching, and says so");
            rig.Control.ThrowOnStartBrowsing = null;
            browse.StartSearch();
            That(browse.IsSearching && Calls(rig, "StartBrowsing") == 2, "the failed start kept no share, so searching again starts the browser");
            browse.StopSearch();
            That(Calls(rig, "StopBrowsing") == 1, "and BROWSE's release, the last one, stops it");
        }
    }
}
