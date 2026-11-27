using System;
using System.Collections.Generic;
using System.IO;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A V5 request's runtime records reach the launcher as progress and outcomes V2: its progress with the session still
    /// pending, a failure before the session started as the session launch's outcome, and nothing that would claim the
    /// session started. Every other launch keeps publishing version 1.
    /// </summary>
    internal static class RuntimeSessionPublicationTests
    {
        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            foreach (var (name, test) in new (string, Action<string>)[]
                { ("session-launch", SessionLaunch), ("rejected-session", RejectedSession), ("recovery-session", RecoverySession) })
            {
                try { test(Path.Combine(root, name)); Console.WriteLine("Runtime session publication " + name + ": PASS"); }
                catch (Exception error) { failures.Add(error); Console.WriteLine("Runtime session publication " + name + ": FAIL " + error.Message); }
            }

            if (failures.Count != 0) throw new AggregateException("Runtime session publication regressions failed.", failures);
        }

        private static void SessionLaunch(string root)
        {
            using var fixture = new SessionFixture(root);
            var store = new LaunchStagingStore(new ManagerPaths(root));
            var errors = new List<Exception>();
            var identity = new LaunchSessionIdentity("owned-request", "host", "intent-1");
            using var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, "owned-request", errors.Add, session: identity);
            var launch = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "owned-request");
            fixture.Wait(launch);
            Assert(launch.Result.Succeeded, "The host's launch target runs.");
            var progress = LaunchTransportJson.ReadProgress(File.ReadAllText(store.ProgressPath("owned-request")));
            Assert(progress.Version == 2 && progress.Session!.Role == "host" && progress.Session.State == "pending" && progress.Phase == "running",
                "A session launch's progress is version 2, its session pending until the provider reports it.");
            Assert(!File.Exists(store.OutcomePath("owned-request")), "A running target is not yet a host's success.");
            fixture.Wait(fixture.Hosted.StopAsync(fixture.Hosted.Current.Identity!.SessionId));
            fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
            Assert(!File.Exists(store.OutcomePath("owned-request", true)), "A gamemode session's end is not the live session's end.");
            Assert(errors.Count == 0, "Publication completes without IO failures.");
            Rejects(() => new RuntimeLaunchPublisher(fixture.Hosted, store, "other-request", errors.Add, session: identity),
                "A session launch publishes under its own request.");
        }

        private static void RejectedSession(string root)
        {
            using var fixture = new SessionFixture(root);
            var store = new LaunchStagingStore(new ManagerPaths(root));
            var errors = new List<Exception>();
            var identity = new LaunchSessionIdentity("rejected-request", "join", "intent-2");
            using var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, "rejected-request", errors.Add, session: identity);
            fixture.Wait(fixture.Hosted.RejectCommandAsync("rejected-request", "main-menu", ModErrorCode.InvalidArgument, "The launcher request was rejected."));
            var outcome = LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath("rejected-request")));
            Assert(outcome.Version == 2 && outcome.Command == "join-session" && outcome.Status == "failed" && outcome.Error!.Code == "invalidArgument"
                && outcome.Multiplayer!.IntentId == "intent-2" && outcome.Multiplayer.Failure == null,
                "A rejected session launch is reported as itself, with the runtime's error.");
            Assert(errors.Count == 0, "Publication completes without IO failures.");
        }

        private static void RecoverySession(string root)
        {
            using var fixture = new SessionFixture(root);
            var store = new LaunchStagingStore(new ManagerPaths(root));
            var errors = new List<Exception>();
            var identity = new LaunchSessionIdentity("recovered-request", "host", "intent-3");
            const string reason = "The game started in recovery after a crash, so it did not host the session.";
            using var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, "recovered-request", errors.Add, session: identity,
                sessionRefusal: reason);
            var outcome = LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath("recovered-request")));
            Assert(outcome.Version == 2 && outcome.Command == "host-session" && outcome.Status == "failed" && outcome.Phase == "idle"
                && outcome.Error!.Code == "unavailable" && outcome.Error.Message == reason && outcome.Multiplayer!.IntentId == "intent-3",
                "A session launch the process will not start fails at once instead of staying unconfirmed.");
            var written = File.ReadAllText(store.OutcomePath("recovered-request"));
            var launch = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "recovered-request");
            fixture.Wait(launch);
            Assert(File.ReadAllText(store.OutcomePath("recovered-request")) == written, "The refusal is the launch's one outcome.");
            Assert(errors.Count == 0, "Publication completes without IO failures.");
            Rejects(() => new RuntimeLaunchPublisher(fixture.Hosted, store, "plain-request", errors.Add, sessionRefusal: reason),
                "Only a session launch is refused as one.");
        }

        private static void Rejects(Action operation, string message)
        {
            try { operation(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
