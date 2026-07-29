using System;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class SceneTransitionTrackerTests
    {
        public static void Run()
        {
            TestFailureAndGenerationIsolation();
            TestTimeoutAndResolution();
            TestPendingAdmissionState();
            TestTimeoutQuarantinesRetryUntilLateArrival();
            TestAbandonQuarantinesRetryUntilLateArrival();
            TestTerminalFailureRetiresAbandonedDispatch();
            TestTerminalFailureRetiresTimedOutDispatch();
        }

        private static void TestFailureAndGenerationIsolation()
        {
            var tracker = new SceneTransitionTracker();
            var old = tracker.Begin(10f, "OldScene");
            tracker.ResolveSceneArrival("OldScene");
            var current = tracker.Begin(11f, "CurrentScene");
            tracker.ReportFailure(old, "late old failure");
            Assert(tracker.ConsumeFailure(12f, 30f) == null,
                "a late async fault from an old dispatch must not end the current transition");

            tracker.ReportFailure(current, "current failure");
            Assert(tracker.ConsumeFailure(12f, 30f) == "current failure",
                "the current dispatch failure should be delivered to the main-thread consumer");
            Assert(tracker.ConsumeFailure(12f, 30f) == null,
                "a scene-load failure should be consumed exactly once");
        }

        private static void TestTimeoutAndResolution()
        {
            var tracker = new SceneTransitionTracker();
            tracker.Begin(100f, "TimedScene");
            Assert(tracker.IsInFlight(129f, 30f), "the transition should be in flight before its deadline");
            Assert(tracker.ConsumeFailure(129f, 30f) == null, "no failure should surface before the deadline");
            var timeout = tracker.ConsumeFailure(130f, 30f);
            Assert(timeout != null && timeout.Contains("30"), "a silent dispatch should fail at its timeout");
            Assert(tracker.IsInFlight(130f, 30f),
                "an uncancelled timed-out transition remains hazardous until its late scene arrives");

            tracker.ResolveSceneArrival("TimedScene");
            tracker.Begin(200f, "ResolvedScene");
            tracker.ResolveSceneArrival("ResolvedScene");
            Assert(tracker.ConsumeFailure(500f, 30f) == null,
                "a scene arrival should resolve the tracker and suppress a later timeout");
        }

        private static void TestPendingAdmissionState()
        {
            var tracker = new SceneTransitionTracker();
            Assert(!tracker.BlocksAdmission, "a fresh tracker must accept a transition");

            var transition = tracker.Begin(10f, "FailedScene");
            Assert(tracker.BlocksAdmission, "a dispatched scene load must block a competing transition");

            tracker.ReportFailure(transition, "async failure");
            Assert(tracker.BlocksAdmission,
                "a reported failure must remain pending until the main thread consumes and cleans it up");
            Assert(tracker.ConsumeFailure(11f, 30f) == "async failure",
                "the pending failure should remain available to the main-thread consumer");
            Assert(!tracker.BlocksAdmission,
                "a terminal loader failure must reopen admission after main-thread cleanup consumes it");
        }

        private static void TestTimeoutQuarantinesRetryUntilLateArrival()
        {
            var tracker = new SceneTransitionTracker();
            tracker.Begin(10f, "OldScene");
            Assert(tracker.ConsumeFailure(40f, 30f) != null, "the first dispatch should time out");
            Assert(tracker.IsQuarantined, "a timed-out, uncancelled dispatch must be quarantined");
            AssertBeginBlocked(tracker,
                "a retry must not begin while the timed-out dispatch can still produce a late scene");

            tracker.ResolveSceneArrival("UnrelatedMenu");
            AssertBeginBlocked(tracker,
                "an unrelated single scene must not retire the old dispatch quarantine");
            tracker.ResolveSceneArrival("OldScene");
            var retry = tracker.Begin(41f, "RetryScene");
            tracker.ReportFailure(retry - 1, "late old failure");
            Assert(tracker.ConsumeFailure(42f, 30f) == null,
                "a late failure token from the retired dispatch must not affect the retry");
            tracker.ResolveSceneArrival("RetryScene");
        }

        private static void TestAbandonQuarantinesRetryUntilLateArrival()
        {
            var tracker = new SceneTransitionTracker();
            tracker.Begin(10f, "OldScene");
            tracker.Abandon();
            Assert(tracker.IsQuarantined,
                "explicit teardown cannot claim to cancel a scene operation without a cancellation handle");
            AssertBeginBlocked(tracker,
                "an explicit teardown must block a retry until the abandoned dispatch's late arrival");

            tracker.ResolveSceneArrival("UnrelatedMenu");
            AssertBeginBlocked(tracker,
                "an unrelated scene must not retire an explicitly abandoned dispatch");
            tracker.ResolveSceneArrival("OldScene");
            tracker.Begin(11f, "RetryScene");
            tracker.ResolveSceneArrival("RetryScene");
            Assert(!tracker.BlocksAdmission, "the retired arrival should make later dispatches safe again");
        }

        private static void TestTerminalFailureRetiresAbandonedDispatch()
        {
            var tracker = new SceneTransitionTracker();
            var transition = tracker.Begin(10f, "OldScene");
            tracker.Abandon();
            tracker.ReportFailure(transition, "terminal fault");
            Assert(tracker.BlocksAdmission,
                "a terminal failure must remain blocked until Unity-thread cleanup consumes it");
            Assert(tracker.ConsumeFailure(11f, 30f) == "terminal fault",
                "an abandoned dispatch's terminal failure should still surface once");
            Assert(!tracker.BlocksAdmission,
                "a terminal fault proves an abandoned target cannot arrive and must reopen admission");
        }

        private static void TestTerminalFailureRetiresTimedOutDispatch()
        {
            var tracker = new SceneTransitionTracker();
            var transition = tracker.Begin(10f, "OldScene");
            Assert(tracker.ConsumeFailure(40f, 30f) != null,
                "the unresolved dispatch should first enter timeout quarantine");
            tracker.ReportFailure(transition, "eventual terminal fault");
            Assert(tracker.ConsumeFailure(41f, 30f) == "eventual terminal fault",
                "a later terminal fault should retire and report the timed-out dispatch");
            Assert(!tracker.BlocksAdmission,
                "a terminal fault must reopen admission from timeout quarantine");
        }

        private static void AssertBeginBlocked(SceneTransitionTracker tracker, string message)
        {
            var threw = false;
            try
            {
                tracker.Begin(999f, "BlockedScene");
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert(threw, message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
