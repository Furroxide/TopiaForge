using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The game writes progress and outcomes V2 as tests/fixtures/multiplayer/progress-v2 defines them, byte for byte. Each
    /// case is a launched session following the provider's status into the runtime's publisher, which writes through the
    /// launch staging store, while the runtime's own records for the request are scripted under it. Over the real runtime,
    /// the publisher keeps one sequence for both sources, takes a success only from the provider, lets a failed command
    /// settle the launch, and writes the session's end as the process exits.
    /// </summary>
    internal static class SessionLaunchRecordsTests
    {
        private const string RequestId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
        private const string IntentId = "intent-7d3e1f20";
        private const string JoinServer = "c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3";
        private const string HostServer = "cf82f7bc6b072f456554dd30c31b12fe5af423bbe03ff908124a1a08f0f16443";
        private const string GameSession = "session-0a1b2c3d";

        private static readonly FieldInfo ProgressEvent = typeof(GamemodeSessionOrchestrator).GetField("Progress", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Assertion failed: the orchestrator's progress is a field-like event");

        internal static void Run(string repositoryRoot, string root)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "tests", "fixtures", "multiplayer", "progress-v2", "cases.json")));
            var expected = new Dictionary<string, (string Bytes, string File)>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.GetProperty("progress").GetProperty("valid").EnumerateArray())
                expected.Add(item.GetProperty("id").GetString()!, (item.GetProperty("bytes").GetString()!, "progress"));
            foreach (var item in document.RootElement.GetProperty("outcome").GetProperty("valid").EnumerateArray())
            {
                var bytes = item.GetProperty("bytes").GetString()!;
                using var outcome = JsonDocument.Parse(bytes);
                expected.Add(item.GetProperty("id").GetString()!, (bytes, outcome.RootElement.GetProperty("kind").GetString()!));
            }

            var cases = Cases();
            That(cases.Count == expected.Count && cases.Keys.All(expected.ContainsKey), "every valid fixture has its case (" + cases.Count + " of " + expected.Count + ")");
            using var fixture = new SessionFixture(Path.Combine(root, "runtime"));
            foreach (var (id, scenario) in cases)
            {
                var store = Store(Path.Combine(root, id));
                var errors = new List<Exception>();
                var identity = new LaunchSessionIdentity(RequestId, scenario.Role, IntentId);
                using var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, RequestId, errors.Add, session: identity);
                var launch = new LaunchedSession(identity, Plan(scenario.Role), publisher, () => LaunchedGame.Ready, hostPort: scenario.HostPort);
                scenario.Run(new Script(fixture.Hosted, launch));
                var (bytes, file) = expected[id];
                var path = file == "progress" ? store.ProgressPath(RequestId) : store.OutcomePath(RequestId, file == "session");
                var written = File.ReadAllText(path);
                That(written == bytes, id + " is written byte for byte:\n  wrote    " + written + "\n  fixture  " + bytes);
                That(errors.Count == 0, id + " is published without failures");
            }

            TheRuntimeAndTheProviderShareOneSequence(fixture, Path.Combine(root, "shared"));
            AFailedCommandIsTheLaunchsOutcome(fixture, Path.Combine(root, "failed"));
            AnUncleanExitEndsTheSessionAsFailed(fixture, Path.Combine(root, "unclean"));
            ALaunchedJoinsFollowWritesItsRecords(Path.Combine(root, "follow"));

            var noIPv6 = SessionLaunchRecords.Result(new LaunchSessionIdentity(RequestId, "join", IntentId),
                new InternalLiveStatus { JoinPhase = InternalJoinPhase.Ended, JoinFailure = InternalLiveFailure.NoIPv6 });
            That(noIPv6?.Status == "failed" && noIPv6.Error?.Code == "unavailable" &&
                 noIPv6.Error.Message == "This computer has no IPv6, so it cannot reach this address; ask the host for an IPv4 address or an invite.",
                "a launched join names the local IPv6 limit and an available next step");

            // A launched host whose ban list cannot be read opened nothing, for a cause on this computer that its record names.
            var unreadBans = SessionLaunchRecords.Result(new LaunchSessionIdentity(RequestId, "host", IntentId),
                new InternalLiveStatus { HostPhase = InternalHostPhase.Failed, HostFailure = InternalLiveFailure.BansUnreadable });
            That(unreadBans?.Status == "failed" && unreadBans.Error?.Code == "io" &&
                 unreadBans.Error.Message == "This game could not read its list of banned players, so hosting did not start; close any program that may be using it, then try again.",
                "a launched host whose ban list cannot be read says so, and what to do next (" + unreadBans?.Error?.Code + ": " + unreadBans?.Error?.Message + ")");

            // A refusal's words can be the provider's: an address in them never reaches the launcher.
            var refused = SessionLaunchRecords.Refused(new LaunchSessionIdentity(RequestId, "join", IntentId), ModErrorCode.External,
                "The relay at 203.0.113.9:3478 refused play.example.org.");
            That(refused.Error!.Message.Contains(MultiplayerDiagnosticRedactor.Replacement, StringComparison.Ordinal) &&
                 !refused.Error.Message.Contains("203.0.113.9", StringComparison.Ordinal) && !refused.Error.Message.Contains("example.org", StringComparison.Ordinal),
                "a refusal is redacted before it is written (" + refused.Error.Message + ")");
            Console.WriteLine("Session launch records: " + cases.Count + " fixtures written byte for byte; the publisher follows the real runtime.");
        }

        private static Dictionary<string, Scenario> Cases() => new Dictionary<string, Scenario>(StringComparer.Ordinal)
        {
            // Progress V2: the block a launched session reports, at the runtime's phase or the join's own.
            ["host-pending"] = Host(run => run.Runtime(0, "preparing")),
            ["join-connecting"] = Join(run =>
            {
                run.Runtime(2, "idle");
                run.Started();
                run.Status(s => { s.JoinPhase = InternalJoinPhase.Connecting; s.Server = Server(JoinServer); });
            }),
            ["join-awaiting-password"] = Join(run =>
            {
                run.Runtime(4, "idle");
                run.Started();
                run.Status(s => { s.JoinPhase = InternalJoinPhase.AwaitingPassword; s.Server = Server(JoinServer); });
            }),
            ["join-in-session"] = Join(run =>
            {
                run.Runtime(8, "running", GameSession);
                run.Started();
                run.Status(s => { s.JoinPhase = InternalJoinPhase.InSession; s.Server = Server(JoinServer, 8); s.ConnectedPlayers = 3; });
            }),
            ["host-hosting"] = Host(run =>
            {
                run.Runtime(11, "running", GameSession, nativeBusy: false);
                run.Started();
                run.Status(s => Hosting(s, players: 1, maximum: 16, invite: "ABCDE12345"));
            }),

            // Outcomes V2: the launch's one result, and its session's end.
            ["join-started"] = Join(run =>
            {
                run.Runtime(8, "running", GameSession);
                run.Started();
                run.Status(s => { s.JoinPhase = InternalJoinPhase.InSession; s.Server = Server(JoinServer); });
            }),
            ["join-password-rejected"] = Join(run =>
            {
                run.Runtime(7, "idle");
                run.Started();
                run.Status(s => { s.JoinPhase = InternalJoinPhase.AwaitingPassword; s.Server = Server(JoinServer); });
                run.Status(s => Rejected(s, InternalAdmissionCode.PasswordWrong, JoinServer));
            }),
            ["join-admission-changed"] = Join(run =>
            {
                run.Runtime(7, "idle");
                run.Started();
                run.Status(s => { s.JoinPhase = InternalJoinPhase.Connecting; s.Server = Server(JoinServer); });
                run.Status(s =>
                {
                    Rejected(s, InternalAdmissionCode.Content, JoinServer);
                    s.ContentProblems = new[] { "Zombies 0.2.0 is missing.", "Sandbox 0.2.0 differs." };
                    s.ContentProblemsFromHost = true;
                });
            }),
            ["host-port-in-use"] = Host(run =>
            {
                run.Runtime(7, "idle");
                run.Started();
                run.Status(s => s.HostPhase = InternalHostPhase.Preparing);
                run.Status(s => { s.HostPhase = InternalHostPhase.Failed; s.HostFailure = InternalLiveFailure.PortInUse; });
            }, hostPort: 27250),
            ["host-started-at-menu"] = Host(run =>
            {
                run.Runtime(8, "idle");
                run.Started();
                run.Status(s => Hosting(s, players: 0, maximum: 0, invite: null));
            }),
            ["join-session-ended"] = Join(run =>
            {
                run.Runtime(8, "running", GameSession);
                run.Started();
                run.Status(s => { s.JoinPhase = InternalJoinPhase.InSession; s.Server = Server(JoinServer); });
                run.Runtime(38, "running", GameSession);
                run.Status(s => { s.JoinPhase = InternalJoinPhase.Ended; s.JoinFailure = InternalLiveFailure.HostEnded; s.Server = Server(JoinServer); });
            }),
            ["join-session-rejected"] = Join(run =>
            {
                run.Runtime(8, "running", GameSession);
                run.Started();
                run.Status(s => { s.JoinPhase = InternalJoinPhase.InSession; s.Server = Server(JoinServer); });
                run.Runtime(39, "idle");
                run.Status(s => Rejected(s, InternalAdmissionCode.Full, null));
            }),
            ["join-cancelled"] = Join(run =>
            {
                run.Runtime(4, "idle");
                run.Started();
                run.Status(s => { s.JoinPhase = InternalJoinPhase.Synchronizing; s.Server = Server(JoinServer); });
                run.Status(s => { s.JoinPhase = InternalJoinPhase.Ended; s.JoinFailure = InternalLiveFailure.Cancelled; });
            }),
            ["host-error"] = Host(run =>
            {
                run.Runtime(1, "preparing");
                run.Launch.Refuse(ModErrorCode.Unavailable, SessionLaunch.ProviderMissing);
            })
        };

        /// <summary>
        /// A host's launch over the real runtime: the runtime runs the launch target, the provider opens hosting, and the process
        /// exits with the session open. Every record shares one rising sequence, the runtime's success is not the launch's, and
        /// the session's end is written as the process goes.
        /// </summary>
        private static void TheRuntimeAndTheProviderShareOneSequence(SessionFixture fixture, string root)
        {
            var store = Store(root);
            var errors = new List<Exception>();
            var identity = new LaunchSessionIdentity("owned-host", "host", IntentId);
            var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, "owned-host", errors.Add, session: identity);
            var launch = new LaunchedSession(identity, Plan("host"), publisher, () => Game(publisher.CommandState), startsGame: true);
            var sequences = new List<int>();
            void Read() => sequences.Add(LaunchTransportJson.ReadProgress(File.ReadAllText(store.ProgressPath("owned-host"))).Sequence);

            That(launch.Game == LaunchedGame.Starting, "the launch waits while the runtime starts its target");
            var started = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "owned-host");
            fixture.Wait(started);
            Read();
            var running = LaunchTransportJson.ReadProgress(File.ReadAllText(store.ProgressPath("owned-host")));
            That(started.Result.Succeeded && launch.Game == LaunchedGame.Ready && running.Phase == "running" && running.Session!.State == "pending",
                "once the target runs the launch may open, its session still pending");
            That(!File.Exists(store.OutcomePath("owned-host")), "the runtime's success is not the host's");

            launch.Started(0);
            var status = new InternalLiveStatus { IsLiveAvailable = true, Role = InternalSessionRole.ListenHost, HostPhase = InternalHostPhase.Preparing };
            launch.Follow(status, 1, 1);
            Read();
            Hosting(status, players: 1, maximum: 8, invite: null);
            launch.Follow(status, 2, 2);
            Read();
            var outcome = LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath("owned-host")));
            That(outcome.Status == "succeeded" && outcome.Phase == "running" && outcome.SessionId == running.SessionId && outcome.Multiplayer!.ServerId == HostServer &&
                 outcome.Sequence > sequences[sequences.Count - 1], "the provider's hosting is the launch's success, after every progress record");
            launch.Follow(status, 3, 3);
            That(!launch.IsFinished && !File.Exists(store.OutcomePath("owned-host", true)), "a session still open has not ended");

            publisher.Dispose(clean: true);
            var end = LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath("owned-host", true)));
            That(end.Status == "succeeded" && end.Multiplayer!.EndReason == "host-ended" && end.Sequence > outcome.Sequence && end.Error == null,
                "the process exits with the session open: it ends as the host ending it");
            That(sequences.Zip(sequences.Skip(1), (left, right) => right > left).All(rises => rises), "the records' sequence rises (" + string.Join(", ", sequences) + ")");
            That(errors.Count == 0, "the host's records are published without failures");
        }

        private static void AFailedCommandIsTheLaunchsOutcome(SessionFixture fixture, string root)
        {
            var store = Store(root);
            var errors = new List<Exception>();
            var identity = new LaunchSessionIdentity("failed-join", "join", IntentId);
            using var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, "failed-join", errors.Add, session: identity);
            var launch = new LaunchedSession(identity, Plan("join"), publisher, () => Game(publisher.CommandState));
            fixture.Wait(fixture.Hosted.RejectCommandAsync("failed-join", "main-menu", ModErrorCode.InvalidArgument, "The launcher request was rejected."));
            var written = File.ReadAllText(store.OutcomePath("failed-join"));
            That(launch.Game == LaunchedGame.Failed && LaunchTransportJson.ReadOutcome(written).Error!.Code == "invalidArgument", "the runtime's failure is the launch's");
            launch.Follow(new InternalLiveStatus { JoinPhase = InternalJoinPhase.InSession, Server = Server(JoinServer) }, 1, 1);
            That(launch.IsSettled && launch.IsFinished && File.ReadAllText(store.OutcomePath("failed-join")) == written,
                "and the launch settles on it without a second result");
            That(errors.Count == 0, "the failure is published without failures");
        }

        private static void AnUncleanExitEndsTheSessionAsFailed(SessionFixture fixture, string root)
        {
            var store = Store(root);
            var errors = new List<Exception>();
            var identity = new LaunchSessionIdentity("unclean-join", "join", IntentId);
            var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, "unclean-join", errors.Add, session: identity);
            var launch = new LaunchedSession(identity, Plan("join"), publisher, () => LaunchedGame.Ready);
            launch.Started(0);
            launch.Follow(new InternalLiveStatus { JoinPhase = InternalJoinPhase.InSession, Server = Server(JoinServer) }, 1, 1);
            publisher.Dispose(clean: false);
            var end = LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath("unclean-join", true)));
            That(end.Status == "failed" && end.Error!.Code == "external" && end.Multiplayer!.EndReason == "left" && end.Multiplayer.ServerId == JoinServer,
                "a runtime that did not stop cleanly ends the session as failed, the player having left it");
            That(errors.Count == 0, "the end is published without failures");
        }

        /// <summary>
        /// A launched join's records over the real orchestrator, with session follow (WS3.8) running the host's world under the
        /// join's request: while the join synchronizes its own phase stands; in session, the launch succeeds at Running with the
        /// host's world session; the host's next world moves the phase on, and the end of the world before is not the join's;
        /// the provider's end of the join is its terminal record. A later join started in game writes nothing more.
        /// </summary>
        private static void ALaunchedJoinsFollowWritesItsRecords(string root)
        {
            const string request = "launched-join";
            using var test = GamemodeParticipantRealmTests.InRealm(Path.Combine(root, "runtime"), NetRealm.Client);
            var store = Store(Path.Combine(root, "staging"));
            var errors = new List<Exception>();
            var identity = new LaunchSessionIdentity(request, "join", IntentId);
            using var publisher = new RuntimeLaunchPublisher(test.Hosted, store, request, errors.Add, session: identity);
            var launch = new LaunchedSession(identity, Plan("join"), publisher, () => LaunchedGame.Ready);
            LaunchProgress Progress() => LaunchTransportJson.ReadProgress(File.ReadAllText(store.ProgressPath(request)));
            SessionFollowResult Follow(string session) => SessionFollowTests.Follow(test, SessionFollowTests.Plan(test, session, launchRequestId: request));

            launch.Started(0);
            launch.Follow(new InternalLiveStatus { IsLiveAvailable = true, JoinPhase = InternalJoinPhase.Synchronizing, Server = Server(JoinServer) }, 1, 1);
            var host = SessionFollowTests.NewSessionId();
            That(Follow(host).Succeeded, "the launched join follows the host's world");
            var synchronizing = Progress();
            That(synchronizing.Phase == "synchronizing" && synchronizing.SessionId == null && synchronizing.Session!.State == "synchronizing",
                "while the join synchronizes, its own phase stands over the follow's (" + synchronizing.Phase + ")");

            launch.Follow(new InternalLiveStatus { IsLiveAvailable = true, JoinPhase = InternalJoinPhase.InSession, Server = Server(JoinServer) }, 2, 2);
            var started = LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath(request)));
            var inSession = Progress();
            That(started.Status == "succeeded" && started.Phase == "running" && started.SessionId == host,
                "in session in the host's world, the launch succeeds at Running with the host's world session (" + started.Phase + ", " + started.SessionId + ")");
            That(inSession.Phase == "running" && inSession.SessionId == host && inSession.Session!.State == "in-session", "and its progress says so");

            var next = SessionFollowTests.NewSessionId();
            That(Follow(next).Succeeded, "the host's next world is followed");
            var moved = Progress();
            That(moved.Phase == "running" && moved.SessionId == next && moved.Session!.State == "in-session" && moved.Sequence > inSession.Sequence,
                "its phases move on under the join's block, to the host's next world");
            That(!File.Exists(store.OutcomePath(request, true)), "the end of the world before is not the join's end");

            launch.Follow(new InternalLiveStatus { IsLiveAvailable = true, JoinPhase = InternalJoinPhase.Ended, JoinFailure = InternalLiveFailure.HostEnded,
                Server = Server(JoinServer) }, 3, 3);
            var end = LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath(request, true)));
            That(end.Multiplayer!.EndReason == "host-ended" && end.SessionId == next && end.Status == "succeeded",
                "the provider's end of the join is its terminal record, naming the host's world it was in");

            // The game returns to the main menu as the follower takes it there once the join has ended, which ends the host's world
            // here; then a join started in game, whose follow writes nothing to the launched join's records.
            var menu = test.Hosted.FollowMainMenuAsync();
            test.Wait(menu);
            test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
            var written = File.ReadAllText(store.ProgressPath(request));
            That(SessionFollowTests.Follow(test, SessionFollowTests.Plan(test, SessionFollowTests.NewSessionId())).Succeeded, "an in-game join's follow runs");
            That(File.ReadAllText(store.ProgressPath(request)) == written, "and the launched join's records stay as they were");
            That(errors.Count == 0, "the launched join's records are published without failures");
        }

        private static Scenario Host(Action<Script> run, int hostPort = 0) => new Scenario("host", run, hostPort);

        private static Scenario Join(Action<Script> run) => new Scenario("join", run, 0);

        private static IInternalLaunchPlan Plan(string role) => new ScriptedLaunchPlan(role == "host"
            ? new InternalLaunchSession(IntentId, "Sam", false, new InternalLaunchHost("Sam's Sandbox", 8, InternalSessionVisibility.Lan, false, false))
            : new InternalLaunchSession(IntentId, "Sam", false, target: InternalLaunchTarget.Listing(JoinServer, new string('d', 64), lan: true)));

        private static LaunchedGame Game(LaunchCommandState state) =>
            state == LaunchCommandState.Succeeded ? LaunchedGame.Ready : state == LaunchCommandState.Failed ? LaunchedGame.Failed : LaunchedGame.Starting;

        private static LaunchStagingStore Store(string root)
        {
            var paths = new ManagerPaths(root);
            paths.EnsureCreated();
            return new LaunchStagingStore(paths);
        }

        private static InternalServerInfo Server(string serverId, int maximum = 0) =>
            new InternalServerInfo(serverId, "Sam's Sandbox", Array.Empty<InternalHostPackage>()) { MaxPlayers = maximum };

        private static void Hosting(InternalLiveStatus status, int players, int maximum, string? invite)
        {
            status.Role = InternalSessionRole.ListenHost;
            status.HostPhase = InternalHostPhase.Hosting;
            status.HostServerId = HostServer;
            status.ConnectedPlayers = players;
            status.MaxPlayers = maximum;
            if (invite != null) status.Internet.Invite = new InternalInvite(invite, invite.Substring(0, 5) + "-" + invite.Substring(5), "topiaforge://join/" + invite, 1_800_000_000);
        }

        private static void Rejected(InternalLiveStatus status, InternalAdmissionCode code, string? server)
        {
            status.JoinPhase = InternalJoinPhase.Ended;
            status.JoinFailure = InternalLiveFailure.Rejected;
            status.RejectionCode = code;
            status.Server = server == null ? null : Server(server);
        }

        private static void That(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }

        private sealed class Scenario
        {
            internal Scenario(string role, Action<Script> run, int hostPort)
            {
                Role = role;
                Run = run;
                HostPort = hostPort;
            }

            internal string Role { get; }

            internal Action<Script> Run { get; }

            internal int HostPort { get; }
        }

        /// <summary>One case's run: the runtime's own records for the request, and the provider's status as the launch follows it.</summary>
        private sealed class Script
        {
            private readonly GamemodeSessionOrchestrator sessions;
            private int version;
            private int reads;

            internal Script(GamemodeSessionOrchestrator sessions, LaunchedSession launch)
            {
                this.sessions = sessions;
                Launch = launch;
            }

            internal LaunchedSession Launch { get; }

            /// <summary>The runtime's progress for the request, as its command reports it.</summary>
            internal void Runtime(int sequence, string phase, string? sessionId = null, bool? nativeBusy = null) =>
                ((Action<LaunchProgress>?)ProgressEvent.GetValue(sessions))?.Invoke(new LaunchProgress(RequestId, sequence, phase, sessionId, nativeBusy));

            /// <summary>The session begins, as the screens start it, after the status reads so far.</summary>
            internal void Started() => Launch.Started(reads);

            /// <summary>The provider's status moves in a new read, and the launch follows it.</summary>
            internal void Status(Action<InternalLiveStatus> change)
            {
                var status = new InternalLiveStatus { IsLiveAvailable = true };
                change(status);
                Launch.Follow(status, ++version, ++reads);
            }
        }
    }
}
