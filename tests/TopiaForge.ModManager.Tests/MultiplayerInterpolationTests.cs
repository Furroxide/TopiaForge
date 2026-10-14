using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Snapshot interpolation: delay adaptation, ordering rules and the extrapolation cap (WS3.5).</summary>
    internal static class MultiplayerInterpolationTests
    {
        private const long Start = 1_000_000;
        private const long Step = 100_000;
        private const long Transit = 50_000;

        public static void Run()
        {
            SteadyStreamInterpolatesExactly();
            FastStreamUsesTheMinimumDelay();
            DelayAdaptsToJitterWithinTheRange();
            DelayChangesSmoothlyAndRenderTimeNeverRewinds();
            ALateSampleNeverPausesPlayback();
            TheDelayCoversTheLongestBufferedGap();
            UnevenCadenceNeverRunsPastTheNewestSample();
            OrdersDuplicatesAndStaleSamples();
            FullBufferEvictsTheOldest();
            AFullBufferKeepsTheSegmentOnScreen();
            ExtrapolatesUpToTheCap();
            HoldsAndResetsAtTheEdges();
            InterpolatesRotationsAlongTheShortArc();
            ValidatesInputs();
            Console.WriteLine("MultiplayerInterpolationTests passed.");
        }

        private static void SteadyStreamInterpolatesExactly()
        {
            var buffer = new SnapshotInterpolationBuffer();
            var delivered = 0;
            var lastRender = long.MinValue;
            var interpolatedFrames = 0;
            for (var now = Start + Transit; now < Start + 3_000_000; now += 16_667)
            {
                while (Start + delivered * Step + Transit <= now)
                {
                    Equal(SnapshotSampleResult.Accepted, buffer.Add(Moving(delivered, false), Start + delivered * Step + Transit), "accept");
                    delivered++;
                }

                True(buffer.TrySample(now, out var result), "a sample is available");
                True(result.RenderTimeMicros >= lastRender, "render time never rewinds");
                lastRender = result.RenderTimeMicros;
                if (now < Start + 400_000) continue;
                Equal(InterpolationMode.Interpolating, result.Mode, "a steady stream always interpolates");
                Near((result.RenderTimeMicros - Start) / (double)Step, result.Position.X, 1e-4, "linear motion is reproduced");
                interpolatedFrames++;
            }

            Equal(0L, buffer.JitterMicros, "no jitter");
            Equal(Step, buffer.IntervalMicros, "observed interval");
            Equal(Step, buffer.TargetDelayMicros, "delay is one interval");
            Equal(Step, buffer.CurrentDelayMicros, "current delay");
            True(interpolatedFrames > 140, "most frames were checked");
        }

        private static void FastStreamUsesTheMinimumDelay()
        {
            var buffer = new SnapshotInterpolationBuffer(33_333);
            for (var index = 0; index < 60; index++)
            {
                buffer.Add(Sample(Start + index * 33_333, index), Start + index * 33_333 + Transit);
            }

            Equal(33_333L, buffer.IntervalMicros, "30 Hz interval");
            Equal(SnapshotInterpolationBuffer.MinimumDelayMicros, buffer.TargetDelayMicros, "clamped to 80 ms");
        }

        private static void DelayAdaptsToJitterWithinTheRange()
        {
            // Extra transit uniform on [0, a) gives a mean consecutive difference of a / 3, which RFC 3550's
            // estimator tracks: 26.7 ms for a = 80 ms, so the target is one 100 ms interval plus three of those.
            var moderate = Jittered(seed: 3, maximumExtraTransit: 80_000, out var moderateExtrapolations);
            True(moderate.JitterMicros > 22_000 && moderate.JitterMicros < 31_000, "jitter estimate " + moderate.JitterMicros);
            Equal(Step, moderate.IntervalMicros, "in-order arrivals keep the interval");
            Equal(moderate.IntervalMicros + 3 * moderate.JitterMicros, moderate.TargetDelayMicros, "target is interval plus three jitters");
            Equal(moderate.TargetDelayMicros, moderate.CurrentDelayMicros, "the delay has converged");
            Equal(0, moderateExtrapolations, "once adapted, a jittery stream never runs dry");

            var extreme = Jittered(seed: 5, maximumExtraTransit: 400_000, out _);
            True(extreme.JitterMicros > 113_000 && extreme.JitterMicros < 154_000, "extreme jitter estimate " + extreme.JitterMicros);
            Equal(SnapshotInterpolationBuffer.MaximumDelayMicros, extreme.TargetDelayMicros, "clamped to 350 ms");
            Equal(SnapshotInterpolationBuffer.MaximumDelayMicros, extreme.CurrentDelayMicros, "the current delay reaches the clamp");
        }

        private static void DelayChangesSmoothlyAndRenderTimeNeverRewinds()
        {
            // Calm for five seconds, rough for ten, then calm for twenty-five: long enough for the jitter estimate to
            // decay to zero, so the target moves up and all the way back down. Moving the render clock with the offset
            // estimate as well used to swing playback between a standstill and 2.06 times real time here.
            var random = new SeededRandom(29);
            var arrivals = new long[400];
            for (var index = 0; index < arrivals.Length; index++)
            {
                var rough = index >= 50 && index < 150;
                arrivals[index] = Start + index * Step + Transit + (rough ? (long)random.NextDouble(0, 250_000) : 0);
            }

            var buffer = new SnapshotInterpolationBuffer();
            var playback = new PlaybackRate();
            var maximumDelay = 0L;
            for (var now = Start; now < Start + 40_000_000; now += 16_667)
            {
                for (var index = 0; index < arrivals.Length; index++)
                {
                    if (arrivals[index] > now - 16_667 && arrivals[index] <= now) buffer.Add(Moving(index, true), arrivals[index]);
                }

                if (!playback.Sample(buffer, now, out _)) continue;
                maximumDelay = Math.Max(maximumDelay, buffer.CurrentDelayMicros);
                True(buffer.TargetDelayMicros >= SnapshotInterpolationBuffer.MinimumDelayMicros &&
                     buffer.TargetDelayMicros <= SnapshotInterpolationBuffer.MaximumDelayMicros, "the target stays in range");
            }

            True(playback.Frames > 2000, "every frame was checked");
            True(playback.Corrections > 0 && playback.FastestRatio < 1.3,
                "the rough phase needed gentle catch-up only: " + playback.FastestRatio.ToString("F3", CultureInfo.InvariantCulture));
            True(maximumDelay > 200_000, "the rough phase raised the delay to " + maximumDelay);
            Equal(Step, buffer.CurrentDelayMicros, "the calm phase brought it back to one interval");
            Equal(Step, buffer.TargetDelayMicros, "and the target with it");
        }

        private static void ALateSampleNeverPausesPlayback()
        {
            // One sample 700 ms late in a steady 10 Hz stream drags the offset estimate back 87.5 ms at once. Playback
            // used to stand still for five frames and then run at up to 1.47 times real time to catch up.
            var buffer = new SnapshotInterpolationBuffer();
            var playback = new PlaybackRate();
            var lateResult = SnapshotSampleResult.Accepted;
            var maximumTarget = 0L;
            for (var now = Start; now < Start + 10_000_000; now += 16_667)
            {
                for (var index = 0; index < 100; index++)
                {
                    var arrival = Start + index * Step + Transit + (index == 30 ? 700_000 : 0);
                    if (arrival <= now - 16_667 || arrival > now) continue;
                    var outcome = buffer.Add(Moving(index, true), arrival);
                    if (index == 30) lateResult = outcome;
                }

                if (!playback.Sample(buffer, now, out _)) continue;
                maximumTarget = Math.Max(maximumTarget, buffer.TargetDelayMicros);
            }

            Equal(SnapshotSampleResult.Stale, lateResult, "rendering had already passed the late sample");
            Equal(SnapshotInterpolationBuffer.MaximumDelayMicros, maximumTarget, "its jitter still raised the target to the cap");
            True(playback.Frames > 590, "every frame was checked");
            True(playback.SlowestAdvance > 0, "playback never stands still");
            Equal(0, playback.Corrections, "and never leaves 0.9 to 1.1 times real time");
        }

        private static void TheDelayCoversTheLongestBufferedGap()
        {
            var buffer = new SnapshotInterpolationBuffer();
            Equal(0L, buffer.LargestGapMicros, "no samples, no gap");
            buffer.Add(Sample(Start, 0), Start);
            Equal(0L, buffer.LargestGapMicros, "one sample, no gap");
            Equal(Step, buffer.TargetDelayMicros, "the nominal interval until gaps are seen");
            buffer.Add(Sample(Start + 100_000, 1), Start + 100_000);
            buffer.Add(Sample(Start + 300_000, 3), Start + 300_000);
            buffer.Add(Sample(Start + 350_000, 4), Start + 350_000);
            Equal(200_000L, buffer.LargestGapMicros, "a missing sample doubles a gap");
            True(buffer.IntervalMicros < 200_000, "the mean interval is shorter: " + buffer.IntervalMicros);
            Equal(200_000L, buffer.TargetDelayMicros, "the delay covers the longest gap");

            // The gap filled late shrinks it again; a gap that leaves the buffer stops counting.
            Equal(SnapshotSampleResult.Accepted, buffer.Add(Sample(Start + 200_000, 2), Start + 360_000), "a late sample fills the gap");
            Equal(100_000L, buffer.LargestGapMicros, "the gap is filled");
            for (var index = 5; index <= 12; index++) buffer.Add(Sample(Start + 300_000 + index * 10_000, index), Start + 300_000 + index * 10_000);
            Equal(10_000L, buffer.LargestGapMicros, "evicted samples take their gaps with them");

            // A gap longer than the cap clamps the delay at 350 ms.
            var paused = new SnapshotInterpolationBuffer();
            paused.Add(Sample(Start, 0), Start);
            paused.Add(Sample(Start + 900_000, 1), Start + 900_000);
            Equal(SnapshotInterpolationBuffer.MaximumDelayMicros, paused.TargetDelayMicros, "clamped");
        }

        private static void UnevenCadenceNeverRunsPastTheNewestSample()
        {
            // Ten equal bodies share a 400-byte budget, four 100-byte updates a tick at 10 Hz, so each one is sent every
            // 200 or 300 ms. A delay of one mean interval (about 250 ms) used to run past the newest sample on every
            // longer gap: 180 of 1,800 frames extrapolated, by up to 50 ms.
            foreach (var (budget, expectedDelay, worstAhead) in new[] { (400, 300_000L, 0L), (300, 350_000L, 50_000L) })
            {
                var sends = ScheduledSends(budget, 10, 100, 400);
                var buffer = new SnapshotInterpolationBuffer();
                var playback = new PlaybackRate();
                var next = 0;
                var frames = 0;
                var extrapolating = 0;
                var ahead = 0L;
                for (var now = Start; frames < 300 + 1_800; now += 16_667)
                {
                    while (next < sends.Count && sends[next] + Transit <= now)
                    {
                        buffer.Add(Sample(sends[next], sends[next] / (float)Step), sends[next] + Transit);
                        next++;
                    }

                    if (!playback.Sample(buffer, now, out var result)) continue;
                    if (++frames <= 300) continue;
                    if (result.Mode != InterpolationMode.Extrapolating && result.Mode != InterpolationMode.ExtrapolationCapped) continue;
                    extrapolating++;
                    ahead = Math.Max(ahead, result.RenderTimeMicros - buffer.GetSample(buffer.Count - 1).ServerTimeMicros);
                }

                var name = budget + "-byte budget";
                Equal(expectedDelay, buffer.TargetDelayMicros, name + ": the delay covers the longest gap, up to the cap");
                Equal(expectedDelay, buffer.CurrentDelayMicros, name + ": and playback settled on it");
                True(ahead <= worstAhead, name + ": extrapolated " + ahead + " us past the newest sample");
                if (worstAhead == 0) Equal(0, extrapolating, name + ": no frame extrapolates");
            }
        }

        private static void OrdersDuplicatesAndStaleSamples()
        {
            var buffer = new SnapshotInterpolationBuffer();
            Equal(SnapshotSampleResult.Accepted, buffer.Add(Sample(Start + Step, 1), Start + Step + Transit), "newer first");
            Equal(SnapshotSampleResult.Accepted, buffer.Add(Sample(Start, 0), Start + Step + Transit + 1_000), "older second");
            Equal(Start, buffer.GetSample(0).ServerTimeMicros, "out-of-order samples are sorted");
            Equal(SnapshotSampleResult.Duplicate, buffer.Add(Sample(Start, 0), Start + Step + Transit + 2_000), "duplicate");
            Equal(2, buffer.Count, "the duplicate was not stored");
            buffer.Add(Sample(Start + 2 * Step, 2), Start + 2 * Step + Transit);
            buffer.Add(Sample(Start + 3 * Step, 3), Start + 3 * Step + Transit);

            // Render between the second and third samples: the second becomes the consumed lower bracket.
            True(buffer.TrySample(Start + 3 * Step + Transit + 1_000, out var shown), "render");
            True(shown.RenderTimeMicros > Start + Step && shown.RenderTimeMicros < Start + 2 * Step, "render inside the second segment");
            Equal(SnapshotSampleResult.Stale, buffer.Add(Sample(Start + Step / 2, 0), Start + 3 * Step + Transit + 2_000), "older than consumed");
            Equal(SnapshotSampleResult.Accepted, buffer.Add(Sample(Start + Step + Step / 2, 1), Start + 3 * Step + Transit + 3_000), "a late sample that still helps");
        }

        private static void FullBufferEvictsTheOldest()
        {
            var buffer = new SnapshotInterpolationBuffer();
            for (var index = 0; index < SnapshotInterpolationBuffer.Capacity + 1; index++)
            {
                buffer.Add(Sample(Start + index * Step, index), Start + index * Step + Transit);
            }

            Equal(SnapshotInterpolationBuffer.Capacity, buffer.Count, "eight samples");
            Equal(Start + Step, buffer.GetSample(0).ServerTimeMicros, "the oldest was evicted");
            Equal(SnapshotSampleResult.Stale, buffer.Add(Sample(Start, 0), Start + 9 * Step + Transit), "older than a full buffer");
            // Eight samples 10 ms apart cover only 70 ms, less than the 80 ms minimum delay. The render starts behind
            // the buffer and holds its oldest sample, and the buffer keeps that sample while the render needs it: the
            // delay holds at 80 ms and the render clock never jumps forward to stay inside the buffer.
            var dense = new SnapshotInterpolationBuffer();
            for (var index = 0; index < SnapshotInterpolationBuffer.Capacity; index++)
            {
                dense.Add(Sample(Start + index * 10_000, index), Start + index * 10_000);
            }

            var playback = new PlaybackRate();
            True(playback.Sample(dense, Start + 70_000, out var held), "render a dense buffer");
            Equal(Start - 10_000, held.RenderTimeMicros, "the render keeps the 80 ms delay");
            Equal(InterpolationMode.Holding, held.Mode, "and holds the oldest sample until it gets there");
            for (var index = SnapshotInterpolationBuffer.Capacity; index < 60; index++)
            {
                var now = Start + index * 10_000;
                dense.Add(Sample(now, index), now);
                True(playback.Sample(dense, now, out var frame), "render while the dense stream goes on");
                Equal(now - 80_000, frame.RenderTimeMicros, "every frame keeps the delay");
                True(frame.Mode == InterpolationMode.Interpolating || frame.Mode == InterpolationMode.Holding, "inside the buffer");
            }

            Equal(80_000L, dense.CurrentDelayMicros, "the delay held");
            True(dense.GetSample(0).ServerTimeMicros <= Start + 510_000, "the buffer kept the sample the render needed");
        }

        private static void AFullBufferKeepsTheSegmentOnScreen()
        {
            // Positions on a curve, x = index squared, so dropping either sample the render is between would move it.
            var buffer = new SnapshotInterpolationBuffer();
            for (var index = 0; index < SnapshotInterpolationBuffer.Capacity; index++)
            {
                buffer.Add(Sample(Start + index * Step, index * index), Start + index * Step + Transit);
            }

            True(buffer.TrySample(Start + 200_000, out var before), "render between the oldest two samples");
            Equal(Start + 50_000, before.RenderTimeMicros, "a quarter-way render");
            Near(0.5, before.Position.X, 1e-6, "halfway from 0 to 1");
            buffer.Add(Sample(Start + 8 * Step, 64), Start + 8 * Step + Transit);
            Equal(SnapshotInterpolationBuffer.Capacity, buffer.Count, "still full");
            Equal(Start, buffer.GetSample(0).ServerTimeMicros, "the oldest sample is still on screen");
            Equal(Start + Step, buffer.GetSample(1).ServerTimeMicros, "and so is the next");
            Equal(Start + 3 * Step, buffer.GetSample(2).ServerTimeMicros, "the oldest sample outside the segment went");
            True(buffer.TrySample(Start + 200_000, out var after), "render again at the same moment");
            Near(0.5, after.Position.X, 1e-6, "dropping a sample did not move the replica");

            // Once the render has passed the second sample, the oldest goes as usual.
            True(buffer.TrySample(Start + 300_000, out var later) && later.RenderTimeMicros == Start + 150_000, "render on");
            buffer.Add(Sample(Start + 9 * Step, 81), Start + 9 * Step + Transit);
            Equal(Start + Step, buffer.GetSample(0).ServerTimeMicros, "the oldest is dropped once passed");

            // Holding the oldest sample keeps it, and the one after it goes instead.
            var holding = new SnapshotInterpolationBuffer();
            for (var index = 0; index < SnapshotInterpolationBuffer.Capacity; index++)
            {
                holding.Add(Sample(Start + index * Step, index), Start + index * Step + Transit);
            }

            True(holding.TrySample(Start + 100_000, out var held) && held.Mode == InterpolationMode.Holding, "render before the oldest sample");
            holding.Add(Sample(Start + 8 * Step, 8), Start + 8 * Step + Transit);
            Equal(Start, holding.GetSample(0).ServerTimeMicros, "the held sample stays");
            Equal(Start + 2 * Step, holding.GetSample(1).ServerTimeMicros, "the next one went");

            // The gap a dropped sample leaves says nothing about the cadence, so it does not raise the delay, even
            // after a late sample lands in front of it and moves it along. That sample fills the buffer again, so the
            // sample after the held one goes too.
            Equal(Step, holding.LargestGapMicros, "the dropped sample's gap does not count");
            Equal(SnapshotSampleResult.Accepted, holding.Add(Sample(Start + 25_000, 0.25f), Start + 8 * Step + Transit + 1_000), "a late sample");
            True(holding.GetSample(1).ServerTimeMicros == Start + 25_000 && holding.GetSample(2).ServerTimeMicros == Start + 3 * Step,
                "lands in front of that gap");
            Equal(Step, holding.LargestGapMicros, "which still does not count");
        }

        private static void ExtrapolatesUpToTheCap()
        {
            foreach (var withVelocity in new[] { true, false })
            {
                var buffer = new SnapshotInterpolationBuffer();
                for (var index = 0; index < 10; index++)
                {
                    buffer.Add(Moving(index, withVelocity), Start + index * Step + Transit);
                    buffer.TrySample(Start + index * Step + Transit, out _);
                }

                // The render time is now - transit - delay; the newest sample is at Start + 9 steps.
                var newest = Start + 9 * Step;
                Expect(buffer, newest + 100_000, InterpolationMode.Extrapolating, 10.0f, withVelocity);
                Expect(buffer, newest + SnapshotInterpolationBuffer.ExtrapolationLimitMicros, InterpolationMode.Extrapolating, 11.5f, withVelocity);
                Expect(buffer, newest + SnapshotInterpolationBuffer.ExtrapolationLimitMicros + 1, InterpolationMode.ExtrapolationCapped, 11.5f, withVelocity);
                Expect(buffer, newest + 5_000_000, InterpolationMode.ExtrapolationCapped, 11.5f, withVelocity);
            }

            static void Expect(SnapshotInterpolationBuffer buffer, long render, InterpolationMode mode, float x, bool withVelocity)
            {
                True(buffer.TrySample(render + Transit + Step, out var result), "extrapolate");
                Equal(render, result.RenderTimeMicros, "render time");
                Equal(mode, result.Mode, "mode at " + (render - Start) + " (velocity " + withVelocity + ")");
                Near(x, result.Position.X, 1e-4, "extrapolated position (velocity " + withVelocity + ")");
            }
        }

        private static void HoldsAndResetsAtTheEdges()
        {
            var buffer = new SnapshotInterpolationBuffer();
            True(!buffer.TrySample(Start, out var empty) && empty.Mode == InterpolationMode.Empty, "empty");
            buffer.Add(Sample(Start, 4), Start + Transit);
            True(buffer.TrySample(Start + Transit, out var held), "one sample");
            Equal(InterpolationMode.Holding, held.Mode, "before the first sample the replica holds it");
            Near(4.0, held.Position.X, 0.0, "held position");

            buffer.Add(Sample(Start + Step, 5), Start + Step + Transit);

            // A local clock that steps back holds the render time rather than rewinding it, and the playback offset
            // follows, so playback runs on from there instead of waiting for the clock to catch up.
            True(buffer.TrySample(Start + Step + Transit + 40_000, out var before), "render");
            True(buffer.TrySample(Start + Step + Transit, out var steppedBack), "render with the clock stepped back");
            Equal(before.RenderTimeMicros, steppedBack.RenderTimeMicros, "the render time never rewinds");
            True(buffer.TrySample(Start + Step + Transit + 10_000, out var runOn), "render as the clock runs on");
            Equal(before.RenderTimeMicros + 10_000, runOn.RenderTimeMicros, "playback goes on from where it was held");
            Equal(SnapshotSampleResult.Reset, buffer.Add(Sample(Start + 6_000_000, 9), Start + 2 * Step + Transit), "a jump ahead resets");
            Equal(1, buffer.Count, "the reset keeps only the new sample");
            Equal(SnapshotSampleResult.Stale, buffer.Add(Sample(Start + 3_000_000, 8), Start + 3 * Step + Transit), "a jump behind is stale");
            buffer.Reset();
            Equal(0, buffer.Count, "reset");
            Equal(SnapshotInterpolationBuffer.DefaultNominalIntervalMicros, buffer.IntervalMicros, "reset restores the nominal interval");
        }

        private static void InterpolatesRotationsAlongTheShortArc()
        {
            var quarterTurn = new NetQuaternion(0f, 0.70710677f, 0f, 0.70710677f);
            var negatedQuarterTurn = new NetQuaternion(0f, -0.70710677f, 0f, -0.70710677f);
            foreach (var target in new[] { quarterTurn, negatedQuarterTurn })
            {
                var buffer = new SnapshotInterpolationBuffer();
                buffer.Add(new NetTransformSample(Start, NetVector3.Zero, NetQuaternion.Identity), Start);
                buffer.Add(new NetTransformSample(Start + Step, NetVector3.Zero, target), Start + Step);
                buffer.TrySample(Start + Step, out _);
                True(buffer.TrySample(Start + Step + Step / 2, out var middle), "midpoint");
                Equal(Start + Step / 2, middle.RenderTimeMicros, "render at the midpoint");
                Near(0.0, MultiplayerQuantizationTests.AngleDegrees(new NetQuaternion(0f, 0.38268343f, 0f, 0.9238795f), middle.Rotation), 1e-3, "45 degrees about Y");
            }
        }

        private static void ValidatesInputs()
        {
            Throws<ArgumentOutOfRangeException>(() => new SnapshotInterpolationBuffer(999), "nominal interval too small");
            Throws<ArgumentOutOfRangeException>(() => new SnapshotInterpolationBuffer(1_000_001), "nominal interval too large");
            var buffer = new SnapshotInterpolationBuffer();
            Throws<ArgumentOutOfRangeException>(() => buffer.Add(Sample(0, 0), -1), "negative arrival");
            Throws<ArgumentOutOfRangeException>(() => buffer.TrySample(SnapshotInterpolationBuffer.MaximumTimeMicros, out _), "time too large");
            Throws<ArgumentOutOfRangeException>(() => new NetTransformSample(-1, NetVector3.Zero, NetQuaternion.Identity), "negative server time");
            Throws<ArgumentOutOfRangeException>(() => buffer.GetSample(0), "no samples");
        }

        /// <summary>
        /// Streams 200 samples with extra transit uniform on [0, maximum), then keeps rendering for two seconds without
        /// new samples, so the playback offset settles on the final estimates.
        /// </summary>
        private static SnapshotInterpolationBuffer Jittered(ulong seed, long maximumExtraTransit, out int extrapolatingFrames)
        {
            var random = new SeededRandom(seed);
            var buffer = new SnapshotInterpolationBuffer();
            var arrivals = new long[200];
            for (var index = 0; index < arrivals.Length; index++)
            {
                arrivals[index] = Start + index * Step + Transit + (long)random.NextDouble(0, maximumExtraTransit);
            }

            extrapolatingFrames = 0;
            var playback = new PlaybackRate();
            var end = Start + 19_000_000;
            for (var now = Start; now < end + 2_000_000; now += 16_667)
            {
                for (var index = 0; index < arrivals.Length && now < end; index++)
                {
                    if (arrivals[index] > now - 16_667 && arrivals[index] <= now) buffer.Add(Moving(index, true), arrivals[index]);
                }

                if (!playback.Sample(buffer, now, out var result)) continue;
                if (now > Start + 5_000_000 && now < end && result.Mode != InterpolationMode.Interpolating) extrapolatingFrames++;
            }

            return buffer;
        }

        /// <summary>Sends of entity 1 when equal entities share one client's budget, one tick every 100 ms.</summary>
        private static List<long> ScheduledSends(int budget, int entities, int sizeBytes, int ticks)
        {
            var scheduler = new ReplicationScheduler(budget);
            var candidates = new List<ReplicationCandidate>();
            for (uint id = 1; id <= entities; id++) candidates.Add(new ReplicationCandidate(new NetworkEntityId(id), 1, sizeBytes, false));
            var selected = new List<NetworkEntityId>();
            var sends = new List<long>();
            for (var tick = 0; tick < ticks; tick++)
            {
                scheduler.Schedule(candidates, selected);
                if (selected.Contains(new NetworkEntityId(1))) sends.Add(Start + tick * Step);
            }

            return sends;
        }

        private static NetTransformSample Moving(int index, bool withVelocity) =>
            withVelocity
                ? new NetTransformSample(Start + index * Step, new NetVector3(index, 0f, 0f), NetQuaternion.Identity, new NetVector3(10f, 0f, 0f))
                : Sample(Start + index * Step, index);

        private static NetTransformSample Sample(long serverTime, float x) =>
            new NetTransformSample(serverTime, new NetVector3(x, 0f, 0f), NetQuaternion.Identity);
    }
}
