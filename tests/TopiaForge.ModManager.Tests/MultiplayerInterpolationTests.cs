using System;
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
            OrdersDuplicatesAndStaleSamples();
            FullBufferEvictsTheOldest();
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
            // decay to zero, so the target moves up and all the way back down.
            var random = new SeededRandom(29);
            var arrivals = new long[400];
            for (var index = 0; index < arrivals.Length; index++)
            {
                var rough = index >= 50 && index < 150;
                arrivals[index] = Start + index * Step + Transit + (rough ? (long)random.NextDouble(0, 250_000) : 0);
            }

            var buffer = new SnapshotInterpolationBuffer();
            var lastDelay = -1L;
            var lastNow = 0L;
            var lastRender = long.MinValue;
            var maximumDelay = 0L;
            for (var now = Start; now < Start + 40_000_000; now += 16_667)
            {
                for (var index = 0; index < arrivals.Length; index++)
                {
                    if (arrivals[index] > now - 16_667 && arrivals[index] <= now) buffer.Add(Moving(index, true), arrivals[index]);
                }

                if (!buffer.TrySample(now, out var result)) continue;
                maximumDelay = Math.Max(maximumDelay, buffer.CurrentDelayMicros);
                if (lastDelay >= 0)
                {
                    var allowed = (now - lastNow) * SnapshotInterpolationBuffer.DelaySlewPercent / 100;
                    True(Math.Abs(buffer.CurrentDelayMicros - lastDelay) <= allowed, "the delay slews at most 10% of elapsed time");
                }

                True(result.RenderTimeMicros >= lastRender, "render time never rewinds");
                True(buffer.CurrentDelayMicros >= SnapshotInterpolationBuffer.MinimumDelayMicros &&
                     buffer.CurrentDelayMicros <= SnapshotInterpolationBuffer.MaximumDelayMicros, "delay stays in range");
                lastRender = result.RenderTimeMicros;
                lastDelay = buffer.CurrentDelayMicros;
                lastNow = now;
            }

            True(maximumDelay > 200_000, "the rough phase raised the delay to " + maximumDelay);
            Equal(Step, buffer.CurrentDelayMicros, "the calm phase brought it back to one interval");
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
            // Eight samples 10 ms apart cover only 70 ms, less than the 80 ms minimum delay. The render time is held
            // at the oldest sample instead of falling behind the whole buffer.
            var dense = new SnapshotInterpolationBuffer();
            for (var index = 0; index < SnapshotInterpolationBuffer.Capacity; index++)
            {
                dense.Add(Sample(Start + index * 10_000, index), Start + index * 10_000);
            }

            True(dense.TrySample(Start + 70_000, out var clamped), "render a dense buffer");
            Equal(Start, clamped.RenderTimeMicros, "a full buffer never renders behind its oldest sample");
            Equal(InterpolationMode.Interpolating, clamped.Mode, "it interpolates from the oldest sample");
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
            var lastRender = long.MinValue;
            for (var now = Start; now < Start + 19_000_000; now += 16_667)
            {
                for (var index = 0; index < arrivals.Length; index++)
                {
                    if (arrivals[index] > now - 16_667 && arrivals[index] <= now) buffer.Add(Moving(index, true), arrivals[index]);
                }

                if (!buffer.TrySample(now, out var result)) continue;
                True(result.RenderTimeMicros >= lastRender, "render time never rewinds under jitter");
                lastRender = result.RenderTimeMicros;
                if (now > Start + 5_000_000 && result.Mode != InterpolationMode.Interpolating) extrapolatingFrames++;
            }

            return buffer;
        }

        private static NetTransformSample Moving(int index, bool withVelocity) =>
            withVelocity
                ? new NetTransformSample(Start + index * Step, new NetVector3(index, 0f, 0f), NetQuaternion.Identity, new NetVector3(10f, 0f, 0f))
                : Sample(Start + index * Step, index);

        private static NetTransformSample Sample(long serverTime, float x) =>
            new NetTransformSample(serverTime, new NetVector3(x, 0f, 0f), NetQuaternion.Identity);
    }
}
