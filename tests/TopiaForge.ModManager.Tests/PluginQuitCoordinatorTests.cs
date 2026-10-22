using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A normal quit must leave the clean-exit record that keeps the next start out of safe mode. Unity runs
    /// no frame once a quit starts destroying objects, so the plugin holds the quit until its teardown, which
    /// drains over later frames, has finished. A stuck teardown must not keep the game open.
    /// </summary>
    internal static class PluginQuitCoordinatorTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            var cases = new (string Name, Action Test)[]
            {
                (nameof(HeldQuitResumesOnceAfterTeardownFinishes), HeldQuitResumesOnceAfterTeardownFinishes),
                (nameof(SynchronousTeardownLetsTheFirstQuitThrough), SynchronousTeardownLetsTheFirstQuitThrough),
                (nameof(TeardownFinishedElsewhereNeverStartsAQuit), TeardownFinishedElsewhereNeverStartsAQuit),
                (nameof(StuckTeardownQuitsAtTheDeadline), StuckTeardownQuitsAtTheDeadline),
                (nameof(QuitRequestedAfterTheDeadlineGoesAhead), QuitRequestedAfterTheDeadlineGoesAhead),
                (nameof(TeardownThatCannotStartDoesNotHoldTheQuit), TeardownThatCannotStartDoesNotHoldTheQuit),
                (nameof(FailingReportDoesNotHoldTheQuit), FailingReportDoesNotHoldTheQuit),
                (nameof(InvalidArgumentsAreRejected), InvalidArgumentsAreRejected),
                (nameof(HeldQuitRecordsACleanExit), () => HeldQuitRecordsACleanExit(root)),
                (nameof(QuitAtTheDeadlineLeavesSafeModeForTheNextStart), () => QuitAtTheDeadlineLeavesSafeModeForTheNextStart(root)),
                (nameof(TeardownLimitReadsTheQuitClock), TeardownLimitReadsTheQuitClock),
            };
            foreach (var (name, test) in cases)
            {
                try { test(); }
                catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
            }

            if (failures.Count != 0) throw new AggregateException("Plugin quit coordination regressions failed.", failures);
            Console.WriteLine("Plugin quit coordination: " + cases.Length + " behavioral checks passed.");
        }

        private static void HeldQuitResumesOnceAfterTeardownFinishes()
        {
            var game = new FakeGame();
            Check(!game.Quit.RequestQuit(), "A quit must be held while teardown needs later frames.");
            Check(!game.Quit.RequestQuit(), "Another quit request must stay held while teardown runs.");
            Check(game.TeardownStarts == 1, "Repeated quit requests must start teardown exactly once.");
            game.Advance(TimeSpan.FromSeconds(1));
            Check(!game.Quit.ShouldResumeQuit(), "An unfinished teardown must not resume the quit.");
            game.Finished = true;
            Check(game.Quit.ShouldResumeQuit(), "A finished teardown must request the held quit again.");
            Check(!game.Quit.ShouldResumeQuit(), "The held quit must be requested again only once.");
            Check(game.Quit.RequestQuit(), "The repeated quit must go ahead once teardown has finished.");
            Check(!game.Quit.DeadlinePassed, "A teardown that finished in time must not report a missed deadline.");
        }

        private static void SynchronousTeardownLetsTheFirstQuitThrough()
        {
            var game = new FakeGame { FinishesWhenStarted = true };
            Check(game.Quit.RequestQuit(), "A teardown that finishes as it starts must let the first quit through.");
            Check(!game.Quit.ShouldResumeQuit(), "A quit that was never held must not be requested again.");
            Check(game.TeardownStarts == 1, "The quit must still run the teardown.");
        }

        private static void TeardownFinishedElsewhereNeverStartsAQuit()
        {
            var game = new FakeGame { Finished = true };
            Check(!game.Quit.ShouldResumeQuit(), "Teardown without a quit request (OnDestroy) must never start a quit.");
            Check(game.Quit.RequestQuit(), "A quit after a finished teardown must go ahead.");
            Check(game.TeardownStarts == 0, "A finished teardown must not be started again.");
        }

        private static void StuckTeardownQuitsAtTheDeadline()
        {
            var game = new FakeGame();
            Check(!game.Quit.RequestQuit(), "The quit must be held while teardown runs.");
            game.Advance(Limit - TimeSpan.FromTicks(1));
            Check(!game.Quit.ShouldResumeQuit() && !game.Quit.RequestQuit(),
                "The quit must stay held until the deadline.");
            game.Advance(TimeSpan.FromTicks(1));
            Check(game.Quit.ShouldResumeQuit(), "The held quit must be requested again at the deadline.");
            Check(game.Quit.DeadlinePassed, "A quit forced by the deadline must say so.");
            Check(game.Quit.RequestQuit(), "The quit must go ahead after the deadline, so the game cannot be trapped.");
            Check(!game.Quit.ShouldResumeQuit(), "The forced quit must be requested only once.");
        }

        private static void QuitRequestedAfterTheDeadlineGoesAhead()
        {
            var game = new FakeGame();
            Check(!game.Quit.RequestQuit(), "The first quit must be held.");
            game.Advance(Limit + TimeSpan.FromSeconds(5));
            Check(game.Quit.RequestQuit() && game.Quit.DeadlinePassed,
                "A quit requested after the deadline must go ahead at once.");
            Check(game.TeardownStarts == 1, "The late quit must not start teardown again.");
        }

        private static void TeardownThatCannotStartDoesNotHoldTheQuit()
        {
            var reported = new List<Exception>();
            var starts = 0;
            var quit = new PluginQuitCoordinator(() => { starts++; throw new InvalidOperationException("broken"); },
                () => false, () => TimeSpan.Zero, Limit, reported.Add);
            Check(quit.RequestQuit(), "A teardown that cannot start must not hold the quit.");
            Check(reported.Count == 1 && reported[0].Message == "broken", "The failed start must be reported once.");
            Check(quit.RequestQuit() && starts == 1, "A failed teardown must not be retried on the next quit.");
            Check(!quit.ShouldResumeQuit(), "A quit that was not held must not be requested again.");
        }

        private static void FailingReportDoesNotHoldTheQuit()
        {
            var quit = new PluginQuitCoordinator(() => throw new InvalidOperationException("broken"),
                () => false, () => TimeSpan.Zero, Limit, _ => throw new IOException("log closed"));
            Check(quit.RequestQuit(), "A closed log must not keep the game open.");
        }

        private static void InvalidArgumentsAreRejected()
        {
            Throws<ArgumentNullException>(() => new PluginQuitCoordinator(null!, () => true, () => TimeSpan.Zero, Limit, _ => { }));
            Throws<ArgumentNullException>(() => new PluginQuitCoordinator(() => { }, null!, () => TimeSpan.Zero, Limit, _ => { }));
            Throws<ArgumentNullException>(() => new PluginQuitCoordinator(() => { }, () => true, null!, Limit, _ => { }));
            Throws<ArgumentNullException>(() => new PluginQuitCoordinator(() => { }, () => true, () => TimeSpan.Zero, Limit, null!));
            Throws<ArgumentOutOfRangeException>(() => new PluginQuitCoordinator(() => { }, () => true, () => TimeSpan.Zero, TimeSpan.Zero, _ => { }));
            Throws<ArgumentOutOfRangeException>(() => new PluginQuitCoordinator(() => { }, () => true, () => TimeSpan.Zero, TimeSpan.FromSeconds(-1), _ => { }));
        }

        // The plugin's teardown ends by recording the clean exit; a quit held until then keeps the next start normal.
        private static void HeldQuitRecordsACleanExit(string root)
        {
            var path = Path.Combine(root, "quit-coordinator", "held.json");
            var journal = StartupJournal.Begin(path, out _);
            journal.MarkStartupComplete();
            var game = new FakeGame();
            game.OnFrame = () => { journal.MarkCleanExit(); game.Finished = true; };
            Check(!game.Quit.RequestQuit(), "The quit must wait for the teardown that records the clean exit.");
            game.RunFrame();
            Check(game.Quit.ShouldResumeQuit() && game.Quit.RequestQuit(), "The quit must go ahead after the clean exit.");

            StartupJournal.Begin(path, out var recovery);
            Check(!recovery.SafeMode && recovery.QuarantineModId.Length == 0,
                "A quit held for teardown must let the next start load every mod.");
        }

        private static void QuitAtTheDeadlineLeavesSafeModeForTheNextStart(string root)
        {
            var path = Path.Combine(root, "quit-coordinator", "deadline.json");
            var journal = StartupJournal.Begin(path, out _);
            journal.MarkStartupComplete();
            var game = new FakeGame();
            Check(!game.Quit.RequestQuit(), "The quit must be held while teardown runs.");
            game.Advance(Limit);
            Check(game.Quit.ShouldResumeQuit() && game.Quit.RequestQuit(), "The quit must go ahead at the deadline.");

            StartupJournal.Begin(path, out var recovery);
            Check(recovery.SafeMode, "Unfinished cleanup must still open the next start in safe mode.");
        }

        private static void TeardownLimitReadsTheQuitClock() => ReadsTheQuitClock("new PluginQuitCoordinator(");

        /// <summary>
        /// The plugin stops its startup stopwatch once startup is reported, and a deadline read from a stopped clock never
        /// passes: a stuck teardown would keep the game open for good. Every quit timer the plugin makes reads its quit
        /// clock instead, which starts with the plugin and is never stopped.
        /// </summary>
        internal static void ReadsTheQuitClock(string construction)
        {
            var sources = string.Join("\n", Directory.GetFiles(Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager"),
                "TopiaForgeModManagerPlugin*.cs").OrderBy(file => file, StringComparer.Ordinal).Select(File.ReadAllText));
            Check(sources.Contains("startupStopwatch.Stop()", StringComparison.Ordinal),
                "The startup stopwatch stops after startup; if that changes, revisit this test.");
            Check(sources.Contains("private readonly Stopwatch quitClock = Stopwatch.StartNew();", StringComparison.Ordinal),
                "The plugin keeps a quit clock that starts with it.");
            foreach (var stop in new[] { "quitClock.Stop(", "quitClock.Reset(", "quitClock.Restart(" })
                Check(!sources.Contains(stop, StringComparison.Ordinal), "The quit clock is never stopped or reset.");
            var start = sources.IndexOf(construction, StringComparison.Ordinal);
            Check(start >= 0, construction + " is made in the plugin.");
            var call = sources.Substring(start, sources.IndexOf(");", start, StringComparison.Ordinal) - start);
            Check(call.Contains("() => quitClock.Elapsed", StringComparison.Ordinal) && !call.Contains("startupStopwatch", StringComparison.Ordinal),
                construction + " reads the quit clock, never the startup stopwatch.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private sealed class FakeGame
        {
            private TimeSpan now = TimeSpan.FromMinutes(3);

            internal FakeGame()
            {
                Quit = new PluginQuitCoordinator(StartTeardown, () => Finished, () => now, Limit,
                    error => throw new InvalidOperationException("Unexpected teardown failure.", error));
            }

            internal PluginQuitCoordinator Quit { get; }
            internal bool Finished { get; set; }
            internal bool FinishesWhenStarted { get; set; }
            internal int TeardownStarts { get; private set; }
            internal Action? OnFrame { get; set; }

            internal void Advance(TimeSpan elapsed) => now += elapsed;

            internal void RunFrame()
            {
                now += TimeSpan.FromMilliseconds(16);
                OnFrame?.Invoke();
            }

            private void StartTeardown()
            {
                TeardownStarts++;
                if (FinishesWhenStarted) Finished = true;
            }
        }
    }
}
