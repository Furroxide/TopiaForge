using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// What else the launcher and a running game share: live joins, checked and acted on once, which the staging store's
    /// take refuses when stale, foreign or unreadable; handoffs to the launcher that owns this game; the launcher's
    /// preferences, read as the launcher applies its journal; and frames with a launch at rest, which allocate nothing.
    /// </summary>
    internal static partial class MultiplayerSessionsTests
    {
        private static void LiveJoinsAndTheLauncher(string root)
        {
            LiveJoinsAreCheckedAndActedOnOnce();
            TheIntakeLooksOffTheMainThread();
            TheStoreRefusesStaleForeignAndUnreadableJoins(root);
            HandoffsGoToTheLauncherThatOwnsThisGame();
            PreferencesReadAsTheLauncherAppliesThem(Program.FindRepoRoot());
            ALaunchAtRestAllocatesNothing();
        }

        private static void LiveJoinsAreCheckedAndActedOnOnce()
        {
            var rig = Owned();
            That(rig.Presenter.Intake!.IsListening, "a game the launcher started listens for live joins");
            Offer(rig, LiveJoin("join-wrong-mods", digest: "0123456789abcdef"));
            var refused = rig.Launcher.Outcomes.Single();
            That(refused.RequestId == "join-wrong-mods" && refused.Status == "failed" && refused.Multiplayer!.Failure == "admission-changed" &&
                 refused.Multiplayer.Detail == LaunchSessionPresenter.WrongMods && !rig.Control.Calls.Any(call => call.StartsWith("PlanLaunchSession", StringComparison.Ordinal)),
                "a join planned for other mods than this game runs is refused under its own id, and nothing is planned");
            Offer(rig, LiveJoin("join-other-service", service: "https://other.example.org"));
            That(rig.Launcher.Outcomes.Count == 2 && rig.Launcher.Outcomes[1].Error!.Message == LaunchSessionPresenter.OtherService,
                "a join through another TopiaForge service than this game reaches is refused");

            Offer(rig, LiveJoin("join-01"));
            That(rig.Control.Calls.Contains("PlanLaunchSession:join") && rig.Noticed(LaunchSessionPresenter.LiveJoinArrived) && rig.Control.Calls.Contains("StartBrowsing"),
                "a join that matches is planned by the provider and started, listening for its LAN session first");
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            rig.Frame();
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Frame(2);
            var joined = rig.Launcher.Outcomes.Last();
            That(rig.Control.Calls.Contains("Join:Lan:relay") && joined.RequestId == "join-01" && joined.Status == "succeeded" && joined.Phase == "idle" &&
                 rig.Launcher.Progress.Count > 0 && rig.Launcher.Progress.All(record => record.RequestId == "join-01"),
                "it joins relay-only as staged, and its records go out under its join id");

            var planned = rig.Control.Calls.Count(call => call == "PlanLaunchSession:join");
            Offer(rig, LiveJoin("join-01"));
            That(rig.Control.Calls.Count(call => call == "PlanLaunchSession:join") == planned && rig.Launcher.Outcomes.Count(outcome => outcome.RequestId == "join-01") == 1,
                "the same join staged again is never acted on twice");
            That(rig.Launcher.Threads.Count > 0, "the channels were used");
        }

        private static void TheIntakeLooksOffTheMainThread()
        {
            var channels = new MemoryLauncherChannels(OwnerRequest, OwnerDigest, OwnerService);
            using (var intake = new LiveJoinIntake(channels))
            {
                intake.Start();
                channels.Stage(LiveJoin("join-timer"));
                SessionJoinRequest? arrived = null;
                var found = SpinWait.SpinUntil(() => (arrived = intake.TakeArrived()) != null, TimeSpan.FromSeconds(10));
                int[] threads;
                lock (channels.Threads) threads = channels.Threads.ToArray();
                That(found && arrived!.JoinId == "join-timer" && threads.Length > 0 && threads.All(thread => thread != System.Environment.CurrentManagedThreadId),
                    "the intake looks for joins on a pool thread, and the frame takes one without waiting");
                That(intake.TakeArrived() == null, "a join is taken once");
            }

            using var unowned = new LiveJoinIntake(new MemoryLauncherChannels());
            unowned.Start();
            That(!unowned.IsListening, "a game no launcher started looks for no joins");
        }

        /// <summary>The game side of the channel: the staging store takes the file, and refuses one for another request or one it cannot read.</summary>
        private static void TheStoreRefusesStaleForeignAndUnreadableJoins(string root)
        {
            var paths = new ManagerPaths(Path.Combine(root, "live-joins"));
            paths.EnsureCreated();
            var store = new LaunchStagingStore(paths);
            var rig = new SessionsRig(channels: new StagingLauncherChannels(store, OwnerRequest, OwnerDigest, OwnerService));
            rig.Presenter.Intake!.Dispose();
            void Stage(string text)
            {
                File.WriteAllText(store.SessionJoinPath(OwnerRequest), text);
                rig.Presenter.Intake.Poll();
                rig.Frame(2);
            }

            // Left by an earlier run, whose request it names, or written for another game; and a file cut short.
            Stage(LaunchTransportJson.WriteSessionJoin(LiveJoin("join-stale", request: "0f1e2d3c4b5a69788796a5b4c3d2e1f0")));
            Stage("{\"schemaVersion\":1,\"joinId\":");
            That(!rig.Control.Calls.Any(call => call.StartsWith("PlanLaunchSession", StringComparison.Ordinal)) && !File.Exists(store.SessionJoinPath(OwnerRequest)) &&
                 !File.Exists(store.OutcomePath("join-stale")), "a join for another request, or one that cannot be read, is taken off the channel, never acted on or answered");
            Stage(LaunchTransportJson.WriteSessionJoin(LiveJoin("join-02")));
            That(rig.Control.Calls.Count(call => call == "PlanLaunchSession:join") == 1, "a join for this game is taken and planned");
            Stage(LaunchTransportJson.WriteSessionJoin(LiveJoin("join-02")));
            That(rig.Control.Calls.Count(call => call == "PlanLaunchSession:join") == 1 && !File.Exists(store.SessionJoinPath(OwnerRequest)),
                "and the same join written again is taken but never acted on twice");
        }

        private static void HandoffsGoToTheLauncherThatOwnsThisGame()
        {
            // A launched join the launcher can plan again: the handoff names the current session, never an address.
            var owned = Owned();
            var sink = Launch(owned, JoinSession(Typed("192.168.1.20")));
            owned.Frame();
            owned.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.AdmissionChanged);
            owned.Frame();
            var overlay = owned.Presenter.Connecting;
            That(overlay.CanOpenLauncher && overlay.FailureTitle == "Server no longer matches" && !overlay.CanRetry && sink.Outcome?.Multiplayer.Failure == "admission-changed",
                "a server that changed since the launcher checked it is handed back to the launcher");
            overlay.OpenLauncher();
            owned.Frame(2);
            var current = owned.Launcher.Handoffs.Single();
            That(current.Target.Kind == "current" && current.RequestId == OwnerRequest && owned.Environment.Opened.Count == 0 && owned.Noticed("Handed the join"),
                "the launcher that owns this game takes it as a handoff, and no link is opened");

            // A LAN session from Browse: owned, it goes as a handoff; with no launcher behind the game, as a link.
            var lan = OwnedBrowsing();
            FailOnMods(lan);
            lan.Presenter.Connecting.OpenLauncher();
            lan.Frame(2);
            var listing = lan.Launcher.Handoffs.Single();
            That(listing.Target.Kind == "listing" && listing.Target.Source == "lan" && listing.Target.ServerId == LanServer && lan.Environment.Opened.Count == 0,
                "a LAN session is handed over by its server id");
            var failing = OwnedBrowsing();
            failing.Launcher.FailHandoff = true;
            FailOnMods(failing);
            failing.Presenter.Connecting.OpenLauncher();
            failing.Frame(2);
            That(failing.Launcher.Handoffs.Count == 0 && failing.Environment.Opened.Single() == "topiaforge://join/server/" + LanServer,
                "a handoff that cannot be written opens the server's link instead");
            var lost = Owned();
            lost.Launcher.FailHandoff = true;
            Launch(lost, JoinSession(Typed("192.168.1.20")));
            lost.Frame();
            lost.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.AdmissionChanged);
            lost.Frame();
            lost.Presenter.Connecting.OpenLauncher();
            lost.Frame(2);
            That(lost.Environment.Opened.Count == 0 && lost.Noticed(LauncherHandoffs.NotDelivered), "the current session has no link, so the player is told to use the launcher");

            // An address the player typed goes only in its link, which the system opens, whoever owns the game.
            var typed = Owned();
            typed.Presenter.Connect.SetAddress("192.0.2.9:27250");
            typed.Presenter.Connect.JoinAddress();
            typed.Frame();
            typed.Control.Queries[0].Complete(InternalLiveFailure.None, Server('3', "Typed"));
            typed.Frame();
            typed.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.ContentMismatch);
            typed.Frame();
            typed.Presenter.Connecting.OpenLauncher();
            typed.Frame(2);
            That(typed.Launcher.Handoffs.Count == 0 && typed.Environment.Opened.Single().StartsWith("topiaforge://join/direct/", StringComparison.Ordinal),
                "a typed address is never written into a handoff");
        }

        private static void PreferencesReadAsTheLauncherAppliesThem(string repositoryRoot)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "tests", "fixtures", "multiplayer", "channels", "cases.json")));
            var cases = 0;
            foreach (var item in document.RootElement.GetProperty("journalApply").EnumerateArray())
            {
                cases++;
                var projection = LaunchTransportJson.ReadPreferences(item.GetProperty("projection").GetString()!);
                var journal = LaunchTransportJson.ReadPreferencesJournal(item.GetProperty("journal").GetString()!);
                var result = LaunchTransportJson.ReadPreferences(item.GetProperty("result").GetString()!);
                var values = MultiplayerPreferenceValues.From(projection, journal);
                That(values.DisplayName == result.DisplayName && values.HostingName == result.Hosting.Name && values.MaxPlayers == result.Hosting.MaxPlayers &&
                     MultiplayerPreferenceValues.VisibilityName(values.Visibility) == result.Hosting.Visibility && values.Voice == result.Hosting.Voice &&
                     values.Port == result.Hosting.Port, item.GetProperty("id").GetString() + ": this game reads the preferences the launcher's journal apply makes");
            }

            That(cases == 5, "every journal-apply case is read (" + cases + ")");
        }

        private static void ALaunchAtRestAllocatesNothing()
        {
            Settled("started by the launcher, with nothing launched", Owned());
            var hosting = Owned();
            var host = Launch(hosting, HostSession(password: false));
            hosting.Frame();
            hosting.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            hosting.Frame(3);
            That(host.Outcome?.Status == "succeeded" && hosting.Presenter.Launches.IsActive, "a launched host is followed while it hosts");
            Settled("following a launched host", hosting);
            var joined = Owned();
            var join = Launch(joined, JoinSession(Typed("192.168.1.20")));
            joined.Frame();
            joined.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            joined.Frame(3);
            That(join.Outcome?.Status == "succeeded" && joined.Presenter.Launches.IsActive, "a launched join is followed while it is in session");
            Settled("following a launched join", joined);
        }

        /// <summary>Stages <paramref name="join"/>, takes it as the intake's timer would, and runs the frames that act on it and write its records.</summary>
        private static void Offer(SessionsRig rig, SessionJoinRequest join)
        {
            ((MemoryLauncherChannels)rig.Environment.Launcher!).Stage(join);
            rig.Presenter.Intake!.Poll();
            rig.Frame(2);
        }

        /// <summary>A live join of a LAN listing, or of an Internet listing when it names a service, relay-only, as the launcher stages one.</summary>
        private static SessionJoinRequest LiveJoin(string joinId, string digest = OwnerDigest, string? service = null, string request = OwnerRequest)
        {
            var target = service == null
                ? LaunchSessionTarget.Listing("lan", LanServer, LaunchedDescriptor, LaunchedPackages)
                : LaunchSessionTarget.Listing("internet", LaunchedServer, LaunchedDescriptor, LaunchedPackages);
            var session = new LaunchSessionRequest("join", LaunchIntent, "Sam", new LaunchSessionTransport("relay-only", null, 0, false, true),
                serviceOrigin: service, target: target);
            return new SessionJoinRequest(request, joinId, digest, session);
        }

        /// <summary>A game the launcher started, browsing its LAN, with a session checked and ready to join.</summary>
        private static SessionsRig OwnedBrowsing()
        {
            var rig = Owned();
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            rig.Presenter.Browse.StartSearch();
            rig.Frame();
            rig.Control.Queries.Single().Complete(InternalLiveFailure.None, Server('1', "LAN game"));
            rig.Frame();
            return rig;
        }

        /// <summary>Joins the LAN session, which fails because this game's mods are not the host's.</summary>
        private static void FailOnMods(SessionsRig rig)
        {
            rig.Presenter.Browse.Select(LanServer);
            rig.Presenter.Browse.Join();
            rig.Frame();
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.ContentMismatch);
            rig.Frame();
            That(rig.Presenter.Connecting.CanOpenLauncher, "mods that differ hand over to the launcher");
        }
    }
}
