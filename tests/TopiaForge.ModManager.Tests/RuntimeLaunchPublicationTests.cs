using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class RuntimeLaunchPublicationTests
    {
        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            foreach (var mode in new[] { "launch-terminal-separation", "menu-native-progress", "failed-owner-observation", "publication-failure", "observation-retry", "record-retry", "dispose-retry" })
            {
                try { Check(root + "/" + mode, mode); Console.WriteLine("Runtime launch publication " + mode + ": PASS"); }
                catch (Exception error) { failures.Add(error); Console.WriteLine("Runtime launch publication " + mode + ": FAIL " + error.Message); }
            }
            if (failures.Count != 0) throw new AggregateException("Runtime publication regressions failed.", failures);
        }
        private static void Check(string root, string mode)
        {
            if (mode == "record-retry" || mode == "dispose-retry") { RetryRecords(root, mode == "dispose-retry"); return; }
            using var fixture = new SessionFixture(root);
            var observationWrites = 0;
            var store = mode == "observation-retry" ? new LaunchStagingStore(new ManagerPaths(root), () =>
            {
                if (++observationWrites == 1) throw new IOException("Transient observation write failure.");
            }) : new LaunchStagingStore(new ManagerPaths(root));
            var errors = new List<Exception>();
            long observationClock = 0;
            using var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, "owned-request", errors.Add, () => observationClock);
            if (mode == "observation-retry")
            {
                var registry = new RuntimeBindingRegistry(fixture.Profile);
                var identity = fixture.Profile.Packages[0].Identity;
                registry.CommitFailed(registry.BeginPackageLoad(identity), "Provider unavailable.");
                var snapshot = registry.Capture();
                var observation = registry.CaptureObservations()[0];
                publisher.PublishObservations(registry);
                Assert(errors.Count == 1 && !File.Exists(store.ObservationPath(observation)), "A transient observation failure must remain unconfirmed.");
                observationClock = 250;
                publisher.PublishObservations(registry);
                Assert(ReferenceEquals(snapshot, registry.Capture()) && File.Exists(store.ObservationPath(observation)),
                    "The production publisher must retry failed observation writes without requiring a registry mutation.");
                publisher.PublishObservations(registry);
                Assert(observationWrites == 2, "A successfully published unchanged snapshot must be idempotent.");
                registry.CompleteOwnerRemoval(identity);
                publisher.PublishObservations(registry);
                var removed = LaunchTransportJson.ReadObservation(File.ReadAllText(store.ObservationPath(observation)));
                Assert(observationWrites == 3 && removed.ObservationRevision > observation.ObservationRevision,
                    "Owner removal must still publish after a successful retry.");
                return;
            }
            if (mode == "publication-failure")
            {
                Directory.CreateDirectory(store.OutcomePath("owned-request"));
                var launch = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "owned-request"); fixture.Wait(launch);
                Assert(launch.Result.Succeeded && fixture.Hosted.Current.Phase == SessionPhase.Running && errors.Count != 0,
                    "A failed acknowledgement write must be reported without changing the committed Running outcome.");
                Assert(!File.Exists(store.OutcomePath("owned-request")), "Failed IO cannot manufacture a confirmed launch acknowledgement.");
                fixture.Wait(fixture.Hosted.StopAsync(fixture.Hosted.Current.Identity!.SessionId));
                fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
                Assert(File.Exists(store.OutcomePath("owned-request", true)), "Independent terminal publication and complete cleanup survive launch acknowledgement IO failure.");
                return;
            }
            if (mode == "failed-owner-observation")
            {
                var registry = new RuntimeBindingRegistry(fixture.Profile);
                var identity = fixture.Profile.Packages[0].Identity;
                var attempt = registry.BeginPackageLoad(identity);
                registry.CommitFailed(attempt, "Constructor failed.");
                var observations = registry.CaptureObservations();
                Assert(observations.Count == 1 && observations[0].Producer.Equals(identity) && observations[0].Availability.Count >= 2,
                    "Failed selected packages must publish their exact producer and per-declaration failures.");
                publisher.PublishObservations(observations);
                var saved = LaunchTransportJson.ReadObservation(File.ReadAllText(store.ObservationPath(observations[0])));
                Assert(saved.PackageSetDigest == fixture.Plan.Digest && saved.ProfileRevision == fixture.Profile.Revision,
                    "Published observations must remain bound to the exact profile/package snapshot.");
                registry.CompleteOwnerRemoval(identity);
                var removed = registry.CaptureObservations()[0];
                publisher.PublishObservations(new[] { removed });
                Assert(removed.ObservationRevision > saved.ObservationRevision && removed.DiscoveredWorlds.Count == 0,
                    "Owner removal must supersede prior discovered instances and availability.");
            }
            else if (mode == "menu-native-progress")
            {
                var pending = new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Menu = _ => pending.Task;
                var operation = fixture.Hosted.ReturnToMainMenuAsync(requestedId: "owned-request");
                fixture.Until(() => fixture.MenuLoads == 1);
                try
                {
                    Assert(File.Exists(store.ProgressPath("owned-request")), "A pending menu transition must publish correlated native progress.");
                    var progress = LaunchTransportJson.ReadProgress(File.ReadAllText(store.ProgressPath("owned-request")));
                    Assert(progress.NativeBusy == true && !File.Exists(store.OutcomePath("owned-request")), "Native work is progress, not a successful session acknowledgement.");
                }
                finally { pending.TrySetResult(OperationResult<bool>.Success(true)); fixture.Wait(operation); }
                Assert(LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath("owned-request"))).Command == "main-menu", "Menu completion must retain the original command.");
            }
            else
            {
                var operation = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "owned-request"); fixture.Wait(operation);
                Assert(File.Exists(store.OutcomePath("owned-request")), "Running must publish the owned launch acknowledgement.");
                var initial = File.ReadAllText(store.OutcomePath("owned-request"));
                Assert(LaunchTransportJson.ReadOutcome(initial).Phase == "running", "Launch success requires Running.");
                fixture.Wait(fixture.Hosted.StopAsync(fixture.Hosted.Current.Identity!.SessionId));
                fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
                Assert(File.Exists(store.OutcomePath("owned-request", true)) && File.ReadAllText(store.OutcomePath("owned-request")) == initial,
                    "Terminal cleanup needs its own file and must not replace the launch acknowledgement.");
                fixture.Wait(fixture.Hosted.ReturnToMainMenuAsync(requestedId: "foreign-request"));
                Assert(!File.Exists(store.OutcomePath("foreign-request")), "The publisher cannot produce files for an unrelated request.");
            }
            Assert(errors.Count == 0, "Publication should complete without hidden IO failures.");
        }
        private static void RetryRecords(string root, bool onDispose)
        {
            using var fixture = new SessionFixture(root);
            var blocked = false;
            var writes = 0;
            long now = 0;
            var store = new LaunchStagingStore(new ManagerPaths(root), () =>
            {
                writes++;
                if (blocked) throw new IOException("Transient launch evidence failure.");
            });
            var errors = new List<Exception>();
            using var publisher = new RuntimeLaunchPublisher(fixture.Hosted, store, "owned-request", errors.Add, () => now);
            var registry = new RuntimeBindingRegistry(fixture.Profile);
            publisher.PublishObservations(registry);
            blocked = true;
            var notifications = 0;
            fixture.Hosted.Progress += _ => notifications++;
            fixture.Hosted.Outcome += _ => notifications++;
            var launch = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "owned-request"); fixture.Wait(launch);
            fixture.Wait(fixture.Hosted.StopAsync(fixture.Hosted.Current.Identity!.SessionId));
            fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
            Assert(launch.Result.Succeeded && errors.Count != 0 && !File.Exists(store.OutcomePath("owned-request")),
                "IO failures must not change committed lifecycle success or fabricate acknowledgement.");
            var beforeRetry = writes;
            var emitted = notifications;
            publisher.PublishObservations(registry);
            now = 100; publisher.PublishObservations(registry);
            Assert(writes == beforeRetry, "Retry attempts must be bounded while transient IO remains unavailable.");
            blocked = false;
            if (onDispose) publisher.Dispose();
            else { now = 250; publisher.PublishObservations(registry); }
            Assert(File.Exists(store.ProgressPath("owned-request")) && File.Exists(store.OutcomePath("owned-request"))
                && File.Exists(store.OutcomePath("owned-request", true)),
                "Latest progress, launch acknowledgement, and terminal outcome must survive transient writes without fresh lifecycle events.");
            Assert(LaunchTransportJson.ReadProgress(File.ReadAllText(store.ProgressPath("owned-request"))).Phase == "idle",
                "Only the latest pending progress is retained.");
            Assert(LaunchTransportJson.ReadOutcome(File.ReadAllText(store.OutcomePath("owned-request"))).Phase == "running",
                "The immutable launch acknowledgement retains Running after terminal cleanup.");
            var persisted = writes;
            now = 1000; publisher.PublishObservations(registry); publisher.Dispose();
            Assert(writes == persisted && notifications == emitted, "Persistence retries cannot re-emit events or rewrite successful outcome records.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
