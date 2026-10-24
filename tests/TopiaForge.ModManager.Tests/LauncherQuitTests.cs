using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A restart asks the game the launcher started to quit through manager staging, so the game's own quit records its
    /// clean exit. Only the process launched as the request's id takes it; a foreign or malformed request is removed unread;
    /// a request written during a take waits at its name; stale claims are swept and live ones kept. The plugin's watcher
    /// looks once an interval on the plugin's quit clock, quits once, and keeps looking after a failed look.
    /// </summary>
    internal static class LauncherQuitTests
    {
        internal static void Run()
        {
            var failures = new List<Exception>();
            var runs = 0;
            foreach (var test in new (string Name, Action<ManagerPaths> Run)[]
            {
                ("taken-once", TakenOnce), ("foreign-removed", ForeignRemoved), ("malformed-removed", MalformedRemoved),
                ("written-during-take", WrittenDuringTake), ("claims-swept", ClaimsSwept),
            })
            {
                runs++;
                var owned = Directory.CreateTempSubdirectory("TopiaForgeLauncherQuit-");
                try
                {
                    var paths = new ManagerPaths(owned.CreateSubdirectory("game").CreateSubdirectory("BepInEx").FullName);
                    paths.EnsureCreated();
                    test.Run(paths);
                }
                catch (Exception error) { failures.Add(new InvalidOperationException(test.Name, error)); }
                finally { owned.Delete(true); }
            }

            foreach (var test in new (string Name, Action Run)[]
            {
                ("watcher-interval", WatcherLooksOnceAnInterval), ("watcher-once", WatcherQuitsOnce),
                ("watcher-foreign", WatcherKeepsLookingPastAForeignRequest), ("watcher-failure", WatcherSurvivesAFailedLook),
                ("watcher-arguments", WatcherRejectsInvalidArguments),
                ("watcher-clock", () => PluginQuitCoordinatorTests.ReadsTheQuitClock("new LauncherQuitWatcher(")),
            })
            {
                runs++;
                try { test.Run(); }
                catch (Exception error) { failures.Add(new InvalidOperationException(test.Name, error)); }
            }

            if (failures.Count != 0) throw new AggregateException("Launcher quit regressions failed.", failures);
            Console.WriteLine("Launcher quit requests: " + runs + " behavioral checks passed.");
        }

        private static string Request(string requestId, string quitId) =>
            LaunchTransportJson.WriteLauncherQuit(new LauncherQuitRequest(requestId, quitId));

        private static bool AnyClaim(ManagerPaths paths) =>
            Directory.EnumerateFiles(paths.Staging).Any(file => Path.GetFileName(file).Contains(".taking-", StringComparison.Ordinal));

        private static void TakenOnce(ManagerPaths paths)
        {
            var store = new LaunchStagingStore(paths);
            var path = store.LauncherQuitPath("process-1");
            File.WriteAllText(path, Request("process-1", "quit-1"));
            Assert(store.TakeLauncherQuit("process-1")!.QuitId == "quit-1", "The process takes the request for its own launch.");
            Assert(!File.Exists(path) && !AnyClaim(paths), "A taken request leaves nothing behind.");
            Assert(store.TakeLauncherQuit("process-1") == null, "A request is taken once.");
        }

        private static void ForeignRemoved(ManagerPaths paths)
        {
            var store = new LaunchStagingStore(paths);
            var ours = store.LauncherQuitPath("process-1");
            var theirs = store.LauncherQuitPath("process-2");
            // A request naming another process, at this process's name, is removed without being acted on.
            File.WriteAllText(ours, Request("process-2", "quit-1"));
            File.WriteAllText(theirs, Request("process-2", "quit-2"));
            Assert(store.TakeLauncherQuit("process-1") == null, "A request for another process is never acted on.");
            Assert(!File.Exists(ours), "A foreign request at this process's name is removed.");
            Assert(File.Exists(theirs), "Another process's own request is never touched.");
            Assert(store.TakeLauncherQuit("process-2")!.QuitId == "quit-2", "The other process takes its own request.");
        }

        private static void MalformedRemoved(ManagerPaths paths)
        {
            var store = new LaunchStagingStore(paths);
            var path = store.LauncherQuitPath("process-1");
            foreach (var text in new[]
            {
                "{", "{\"quitId\":\"quit-1\",\"requestId\":\"process-1\",\"schemaVersion\":2}",
                "{\"quitId\":\"quit-1\",\"reason\":\"now\",\"requestId\":\"process-1\",\"schemaVersion\":1}",
                "{\"quitId\":\"../quit\",\"requestId\":\"process-1\",\"schemaVersion\":1}",
            })
            {
                File.WriteAllText(path, text);
                Assert(store.TakeLauncherQuit("process-1") == null, "A malformed request is never acted on: " + text);
                Assert(!File.Exists(path) && !AnyClaim(paths), "A malformed request is removed: " + text);
            }
        }

        private static void WrittenDuringTake(ManagerPaths paths)
        {
            var plain = new LaunchStagingStore(paths);
            var path = plain.LauncherQuitPath("process-1");
            File.WriteAllText(path, Request("process-1", "quit-1"));
            // The launcher stages a newer request between the claim and the read.
            var racing = new LaunchStagingStore(paths, null, () => File.WriteAllText(path, Request("process-1", "quit-2")), null, null);
            Assert(racing.TakeLauncherQuit("process-1")!.QuitId == "quit-1" && File.Exists(path),
                "A request staged during a take waits at its name instead of being removed unread.");
            Assert(plain.TakeLauncherQuit("process-1")!.QuitId == "quit-2" && !File.Exists(path), "The newer request is taken next.");
        }

        private static void ClaimsSwept(ManagerPaths paths)
        {
            var store = new LaunchStagingStore(paths);
            var key = LaunchStorageKeys.Request("process-1");
            string Claim(DateTimeOffset at) => Path.Combine(paths.Staging, "launcher-quit-" + key + ".json.taking-"
                + at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            var stale = Claim(DateTimeOffset.UtcNow.AddHours(-2));
            var live = Claim(DateTimeOffset.UtcNow);
            var other = Path.Combine(paths.Staging, "launcher-quit-" + key + ".json.taking-later");
            foreach (var file in new[] { stale, live, other }) File.WriteAllText(file, "{}");
            Assert(store.SweepStaleClaims() == 1, "Exactly the stale quit claim is swept.");
            Assert(!File.Exists(stale) && File.Exists(live) && File.Exists(other),
                "A live claim, and any file not in the claim pattern, stays.");
        }

        private sealed class FakeLooks
        {
            internal TimeSpan Now;
            internal int Looks;
            internal bool Pending;
            internal LauncherQuitRequest? Taken;
            internal Exception? Fails;
            internal readonly List<Exception> Reports = new List<Exception>();

            internal LauncherQuitWatcher Watcher() => new LauncherQuitWatcher(
                () =>
                {
                    Looks++;
                    if (Fails != null) throw Fails;
                    return Pending;
                },
                () => Taken, () => Now, TimeSpan.FromMilliseconds(500), Reports.Add);
        }

        private static void WatcherLooksOnceAnInterval()
        {
            var looks = new FakeLooks();
            var watcher = looks.Watcher();
            Assert(!watcher.Poll() && looks.Looks == 1, "The first poll looks.");
            Assert(!watcher.Poll() && looks.Looks == 1, "A poll in the same interval does not look again.");
            looks.Now = TimeSpan.FromMilliseconds(499);
            Assert(!watcher.Poll() && looks.Looks == 1, "A poll before the interval has passed does not look.");
            looks.Now = TimeSpan.FromMilliseconds(500);
            Assert(!watcher.Poll() && looks.Looks == 2, "A poll once the interval has passed looks again.");
        }

        private static void WatcherQuitsOnce()
        {
            var looks = new FakeLooks { Pending = true, Taken = new LauncherQuitRequest("process-1", "quit-1") };
            var watcher = looks.Watcher();
            Assert(watcher.Poll(), "The look that takes the request asks for the quit.");
            for (var step = 1; step <= 4; step++)
            {
                looks.Now = TimeSpan.FromSeconds(step);
                Assert(!watcher.Poll(), "The quit is asked for once.");
            }

            Assert(looks.Looks == 1, "Once asked, the watcher stops looking.");
        }

        private static void WatcherKeepsLookingPastAForeignRequest()
        {
            var looks = new FakeLooks { Pending = true };
            var watcher = looks.Watcher();
            Assert(!watcher.Poll(), "A request the take refuses asks for nothing.");
            looks.Now = TimeSpan.FromSeconds(1);
            looks.Taken = new LauncherQuitRequest("process-1", "quit-2");
            Assert(watcher.Poll(), "A later request for this process still asks for the quit.");
        }

        private static void WatcherSurvivesAFailedLook()
        {
            var looks = new FakeLooks { Fails = new IOException("Staging is not readable.") };
            var watcher = looks.Watcher();
            Assert(!watcher.Poll() && looks.Reports.Count == 1, "A failed look is reported.");
            looks.Now = TimeSpan.FromSeconds(1);
            Assert(!watcher.Poll() && looks.Reports.Count == 1, "A failure is reported once, not every look.");
            looks.Now = TimeSpan.FromSeconds(2);
            looks.Fails = null;
            looks.Pending = true;
            looks.Taken = new LauncherQuitRequest("process-1", "quit-1");
            Assert(watcher.Poll(), "After a failed look the watcher keeps looking, and quits when asked.");

            var failingReport = new LauncherQuitWatcher(() => throw new IOException("Not readable."), () => null, () => TimeSpan.Zero,
                TimeSpan.FromMilliseconds(500), _ => throw new InvalidOperationException("The log has stopped."));
            Assert(!failingReport.Poll(), "A report that fails does not stop the watcher.");
        }

        private static void WatcherRejectsInvalidArguments()
        {
            Func<bool> pending = () => false;
            Func<LauncherQuitRequest?> take = () => null;
            Func<TimeSpan> clock = () => TimeSpan.Zero;
            Action<Exception> report = _ => { };
            var interval = TimeSpan.FromMilliseconds(500);
            Throws<ArgumentNullException>(() => new LauncherQuitWatcher(null!, take, clock, interval, report));
            Throws<ArgumentNullException>(() => new LauncherQuitWatcher(pending, null!, clock, interval, report));
            Throws<ArgumentNullException>(() => new LauncherQuitWatcher(pending, take, null!, interval, report));
            Throws<ArgumentNullException>(() => new LauncherQuitWatcher(pending, take, clock, interval, null!));
            Throws<ArgumentOutOfRangeException>(() => new LauncherQuitWatcher(pending, take, clock, TimeSpan.Zero, report));
            Throws<ArgumentException>(() => new LauncherQuitRequest("process-1", ""));
            Throws<ArgumentException>(() => new LauncherQuitRequest("../process", "quit-1"));
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
