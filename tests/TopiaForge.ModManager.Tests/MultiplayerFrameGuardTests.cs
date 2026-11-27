using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The in-game multiplayer interface's boundary with Unity's frame. The game installs a crash reporter on Unity's log,
    /// so an exception that escapes the overlay's frame is a path into crash reports: the overlay runs the presenter, what
    /// shows outside the tab and the tab itself through a guard that catches each failure, logs it once, redacted, and lets
    /// the rest of the frame run.
    /// </summary>
    internal static class MultiplayerFrameGuardTests
    {
        internal static void Run()
        {
            AThrowingPresenterNeitherBreaksTheFrameNorLogsAnAddress();
            EachDistinctFailureIsLoggedOnceAndTheLogIsBounded();
            ABrokenLogNeverBreaksTheFrame();
            AFrameThatDoesNotFailAllocatesNothing();
            Console.WriteLine("MultiplayerFrameGuardTests passed.");
        }

        private static void AThrowingPresenterNeitherBreaksTheFrameNorLogsAnAddress()
        {
            var rig = new SessionsRig();
            var lines = new List<string>();
            var guard = new MultiplayerFrameGuard(lines.Add);
            var outside = 0;
            var tab = 0;
            var frame = new MultiplayerOverlayFrame(guard, rig.Presenter, () => outside++, () => tab++);
            rig.Environment.ControlFailure = new InvalidOperationException("the provider at 203.0.113.7:27250 stopped answering play.example.org");
            var rest = 0;
            for (var index = 0; index < 100; index++)
            {
                frame.Tick(0.016f);
                rest++;
            }

            That(rest == 100 && outside == 100 && tab == 100, "the stages after the failing presenter, and the rest of the frame, ran every frame");
            That(guard.Failures == 100 && lines.Count == 1, "the failure was caught every frame and logged once (" + lines.Count + " lines)");
            var line = lines[0];
            That(!MultiplayerSessionsTests.Shows(line), "the logged line holds no address (" + line + ")");
            That(line.StartsWith(MultiplayerFrameGuard.Prefix + MultiplayerOverlayFrame.PresenterStage, StringComparison.Ordinal) &&
                 line.Contains("InvalidOperationException", StringComparison.Ordinal) && line.Contains("stopped answering", StringComparison.Ordinal) &&
                 line.Contains("MultiplayerSessionsPresenter.Tick", StringComparison.Ordinal),
                "and says which stage failed, how, and where, with its stack (" + line + ")");

            rig.Environment.ControlFailure = null;
            frame.Tick(0.016f);
            That(guard.Failures == 100 && rig.Presenter.IsAvailable, "once the provider answers again, the presenter carries on");

            // The stages after the presenter are guarded too, each on its own.
            var later = new MultiplayerFrameGuard(lines.Add);
            var ticks = 0;
            var failing = new MultiplayerOverlayFrame(later, rig.Presenter,
                () => throw new NullReferenceException("a dialog for 198.51.100.4 was destroyed"), () => ticks++);
            failing.Tick(0.016f);
            failing.Tick(0.016f);
            That(ticks == 2 && later.Failures == 2 && lines.Count == 2 && !MultiplayerSessionsTests.Shows(lines[1]) &&
                 lines[1].Contains(MultiplayerOverlayFrame.OutsideStage, StringComparison.Ordinal),
                "a failing prompt or overlay stops neither the tab nor the frame, and is logged once, redacted");

            // The tab is the frame's last stage: its failure stays inside the frame as well, and its stack is redacted too.
            var tabGuard = new MultiplayerFrameGuard(lines.Add);
            var tabFrame = new MultiplayerOverlayFrame(tabGuard, rig.Presenter, () => { }, () => throw new AddressInStackException());
            var after = 0;
            for (var index = 0; index < 3; index++)
            {
                tabFrame.Tick(0.016f);
                after++;
            }

            That(after == 3 && tabGuard.Failures == 3 && lines.Count == 3 && lines[2].Contains(MultiplayerOverlayFrame.TabStage, StringComparison.Ordinal) &&
                 lines[2].Contains("Overlay.Tab", StringComparison.Ordinal) && !MultiplayerSessionsTests.Shows(lines[2]),
                "a failing tab leaves the rest of the frame running, and neither its words nor its stack reach the log with an address (" + lines[2] + ")");
        }

        /// <summary>A failure whose stack names a host, as a source path on a shared drive would.</summary>
        private sealed class AddressInStackException : InvalidOperationException
        {
            internal AddressInStackException()
                : base("the tab could not bind")
            {
            }

            public override string StackTrace =>
                "   at Overlay.Tab.Bind() in 203.0.113.7/src/Overlay/Tab.cs:line 7\n   at Overlay.Tab.Tick() in play.example.org/src/Overlay/Tab.cs:line 3";
        }

        private static void EachDistinctFailureIsLoggedOnceAndTheLogIsBounded()
        {
            var lines = new List<string>();
            var guard = new MultiplayerFrameGuard(lines.Add);
            Action first = () => throw new InvalidOperationException("first");
            That(!guard.Run("stage one", first) && !guard.Run("stage one", first) && lines.Count == 1, "the same failure again is not logged again");
            guard.Run("stage two", first);
            guard.Run("stage one", () => throw new ArgumentException("first"));
            guard.Run("stage one", () => throw new InvalidOperationException("second"));
            That(lines.Count == 4, "another stage, another type or other words are another failure (" + lines.Count + ")");
            That(guard.Run("stage one", () => { }), "a stage that finishes says so");

            // A failure whose words change every frame cannot fill the log.
            for (var frame = 0; frame < 500; frame++)
            {
                var count = frame;
                guard.Run("a counting stage", () => throw new InvalidOperationException("frame " + count.ToString(CultureInfo.InvariantCulture)));
            }

            That(lines.Count == MultiplayerFrameGuard.MaximumLogged + 1 && lines.Last() == MultiplayerFrameGuard.Silenced && guard.Failures == 505,
                "at most " + MultiplayerFrameGuard.MaximumLogged + " failures are logged, then one line says the rest are not (" + lines.Count + ")");
        }

        private static void ABrokenLogNeverBreaksTheFrame()
        {
            var guard = new MultiplayerFrameGuard(_ => throw new System.IO.IOException("the log is full"));
            That(!guard.Run("a stage", () => throw new InvalidOperationException("boom")) && guard.Failures == 1,
                "a log that cannot take the line leaves the frame running");
        }

        private static void AFrameThatDoesNotFailAllocatesNothing()
        {
            var rig = new SessionsRig();
            var guard = new MultiplayerFrameGuard(_ => { });
            var frame = new MultiplayerOverlayFrame(guard, rig.Presenter, () => { }, () => { });
            rig.Frame(60);
            var allocated = Seam.Allocated(() => frame.Tick(0.016f), 10_000);
            That(allocated == 0 && guard.Failures == 0, "a guarded frame that does not fail allocates nothing (" + allocated + " bytes over 10,000 frames)");
        }
    }
}
