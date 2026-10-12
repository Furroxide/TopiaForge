using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitMultiplayerCoreTests
    {
        private static void TalkStatesStayDistinctWithoutColor()
        {
            var states = (TopiaForgeTalkState[])Enum.GetValues(typeof(TopiaForgeTalkState));
            for (var first = 0; first < states.Length; first++)
            {
                var a = TopiaForgeTalkPresentation.For(states[first]);
                Assert(!string.IsNullOrWhiteSpace(a.Caption), states[first] + " has a caption");
                for (var second = first + 1; second < states.Length; second++)
                {
                    var b = TopiaForgeTalkPresentation.For(states[second]);
                    Assert(a.Glyph != b.Glyph || a.Ring != b.Ring, states[first] + " and " + states[second] + " differ in shape, not only tone");
                    Assert(!string.Equals(a.Caption, b.Caption, StringComparison.Ordinal), states[first] + " and " + states[second] + " have different captions");
                }

                AssertEqual(a.Ring == TopiaForgeTalkRing.Pulsing, states[first] == TopiaForgeTalkState.Talking, states[first] + " pulses only while talking");
            }

            AssertEqual(TopiaForgeTalkPresentation.For((TopiaForgeTalkState)99).Glyph, TopiaForgeTalkGlyph.Microphone, "unknown states present as idle");
            Assert(ReferenceEquals(TopiaForgeTalkPresentation.For(TopiaForgeTalkState.Muted).Caption, TopiaForgeTalkPresentation.For(TopiaForgeTalkState.Muted).Caption), "captions are cached strings");
        }

        private static void TalkPulseHonoursMotion()
        {
            for (var time = 0f; time < 3f; time += 0.137f)
            {
                TopiaForgeBreath.Evaluate(time, 0f, out var stillScale, out var stillAlpha);
                AssertNear(stillScale, 1f, "reduced motion keeps the halo still at " + time);
                AssertNear(stillAlpha, TopiaForgeBreath.RestAlpha, "reduced motion keeps the halo opaque at " + time);

                TopiaForgeBreath.Evaluate(time, float.NaN, out var nanScale, out _);
                AssertNear(nanScale, 1f, "NaN motion is treated as no motion");

                TopiaForgeBreath.Evaluate(time, -1f, out var negativeScale, out var negativeAlpha);
                AssertNear(negativeScale, 1f, "negative motion is treated as no motion (scale)");
                AssertNear(negativeAlpha, TopiaForgeBreath.RestAlpha, "negative motion is treated as no motion (alpha)");

                TopiaForgeBreath.Evaluate(time, 1f, out var scale, out var alpha);
                Assert(scale >= 1f && scale <= 1f + TopiaForgeBreath.GrowthPerMotion + 0.0001f, "scale stays in its subtle band at " + time);
                Assert(alpha >= TopiaForgeBreath.MinAlpha - 0.0001f && alpha <= TopiaForgeBreath.RestAlpha + 0.0001f, "alpha stays in its band at " + time);

                var period = 1f / TopiaForgeBreath.FrequencyHertz;
                TopiaForgeBreath.Evaluate(time + period, 1f, out var nextScale, out var nextAlpha);
                AssertNear(nextScale, scale, "the breath repeats every period", 0.001f);
                AssertNear(nextAlpha, alpha, "the alpha repeats every period", 0.001f);
            }

            var maxAtOne = 0f;
            var maxAtTwo = 0f;
            var maxAtHalf = 0f;
            for (var time = 0f; time < 1f; time += 0.01f)
            {
                TopiaForgeBreath.Evaluate(time, 1f, out var one, out _);
                TopiaForgeBreath.Evaluate(time, 2f, out var two, out _);
                TopiaForgeBreath.Evaluate(time, 0.5f, out var half, out _);
                maxAtOne = Math.Max(maxAtOne, one);
                maxAtTwo = Math.Max(maxAtTwo, two);
                maxAtHalf = Math.Max(maxAtHalf, half);
            }

            Assert(maxAtHalf < maxAtOne && maxAtOne < maxAtTwo, "amplitude follows motion intensity");
            TopiaForgeBreath.Evaluate(0.3f, 50f, out var clamped, out _);
            Assert(clamped <= 1f + (TopiaForgeBreath.GrowthPerMotion * TopiaForgeBreath.MaxMotion) + 0.0001f, "motion above the maximum clamps");
            TopiaForgeBreath.Evaluate(1e9f, 1f, out var late, out var lateAlpha);
            Assert(!float.IsNaN(late) && !float.IsNaN(lateAlpha), "very large times stay finite");
            TopiaForgeBreath.Evaluate(float.PositiveInfinity, 1f, out var infinite, out _);
            Assert(!float.IsNaN(infinite), "an infinite time stays finite");
        }

        private static void PhaseStepperTransitions()
        {
            var stepper = new TopiaForgePhaseStepperState(5);
            AssertEqual(stepper.Count, 5, "count");
            AssertEqual(stepper.ActiveIndex, -1, "nothing active at first");
            Assert(!stepper.IsComplete && !stepper.HasFailed, "a fresh stepper is neither complete nor failed");

            var version = stepper.Version;
            Assert(stepper.Start(), "start activates the first step");
            AssertEqual(stepper.StatusOf(0), TopiaForgeStepStatus.Active, "first step active");
            Assert(stepper.Version != version, "a transition moves the version");
            version = stepper.Version;
            Assert(!stepper.Start(), "start refuses while a step is active");
            AssertEqual(stepper.Version, version, "a refused transition keeps the version");

            Assert(stepper.Skip(2), "a pending step can be skipped");
            Assert(!stepper.Skip(0), "the active step cannot be skipped");
            Assert(stepper.Advance(), "advance");
            AssertEqual(stepper.StatusOf(0), TopiaForgeStepStatus.Done, "advanced step is done");
            AssertEqual(stepper.ActiveIndex, 1, "next step active");
            Assert(stepper.Advance(), "advance over the skipped step");
            AssertEqual(stepper.StatusOf(2), TopiaForgeStepStatus.Skipped, "skipped step stays skipped");
            AssertEqual(stepper.ActiveIndex, 3, "advance jumps past skipped steps");
            AssertEqual(stepper.DoneCount, 2, "two steps done");

            Assert(stepper.Fail(), "fail the active step");
            AssertEqual(stepper.StatusOf(3), TopiaForgeStepStatus.Failed, "active step failed");
            AssertEqual(stepper.ActiveIndex, -1, "nothing active after a failure");
            Assert(stepper.HasFailed && stepper.FailedIndex == 3, "failure is reported");
            Assert(!stepper.Advance() && !stepper.Start() && !stepper.Complete() && !stepper.Fail(), "everything but a jump or reset is refused after a failure");

            Assert(stepper.MoveTo(3), "a jump retries the failed step");
            Assert(!stepper.HasFailed, "a jump clears the failure");
            AssertEqual(stepper.StatusOf(3), TopiaForgeStepStatus.Active, "retried step active");
            AssertEqual(stepper.StatusOf(2), TopiaForgeStepStatus.Skipped, "a jump keeps earlier skips");
            Assert(!stepper.MoveTo(3), "jumping to the current step changes nothing");

            Assert(stepper.MoveTo(1), "jumping backwards");
            AssertEqual(stepper.StatusOf(1), TopiaForgeStepStatus.Active, "earlier step active again");
            AssertEqual(stepper.StatusOf(3), TopiaForgeStepStatus.Pending, "later steps return to pending");
            AssertEqual(stepper.StatusOf(0), TopiaForgeStepStatus.Done, "earlier steps stay done");

            Assert(stepper.MoveTo(2), "jumping onto a skipped step");
            AssertEqual(stepper.StatusOf(2), TopiaForgeStepStatus.Active, "a jump un-skips its target");
            AssertEqual(stepper.StatusOf(1), TopiaForgeStepStatus.Done, "the step jumped past is done");

            Assert(stepper.Complete(), "complete");
            Assert(stepper.IsComplete, "complete reports so");
            Assert(!stepper.Complete(), "completing twice is refused");
            Assert(!stepper.Advance(), "advancing a complete stepper is refused");

            Assert(stepper.Reset(), "reset");
            for (var index = 0; index < stepper.Count; index++)
            {
                AssertEqual(stepper.StatusOf(index), TopiaForgeStepStatus.Pending, "reset step " + index);
            }

            Assert(!stepper.Reset(), "resetting a fresh stepper changes nothing");

            var failEarly = new TopiaForgePhaseStepperState(3);
            Assert(failEarly.Fail(), "failing before start fails the first step");
            AssertEqual(failEarly.FailedIndex, 0, "the first step failed");

            var walk = new TopiaForgePhaseStepperState(2);
            Assert(walk.Advance(), "advance with nothing active starts");
            Assert(walk.Advance(), "advance the first step");
            Assert(walk.Advance(), "advance the last step");
            Assert(walk.IsComplete && walk.ActiveIndex == -1, "advancing the last step completes the stepper");
            Assert(!walk.Fail(), "a complete stepper has nothing to fail");
        }

        private static void PhaseStepperProgressAndBounds()
        {
            var stepper = new TopiaForgePhaseStepperState(7);
            Assert(!stepper.TryGetProgress(5, out _, out _), "no progress at first");
            Assert(stepper.SetProgress(5, 3, 7), "set progress");
            Assert(stepper.TryGetProgress(5, out var current, out var total) && current == 3 && total == 7, "progress reads back");
            Assert(!stepper.SetProgress(5, 3, 7), "unchanged progress is not a change");
            Assert(stepper.SetProgress(5, 99, 7) && stepper.TryGetProgress(5, out current, out _) && current == 7, "progress clamps to the total");
            Assert(stepper.SetProgress(5, -4, 7) && stepper.TryGetProgress(5, out current, out _) && current == 0, "progress clamps at zero");
            Assert(stepper.SetProgress(5, 3, 0) && !stepper.TryGetProgress(5, out _, out _), "a zero total clears progress");

            stepper.SetProgress(5, 3, 7);
            stepper.MoveTo(6);
            Assert(stepper.TryGetProgress(5, out _, out _), "progress on an earlier step survives a forward jump");
            stepper.MoveTo(2);
            Assert(!stepper.TryGetProgress(5, out _, out _), "a backward jump clears later progress");
            stepper.SetProgress(1, 1, 2);
            stepper.Reset();
            Assert(!stepper.TryGetProgress(1, out _, out _), "reset clears progress");

            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgePhaseStepperState(0), "an empty stepper is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgePhaseStepperState(TopiaForgePhaseStepperState.MaxSteps + 1), "too many steps are rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => stepper.StatusOf(7), "an index past the end is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => stepper.MoveTo(-1), "a negative jump is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => stepper.Skip(99), "skipping past the end is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => stepper.SetProgress(-1, 1, 2), "progress on a negative index is rejected");
        }

        private static void ChatLogEvictsOldestAndSanitizes()
        {
            var log = new TopiaForgeChatLogModel(capacity: 3, maxLineChars: 12);
            Assert(log.TryAppend("first", TopiaForgeChatLineKind.Player, 1f, out var line), "append");
            AssertEqual(line.Sequence, 1L, "sequences start at one");
            log.TryAppend("second", TopiaForgeChatLineKind.System, 2f, out _);
            log.TryAppend("third", TopiaForgeChatLineKind.Whisper, 3f, out _);
            AssertEqual(log.Count, 3, "filled to capacity");
            AssertEqual(log.EvictedCount, 0L, "nothing evicted yet");

            log.TryAppend("fourth", TopiaForgeChatLineKind.Error, 4f, out _);
            AssertEqual(log.Count, 3, "count stays at capacity");
            AssertEqual(log.EvictedCount, 1L, "the oldest line was evicted");
            AssertEqual(log[0].Text, "second", "the oldest retained line moves up");
            AssertEqual(log[2].Text, "fourth", "the newest line is last");
            AssertEqual(log[2].Kind, TopiaForgeChatLineKind.Error, "kinds are kept");
            AssertEqual(log.LastSequence, 4L, "sequence counts every post");

            for (var index = 0; index < 10; index++)
            {
                log.TryAppend("spam " + index, TopiaForgeChatLineKind.Player, 5f + index, out _);
            }

            AssertEqual(log[0].Text, "spam 7", "a long burst keeps only the newest lines");
            AssertEqual(log[2].Text, "spam 9", "the burst's last line is newest");
            AssertEqual(log.EvictedCount, 11L, "every overflow is counted");

            Assert(log.TryAppend("hi\nthere\r\n\tyou", TopiaForgeChatLineKind.Player, 20f, out line), "multi-line text is accepted");
            AssertEqual(line.Text, "hi there you", "line breaks cannot add lines to the log");
            log.TryAppend("0123456789ABCDEF", TopiaForgeChatLineKind.Player, 21f, out line);
            AssertEqual(line.Text.Length, 12, "lines are bounded");
            Assert(line.Text.EndsWith(TopiaForgePlainText.Ellipsis.ToString(), StringComparison.Ordinal), "a bounded line shows an ellipsis");
            Assert(!log.TryAppend(" \r\n\t ", TopiaForgeChatLineKind.Player, 22f, out _), "blank text is refused");
            Assert(!log.TryAppend(null, TopiaForgeChatLineKind.Player, 22f, out _), "null text is refused");
            log.TryAppend("clock", TopiaForgeChatLineKind.System, float.NaN, out line);
            AssertNear(line.PostedAt, 0f, "a NaN clock is stored as zero");

            var sequence = log.LastSequence;
            log.Clear();
            AssertEqual(log.Count, 0, "clear empties the log");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = log[0], "reading an empty log is rejected");
            log.TryAppend("after", TopiaForgeChatLineKind.Player, 30f, out line);
            AssertEqual(line.Sequence, sequence + 1, "sequences keep counting after a clear");
            AssertEqual(log[0].Text, "after", "the log refills from the start");

            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeChatLogModel(0), "a zero capacity is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeChatLogModel(TopiaForgeChatLogModel.MaxCapacity + 1), "an oversized capacity is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeChatLogModel(10, 0), "a zero line bound is rejected");
        }

        private static void ChatLinesFadeWhileClosed()
        {
            AssertNear(TopiaForgeChatFade.Alpha(0f, 8f, 2f, false), 1f, "a new line is fully visible");
            AssertNear(TopiaForgeChatFade.Alpha(7.9f, 8f, 2f, false), 1f, "fully visible until the timeout");
            AssertNear(TopiaForgeChatFade.Alpha(9f, 8f, 2f, false), 0.5f, "halfway through the fade");
            AssertNear(TopiaForgeChatFade.Alpha(10f, 8f, 2f, false), 0f, "gone at the end of the fade");
            AssertNear(TopiaForgeChatFade.Alpha(999f, 8f, 2f, false), 0f, "gone long after");
            AssertNear(TopiaForgeChatFade.Alpha(9f, 8f, 2f, true), 0f, "reduced motion hides at the timeout instead of fading");
            AssertNear(TopiaForgeChatFade.Alpha(7f, 8f, 2f, true), 1f, "reduced motion keeps the visible window");
            AssertNear(TopiaForgeChatFade.Alpha(-5f, 8f, 2f, false), 1f, "a line from the future (clock skew) is visible");
            AssertNear(TopiaForgeChatFade.Alpha(float.NaN, 8f, 2f, false), 1f, "a NaN age is treated as new");
            AssertNear(TopiaForgeChatFade.Alpha(8.5f, 8f, 0f, false), 0f, "a zero fade hides at the timeout");
            Assert(!TopiaForgeChatFade.IsExpired(9.99f, 8f, 2f), "not expired while fading");
            Assert(TopiaForgeChatFade.IsExpired(10f, 8f, 2f), "expired at the end of the fade");
            Assert(!TopiaForgeChatFade.IsExpired(-1f, 8f, 2f), "negative ages never expire");

            var log = new TopiaForgeChatLogModel(capacity: 10);
            for (var index = 0; index < 6; index++)
            {
                log.TryAppend("line " + index, TopiaForgeChatLineKind.Player, index * 2f, out _);
            }

            AssertEqual(log.CountVisibleWhenClosed(10f, 8f, 1.5f, 8), 5, "lines posted in the last 9.5 seconds are visible");
            AssertEqual(log.CountVisibleWhenClosed(10f, 8f, 1.5f, 3), 3, "the closed view caps its line count");
            AssertEqual(log.CountVisibleWhenClosed(100f, 8f, 1.5f, 8), 0, "an idle log shows nothing");
            AssertEqual(log.CountVisibleWhenClosed(19f, 8f, 1.5f, 8), 1, "only the newest line remains");
        }
    }
}
